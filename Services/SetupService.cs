namespace LanShare.Services;

public sealed record SetupResult(bool Cancelled, bool Success);

/// <summary>Налаштування комп'ютера: запускає вбудований setup.ps1 від імені адміністратора.</summary>
public static class SetupService
{
    public static async Task<SetupResult> RunAsync(bool guestMode)
    {
        var result = await ElevatedScript.RunAsync("setup.ps1", $"-GuestMode {(guestMode ? 1 : 0)}");
        return new SetupResult(result.Cancelled, !result.Cancelled && result.ExitCode == 0);
    }
}
