using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using LanShare.Models;
using LanShare.Services;

namespace LanShare;

public partial class SharesPage : ContentPage
{
    private readonly NetworkComputer _computer;
    private readonly ObservableCollection<ShareItem> _shares = new();
    private string _host;
    private bool _loadedOnce;

    public SharesPage(NetworkComputer computer)
    {
        InitializeComponent();

        _computer = computer;
        _host = computer.Address;
        HeaderLabel.Text = computer.DisplayName;
        AddressLabel.Text = computer.Address;
        BindableLayout.SetItemsSource(SharesLayout, _shares);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loadedOnce) return;
        _loadedOnce = true;
        await LoadAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        _ = CheckRdpAsync();

        _shares.Clear();
        AuthPanel.IsVisible = false;
        Spinner.IsVisible = true;
        Spinner.IsRunning = true;
        RefreshButton.IsEnabled = false;
        StatusLabel.Text = "Завантаження списку папок...";

        try
        {
            // Спершу IP-адреса, потім ім'я комп'ютера (якщо відоме): інколи працює лише один зі шляхів
            var targets = GetTargets();

            List<ShareItem>? list = null;
            Win32Exception? lastError = null;
            foreach (var target in targets)
            {
                try
                {
                    list = await Task.Run(() => ShareBrowser.GetShares(target));
                    _host = target;
                    break;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode is 53 or 55 or 67)
                {
                    lastError = ex;
                }
            }

            if (list == null) throw lastError!;
            foreach (var share in list) _shares.Add(share);

            StatusLabel.Text = list.Count == 0
                ? "На цьому комп'ютері немає відкритих папок."
                : "Оберіть папку, щоб відкрити її у Провіднику.";
        }
        catch (Win32Exception ex)
        {
            StatusLabel.Text = Describe(ex);
            AuthPanel.IsVisible = ex.NativeErrorCode is 5 or 1326 or 53 or 55 or 67;
            if (ex.NativeErrorCode is 53 or 55 or 67 or 1272) _ = RunDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            Spinner.IsRunning = false;
            Spinner.IsVisible = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private static string Describe(Win32Exception ex) => ex.NativeErrorCode switch
    {
        5 or 1326 =>
            "Потрібні логін і пароль облікового запису цього комп'ютера (або увімкніть на ньому «режим без пароля»).",
        1385 =>
            "Той комп'ютер забороняє цьому обліковому запису вхід через мережу. Натисніть «Налаштувати комп'ютер» на ньому.",
        1219 =>
            "Вже є підключення до цього комп'ютера з іншими обліковими даними. Закрийте вікна Провідника або виконайте у командному рядку: net use * /delete",
        1272 =>
            "Windows блокує підключення без пароля (гостьові). Натисніть «Налаштувати комп'ютер» на ЦЬОМУ комп'ютері " +
            "з увімкненим режимом без пароля, а потім повторіть спробу.",
        53 or 55 or 67 =>
            $"Комп'ютер відповідає в мережі, але Windows не змогла встановити з ним SMB-з'єднання (помилка {ex.NativeErrorCode}). " +
            "Це загальна помилка, зазвичай пов'язана з входом. Спробуйте: ввести логін і пароль нижче; " +
            "натиснути «У Провіднику»; або виконати «Налаштувати комп'ютер» на ОБОХ комп'ютерах " +
            "(для режиму без пароля це потрібно і на цьому).",
        _ => $"Помилка {ex.NativeErrorCode}: {ex.Message}"
    };

    private List<string> GetTargets()
    {
        var targets = new List<string> { _computer.Address };
        if (!string.IsNullOrWhiteSpace(_computer.HostName) &&
            !string.Equals(_computer.HostName, _computer.Address, StringComparison.OrdinalIgnoreCase))
            targets.Add(_computer.HostName!);
        return targets;
    }

    private async Task RunDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        var v = Environment.OSVersion.Version;
        sb.AppendLine($"Збірка застосунку: {BuildInfo.Version}");
        sb.AppendLine($"Windows: {v.Major}.{v.Minor}, збірка {v.Build}");
        sb.AppendLine($"Цей ПК: {Environment.MachineName}, IP {NetworkScanner.GetLocalAddress() ?? "невідомо"}");
        sb.AppendLine($"Ціль: {_computer.Address}, ім'я {_computer.HostName ?? "невідоме"}");

        var p445 = await NetworkScanner.IsPortOpenAsync(_computer.Address, 445, 2000, CancellationToken.None);
        sb.AppendLine($"Порт 445 (SMB): {(p445 ? "відкритий" : "закритий")}");

        foreach (var target in GetTargets())
        {
            string result;
            try
            {
                var list = await Task.Run(() => ShareBrowser.GetShares(target));
                result = $"успішно, папок: {list.Count}";
            }
            catch (Win32Exception ex)
            {
                result = $"помилка {ex.NativeErrorCode}: {ex.Message}";
            }
            sb.AppendLine($"Список папок через {target}: {result}");
        }

        const string ws = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters";
        const string pol = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation";
        const string lsa = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa";
        sb.AppendLine("Налаштування цього ПК:");
        sb.AppendLine("  AllowInsecureGuestAuth = " + Reg(ws, "AllowInsecureGuestAuth"));
        sb.AppendLine("  RequireSecuritySignature (клієнт) = " + Reg(ws, "RequireSecuritySignature"));
        sb.AppendLine("  Політика AllowInsecureGuestAuth = " + Reg(pol, "AllowInsecureGuestAuth"));
        sb.AppendLine("  forceguest = " + Reg(lsa, "forceguest"));

        Debug.WriteLine("[Діагностика]\n" + sb.ToString().TrimEnd());
    }

    private static string Reg(string key, string name) =>
        Registry.GetValue(key, name, null)?.ToString() ?? "не задано";

    private async Task CheckRdpAsync()
    {
        RdpLabel.Text = "Віддалений стіл: перевірка порту 3389...";
        var open = await NetworkScanner.IsPortOpenAsync(_computer.Address, 3389, 2000, CancellationToken.None);
        RdpLabel.Text = open
            ? "Віддалений стіл: порт 3389 відкритий, можна підключатися."
            : "Віддалений стіл: порт 3389 закритий. На тому комп'ютері RDP вимкнено, заблоковано брандмауером, або там Windows Home.";
    }

    private static string _lastRdpUser = Environment.UserName;

    private async void OnRdpClicked(object? sender, EventArgs e)
    {
        // Без правильного префікса користувача mstsc підставляє ім'я СВОГО комп'ютера,
        // і сервер відповідає помилкою "The Local Security Authority cannot be contacted".
        var user = await DisplayPromptAsync(
            "Віддалений стіл",
            "Ім'я користувача на тому комп'ютері (без пароля, якщо його немає):",
            accept: "Підключитись",
            cancel: "Скасувати",
            initialValue: _lastRdpUser);

        if (user == null) return;
        user = user.Trim();
        _lastRdpUser = user.Length > 0 ? user : _lastRdpUser;

        try
        {
            var lines = new List<string> { $"full address:s:{_host}" };
            if (user.Length > 0)
            {
                var domain = !string.IsNullOrWhiteSpace(_computer.HostName) ? _computer.HostName : ".";
                var qualified = user.Contains('\\') || user.Contains('@') ? user : $"{domain}\\{user}";
                lines.Add($"username:s:{qualified}");
            }
            lines.Add("prompt for credentials:i:1");

            var dir = Path.Combine(Path.GetTempPath(), "LanShare");
            Directory.CreateDirectory(dir);
            var safeName = string.Concat(_host.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));
            var path = Path.Combine(dir, safeName + ".rdp");
            await File.WriteAllLinesAsync(path, lines);

            Debug.WriteLine($"[RDP] {string.Join(" | ", lines)}");
            Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[RDP] Виняток: " + ex);
            StatusLabel.Text = "Не вдалося запустити віддалений стіл: " + ex.Message;
        }
    }

    private void OnOpenInExplorerClicked(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($@"\\{_host}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Не вдалося запустити Провідник: " + ex.Message;
        }
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        var user = UserEntry.Text?.Trim();
        var password = PasswordEntry.Text ?? "";
        if (string.IsNullOrEmpty(user))
        {
            StatusLabel.Text = "Введіть логін.";
            return;
        }

        try
        {
            await Task.Run(() => ShareBrowser.Authenticate(_host, user, password));
            PasswordEntry.Text = "";
            await LoadAsync();
        }
        catch (Win32Exception ex)
        {
            StatusLabel.Text = Describe(ex);
        }
    }

    private async void OnShareTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not ShareItem share) return;

        // Перш ніж віддавати шлях Провіднику, перевіряємо, що папка справді доступна.
        // Інакше Провідник мовчки відкриває власну папку «Документи».
        StatusLabel.Text = $"Перевірка доступу до {share.UncPath} ...";
        try
        {
            await Task.Run(() =>
            {
                using var entries = Directory.EnumerateFileSystemEntries(share.UncPath).GetEnumerator();
                entries.MoveNext();
            });
        }
        catch (UnauthorizedAccessException)
        {
            StatusLabel.Text =
                $"Немає доступу до {share.UncPath}. Введіть логін і пароль облікового запису того комп'ютера " +
                "або перевірте права доступу до папки.";
            AuthPanel.IsVisible = true;
            return;
        }
        catch (Exception ex)
        {
            var code = Win32Code(ex);
            StatusLabel.Text = $"Не вдалося відкрити {share.UncPath}: {ex.Message}";
            AuthPanel.IsVisible = code is 5 or 1326;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{share.UncPath}\"") { UseShellExecute = false });
            StatusLabel.Text = $"Відкрито: {share.UncPath}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Не вдалося запустити Провідник: " + ex.Message;
        }
    }

    private static int Win32Code(Exception ex) =>
        (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : 0;
}
