namespace LanShare;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "LAN Share",
            Width = 760,
            Height = 820
        };
    }
}
