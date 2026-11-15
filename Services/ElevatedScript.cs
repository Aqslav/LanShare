using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LanShare.Services;

public sealed record ElevatedResult(bool Cancelled, int ExitCode);

/// <summary>
/// Запускає вбудований PowerShell-скрипт з правами адміністратора (запит UAC).
/// Сам застосунок працює без підвищених прав. Журнал скрипта потрапляє лише у Debug.
/// </summary>
public static class ElevatedScript
{
    private const int ErrorCancelled = 1223; // користувач відмовився у UAC

    public static async Task<ElevatedResult> RunAsync(string resourceName, string arguments = "", object? payload = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LanShare");
        Directory.CreateDirectory(dir);

        var id = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(dir, id + ".ps1");
        var logPath = Path.Combine(dir, id + ".log");
        var payloadPath = Path.Combine(dir, id + ".json");
        var utf8Bom = new UTF8Encoding(true); // BOM потрібен для кирилиці в PowerShell 5.1

        try
        {
            using (var stream = typeof(ElevatedScript).Assembly.GetManifestResourceStream(resourceName)
                                ?? throw new InvalidOperationException($"Скрипт {resourceName} не знайдено серед ресурсів."))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(), utf8Bom);
            }

            var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -LogPath \"{logPath}\"";
            if (payload != null)
            {
                await File.WriteAllTextAsync(payloadPath, JsonSerializer.Serialize(payload), utf8Bom);
                args += $" -PayloadPath \"{payloadPath}\"";
            }
            if (!string.IsNullOrWhiteSpace(arguments)) args += " " + arguments;

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var process = Process.Start(psi)
                                    ?? throw new InvalidOperationException("Не вдалося запустити PowerShell.");
                await process.WaitForExitAsync();

                if (File.Exists(logPath))
                {
                    foreach (var line in (await File.ReadAllTextAsync(logPath)).Split('\n'))
                    {
                        var text = line.TrimEnd('\r');
                        if (text.Length > 0) Debug.WriteLine($"[{resourceName}] {text}");
                    }
                }
                Debug.WriteLine($"[{resourceName}] Код завершення: {process.ExitCode}");
                return new ElevatedResult(false, process.ExitCode);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                Debug.WriteLine($"[{resourceName}] Скасовано користувачем (UAC).");
                return new ElevatedResult(true, -1);
            }
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(logPath);
            TryDelete(payloadPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* тимчасовий файл, не критично */ }
    }
}
