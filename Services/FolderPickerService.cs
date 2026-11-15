using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LanShare.Services;

/// <summary>Системний діалог вибору папки (WinUI FolderPicker).</summary>
public static class FolderPickerService
{
    public static async Task<string?> PickAsync(Microsoft.Maui.Controls.Window? window)
    {
        if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            return null;

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(nativeWindow));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
