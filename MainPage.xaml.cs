using System.Collections.ObjectModel;
using System.Diagnostics;
using LanShare.Models;
using LanShare.Services;

namespace LanShare;

public partial class MainPage : ContentPage
{
    private readonly ObservableCollection<NetworkComputer> _computers = new();
    private readonly ObservableCollection<LocalShare> _myShares = new();
    private CancellationTokenSource? _scanCts;
    private bool _firstAppearance = true;

    public MainPage()
    {
        InitializeComponent();

        BindableLayout.SetItemsSource(ComputersLayout, _computers);
        BindableLayout.SetItemsSource(MySharesLayout, _myShares);

        VersionLabel.Text = $"Збірка {BuildInfo.Version}";
        SetTab(network: true);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_firstAppearance) return;
        _firstAppearance = false;

        _ = LoadMySharesAsync();
        await ScanAsync();
    }

    // ---------- Вкладки ----------

    private void OnTabNetworkClicked(object? sender, EventArgs e) => SetTab(network: true);

    private void OnTabMyPcClicked(object? sender, EventArgs e)
    {
        SetTab(network: false);
        _ = LoadMySharesAsync();
    }

    private void SetTab(bool network)
    {
        NetworkTab.IsVisible = network;
        MyPcTab.IsVisible = !network;
        StyleTab(TabNetworkButton, network);
        StyleTab(TabMyPcButton, !network);
    }

    private static void StyleTab(Button button, bool active)
    {
        if (active)
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty, Color.FromArgb("#2563EB"), Color.FromArgb("#2563EB"));
            button.SetAppThemeColor(Button.TextColorProperty, Colors.White, Colors.White);
        }
        else
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty, Color.FromArgb("#E2E8F0"), Color.FromArgb("#334155"));
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#0F172A"), Color.FromArgb("#E2E8F0"));
        }
    }

    // ---------- Налаштування комп'ютера ----------

    private async void OnSetupClicked(object? sender, EventArgs e)
    {
        SetupButton.IsEnabled = false;
        SetupSpinner.IsVisible = true;
        SetupSpinner.IsRunning = true;
        SetupStatus.IsVisible = true;
        SetupStatus.Text = "Очікування підтвердження адміністратора...";

        try
        {
            var result = await SetupService.RunAsync(GuestSwitch.IsToggled);

            if (result.Cancelled)
            {
                SetupStatus.Text = "Налаштування скасовано: потрібне підтвердження адміністратора.";
                return;
            }

            var ip = NetworkScanner.GetLocalAddress() ?? "невідомо";
            SetupStatus.Text = result.Success
                ? $"Готово! Комп'ютер налаштовано. Ім'я: {Environment.MachineName}, IP: {ip}. Рекомендуємо перезавантажити комп'ютер."
                : "Налаштування завершено з помилками. Перезавантажте комп'ютер і повторіть спробу.";

            _ = ScanAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Setup] Виняток: " + ex);
            SetupStatus.Text = "Помилка налаштування: " + ex.Message;
        }
        finally
        {
            SetupSpinner.IsRunning = false;
            SetupSpinner.IsVisible = false;
            SetupButton.IsEnabled = true;
        }
    }

    // ---------- Спільні папки цього комп'ютера ----------

    private async Task LoadMySharesAsync()
    {
        var shares = await SharingService.GetLocalSharesAsync();

        _myShares.Clear();
        foreach (var share in shares) _myShares.Add(share);
        MySharesEmpty.IsVisible = _myShares.Count == 0;
    }

    private void OnSharePathChanged(object? sender, TextChangedEventArgs e)
    {
        ShareNameEntry.Placeholder = string.IsNullOrWhiteSpace(e.NewTextValue)
            ? "Ім'я в мережі (необов'язково)"
            : $"Ім'я в мережі: {SharingService.SuggestName(e.NewTextValue)}";
    }

    private async void OnBrowseClicked(object? sender, EventArgs e)
    {
        try
        {
            var picked = await FolderPickerService.PickAsync(Window);
            if (!string.IsNullOrEmpty(picked)) SharePathEntry.Text = picked;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Shares] Вибір папки: " + ex);
            ShowShareStatus("Не вдалося відкрити вибір папки. Введіть шлях вручну.");
        }
    }

    private async void OnShareFolderClicked(object? sender, EventArgs e)
    {
        var path = SharePathEntry.Text?.Trim().Trim('"') ?? "";
        if (path.Length == 0)
        {
            ShowShareStatus("Вкажіть папку: введіть шлях або натисніть «Огляд...».");
            return;
        }

        var name = ShareNameEntry.Text?.Trim() ?? "";
        if (name.Length == 0) name = SharingService.SuggestName(path);
        var readOnly = AccessPicker.SelectedIndex == 1;

        ShareButton.IsEnabled = false;
        ShowShareStatus("Очікування підтвердження адміністратора...");
        try
        {
            var result = await SharingService.AddAsync(path, name, readOnly);
            ShowShareStatus(result.Message);

            if (result.Success)
            {
                SharePathEntry.Text = "";
                ShareNameEntry.Text = "";
                await LoadMySharesAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Shares] Виняток: " + ex);
            ShowShareStatus("Помилка: " + ex.Message);
        }
        finally
        {
            ShareButton.IsEnabled = true;
        }
    }

    private async void OnRemoveShareClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not LocalShare share) return;

        var confirmed = await DisplayAlert("Спільний доступ",
            $"Скасувати спільний доступ до «{share.Name}»? Сама папка і файли залишаться на місці.",
            "Скасувати доступ", "Залишити");
        if (!confirmed) return;

        ShowShareStatus("Очікування підтвердження адміністратора...");
        try
        {
            var result = await SharingService.RemoveAsync(share.Name);
            ShowShareStatus(result.Message);
            if (result.Success) await LoadMySharesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Shares] Виняток: " + ex);
            ShowShareStatus("Помилка: " + ex.Message);
        }
    }

    private void ShowShareStatus(string text)
    {
        ShareStatus.Text = text;
        ShareStatus.IsVisible = true;
    }

    // ---------- Пошук комп'ютерів ----------

    private async void OnRefreshClicked(object? sender, EventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        _computers.Clear();
        ScanSpinner.IsVisible = true;
        ScanSpinner.IsRunning = true;
        RefreshButton.IsEnabled = false;

        var localIp = NetworkScanner.GetLocalAddress();
        ScanStatus.Text = localIp == null
            ? "Пошук комп'ютерів у мережі..."
            : $"Пошук комп'ютерів у мережі {localIp[..localIp.LastIndexOf('.')]}.x ...";
        Debug.WriteLine($"[Scan] Локальна адреса: {localIp}");

        try
        {
            var progress = new Progress<NetworkComputer>(c =>
            {
                if (ReferenceEquals(_scanCts, cts)) AddComputer(c);
            });

            await NetworkScanner.ScanAsync(progress, cts.Token);

            ScanStatus.Text = _computers.Count == 0
                ? "Комп'ютерів не знайдено. На іншому комп'ютері має бути виконано «Налаштувати комп'ютер»."
                : $"Знайдено: {_computers.Count}. Натисніть на комп'ютер, щоб побачити його папки.";
        }
        catch (OperationCanceledException)
        {
            // запущено новий пошук
        }
        catch (Exception ex)
        {
            ScanStatus.Text = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                ScanSpinner.IsRunning = false;
                ScanSpinner.IsVisible = false;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void AddComputer(NetworkComputer computer)
    {
        // Тримаємо список відсортованим за останнім числом IP-адреси
        int index = 0;
        while (index < _computers.Count && LastOctet(_computers[index].Address) < LastOctet(computer.Address))
            index++;
        _computers.Insert(index, computer);

        ScanStatus.Text = $"Пошук... знайдено: {_computers.Count}";
    }

    private static int LastOctet(string ip) =>
        int.TryParse(ip[(ip.LastIndexOf('.') + 1)..], out var n) ? n : 0;

    // ---------- Відкриття вікна зі спільними папками ----------

    private void OnComputerTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NetworkComputer computer) return;
        OpenSharesWindow(computer);
    }

    private void OnManualConnect(object? sender, EventArgs e)
    {
        var address = ManualEntry.Text?.Trim().TrimStart('\\');
        if (string.IsNullOrEmpty(address)) return;

        OpenSharesWindow(new NetworkComputer(address, null, false));
    }

    private static void OpenSharesWindow(NetworkComputer computer)
    {
        var window = new Window(new SharesPage(computer))
        {
            Title = $"Спільні папки: {computer.DisplayName}",
            Width = 580,
            Height = 700
        };
        Application.Current?.OpenWindow(window);
    }
}
