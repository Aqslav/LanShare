using System.ComponentModel;
using System.Runtime.InteropServices;
using LanShare.Models;

namespace LanShare.Services;

/// <summary>Отримує список спільних папок віддаленого комп'ютера через Windows API (NetShareEnum).</summary>
public static class ShareBrowser
{
    private const uint StypeMask = 0xFF;
    private const uint StypeDisk = 0;
    private const uint StypeSpecial = 0x80000000;   // адмінресурси C$, ADMIN$, IPC$
    private const uint StypeTemporary = 0x40000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_remark;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string serverName, int level, out IntPtr buffer, int prefMaxLen,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? userName, int flags);

    /// <summary>Повертає спільні дискові папки (без прихованих адмінресурсів).</summary>
    /// <exception cref="Win32Exception">Помилка доступу, входу, мережі тощо.</exception>
    public static List<ShareItem> GetShares(string host)
    {
        var result = new List<ShareItem>();
        int resume = 0;

        int code = NetShareEnum(@"\\" + host, 1, out var buffer, -1, out var read, out _, ref resume);
        if (code != 0) throw new Win32Exception(code);

        try
        {
            int size = Marshal.SizeOf<ShareInfo1>();
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(IntPtr.Add(buffer, i * size));

                if ((info.shi1_type & StypeMask) != StypeDisk) continue;
                if ((info.shi1_type & (StypeSpecial | StypeTemporary)) != 0) continue;
                if (string.IsNullOrEmpty(info.shi1_netname)) continue;

                result.Add(new ShareItem(
                    info.shi1_netname,
                    info.shi1_remark ?? "",
                    $@"\\{host}\{info.shi1_netname}"));
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>Входить на віддалений комп'ютер з логіном і паролем (сесія діє й для Провідника).</summary>
    public static void Authenticate(string host, string userName, string password)
    {
        var resource = new NetResource
        {
            dwType = 0, // RESOURCETYPE_ANY
            lpRemoteName = $@"\\{host}\IPC$"
        };

        int code = WNetAddConnection2(ref resource, password, userName, 0);
        if (code != 0) throw new Win32Exception(code);
    }
}
