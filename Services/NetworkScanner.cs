using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LanShare.Models;

namespace LanShare.Services;

/// <summary>
/// Шукає у локальній підмережі комп'ютери з відкритим портом 445 (SMB).
/// Не залежить від "Виявлення мережі" та застарілого net view.
/// </summary>
public static class NetworkScanner
{
    private const int SmbPort = 445;

    /// <summary>
    /// IPv4-адреса активного мережного адаптера зі шлюзом.
    /// Кабельне підключення має перевагу над Wi-Fi, віртуальні адаптери ігноруються.
    /// </summary>
    public static string? GetLocalAddress()
    {
        var candidates = new List<(int Rank, string Address)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (IsVirtual(ni)) continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (!hasGateway) continue;

            var unicast = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (unicast == null) continue;

            int rank = ni.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2
            };
            candidates.Add((rank, unicast.Address.ToString()));
        }

        return candidates.OrderBy(c => c.Rank).Select(c => c.Address).FirstOrDefault();
    }

    private static bool IsVirtual(NetworkInterface ni)
    {
        var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        string[] markers = { "vethernet", "virtual", "vmware", "vbox", "hyper-v", "tap-", "wsl", "bluetooth" };
        return markers.Any(text.Contains);
    }

    /// <summary>Сканує підмережу /24 і повідомляє про кожен знайдений комп'ютер.</summary>
    public static async Task ScanAsync(IProgress<NetworkComputer> found, CancellationToken ct)
    {
        var my = GetLocalAddress()
                 ?? throw new InvalidOperationException("Не вдалося визначити IP-адресу. Перевірте підключення до мережі.");
        var baseAddress = my[..my.LastIndexOf('.')];

        using var gate = new SemaphoreSlim(80);

        var tasks = Enumerable.Range(1, 254).Select(async i =>
        {
            var ip = $"{baseAddress}.{i}";

            bool open;
            await gate.WaitAsync(ct);
            try { open = await IsPortOpenAsync(ip, SmbPort, 1500, ct); }
            finally { gate.Release(); }

            if (!open || ct.IsCancellationRequested) return;

            var name = ip == my ? Environment.MachineName : await ResolveNameAsync(ip, ct);
            found.Report(new NetworkComputer(ip, name, ip == my));
        });

        await Task.WhenAll(tasks);
    }

    public static async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ім'я комп'ютера: спершу NetBIOS (працює у домашніх мережах), потім зворотний DNS.</summary>
    private static async Task<string?> ResolveNameAsync(string ip, CancellationToken ct)
    {
        var name = await NetBiosNameAsync(ip, 1200, ct);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return await DnsNameAsync(ip, ct);
    }

    private static async Task<string?> DnsNameAsync(string ip, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            var finished = await Task.WhenAny(lookup, Task.Delay(2500, ct));
            if (finished != lookup) return null;

            var host = lookup.Result.HostName;
            if (string.IsNullOrWhiteSpace(host) || host == ip) return null;

            var dot = host.IndexOf('.');
            return dot > 0 ? host[..dot] : host;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>NetBIOS Node Status запит (UDP 137): повертає ім'я комп'ютера.</summary>
    private static async Task<string?> NetBiosNameAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var query = new byte[50];
            query[0] = 0x4C; query[1] = 0x53;          // ідентифікатор запиту
            query[5] = 1;                              // одне питання
            query[12] = 0x20;                          // довжина закодованого імені
            query[13] = (byte)'C'; query[14] = (byte)'K'; // "*" у кодуванні NetBIOS
            for (int i = 15; i < 45; i++) query[i] = (byte)'A';
            query[45] = 0x00;
            query[47] = 0x21;                          // тип NBSTAT
            query[49] = 0x01;                          // клас IN

            using var udp = new UdpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await udp.SendAsync(query, query.Length, ip, 137);
            var result = await udp.ReceiveAsync(cts.Token);
            var data = result.Buffer;
            if (data.Length < 57) return null;

            int pos = 12;
            if ((data[pos] & 0xC0) == 0xC0) pos += 2;
            else
            {
                while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1;
                pos++;
            }
            pos += 10; // тип, клас, TTL, довжина
            if (pos >= data.Length) return null;

            int count = data[pos++];
            for (int i = 0; i < count && pos + 18 <= data.Length; i++, pos += 18)
            {
                var name = Encoding.ASCII.GetString(data, pos, 15).Trim();
                byte suffix = data[pos + 15];
                ushort flags = (ushort)((data[pos + 16] << 8) | data[pos + 17]);
                bool isGroup = (flags & 0x8000) != 0;
                if (suffix == 0x00 && !isGroup && name.Length > 0) return name;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
