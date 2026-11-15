using System.Diagnostics;
using System.Text;
using LanShare.Models;

namespace LanShare.Services;

public sealed record ShareOpResult(bool Cancelled, bool Success, string Message);

/// <summary>Керування спільними папками цього комп'ютера (аналог пунктів 2-4 з bat-файлу).</summary>
public static class SharingService
{
    private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Список спільних дискових папок (без прихованих C$, ADMIN$). Адмін-права не потрібні.</summary>
    public static async Task<List<LocalShare>> GetLocalSharesAsync()
    {
        var result = new List<LocalShare>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                            "Get-CimInstance Win32_Share | Where-Object { $_.Type -eq 0 } | " +
                            "ForEach-Object { $_.Name + '|' + $_.Path }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null) return result;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var separator = line.IndexOf('|');
                if (separator <= 0) continue;
                result.Add(new LocalShare(line[..separator], line[(separator + 1)..]));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Shares] Не вдалося отримати список: " + ex);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public static async Task<ShareOpResult> AddAsync(string path, string name, bool readOnly)
    {
        path = path.Trim().Trim('"');
        if (path.Length > 3) path = path.TrimEnd('\\', '/'); // корінь диска (D:\) залишаємо як є

        if (!Directory.Exists(path))
            return new ShareOpResult(false, false, "Папку не знайдено. Перевірте шлях.");

        name = Sanitize(name);
        if (name.Length == 0)
            return new ShareOpResult(false, false, "Ім'я ресурсу порожнє або містить недозволені символи.");

        var result = await ElevatedScript.RunAsync("share.ps1", payload: new
        {
            Action = "Add",
            Name = name,
            Path = path,
            Access = readOnly ? "Read" : "Full"
        });

        if (result.Cancelled)
            return new ShareOpResult(true, false, "Скасовано: потрібне підтвердження адміністратора.");

        return result.ExitCode switch
        {
            0 => new ShareOpResult(false, true, $"Папку відкрито для мережі: \\\\{Environment.MachineName}\\{name}"),
            2 => new ShareOpResult(false, false, "Папку не знайдено."),
            3 => new ShareOpResult(false, false, "Некоректне ім'я ресурсу."),
            _ => new ShareOpResult(false, false, "Не вдалося створити спільний ресурс. Перевірте шлях та права доступу.")
        };
    }

    public static async Task<ShareOpResult> RemoveAsync(string name)
    {
        var result = await ElevatedScript.RunAsync("share.ps1", payload: new
        {
            Action = "Remove",
            Name = name,
            Path = "",
            Access = ""
        });

        if (result.Cancelled)
            return new ShareOpResult(true, false, "Скасовано: потрібне підтвердження адміністратора.");

        return result.ExitCode == 0
            ? new ShareOpResult(false, true, $"Спільний доступ до «{name}» скасовано.")
            : new ShareOpResult(false, false, "Не вдалося скасувати спільний доступ.");
    }

    /// <summary>Пропонує ім'я ресурсу за назвою папки.</summary>
    public static string SuggestName(string path)
    {
        var trimmed = path.Trim().Trim('"').TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name)) name = trimmed.Replace(":", "");
        return Sanitize(name);
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => !InvalidNameChars.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
