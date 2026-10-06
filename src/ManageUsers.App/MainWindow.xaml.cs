using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ManageUsers.Services;
using ManageUsers.App.Views;

namespace ManageUsers.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "Managed Users Cleanup";

        // DPI-aware sizing clamped to available screen work area
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        int targetW = (int)(1280 * scale);
        int targetH = (int)(1060 * scale);
        int maxW = (int)(workArea.Width * 0.96);
        int maxH = (int)(workArea.Height * 0.96);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min(targetW, maxW),
            Math.Min(targetH, maxH)));

        AppWindow.SetIcon(System.IO.Path.Combine(
            AppContext.BaseDirectory, "Assets", "ManageUsers.ico"));

        // Extend content into title bar for seamless theme-matching appearance
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SystemBackdrop = new MicaBackdrop();

        // The elevated relaunch from Unlock passes --prefs. Prefs is also the first tab,
        // so it is the default either way.
        NavView.SelectedItem = PrefsElevation.OpensOnPrefs(Environment.GetCommandLineArgs().Skip(1))
            ? NavView.MenuItems.OfType<NavigationViewItem>().First(i => (string?)i.Tag == "prefs")
            : NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            var pageType = item.Tag?.ToString() switch
            {
                "prefs" => typeof(PrefsPage),
                "run"   => typeof(RunPage),
                "logs"  => typeof(LogsPage),
                _       => typeof(PrefsPage)
            };
            ContentFrame.Navigate(pageType);
        }
    }
}
