using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ManageUsers.App.ViewModels;

namespace ManageUsers.App.Views;

public sealed partial class PrefsPage : Page
{
    public PrefsViewModel ViewModel { get; } = new();

    public PrefsPage()
    {
        InitializeComponent();

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ManageUsers.png");
        if (System.IO.File.Exists(iconPath))
            AppIcon.Source = new BitmapImage(new Uri(iconPath));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        // Once the elevated copy has started (UAC accepted), this read-only instance closes.
        // A cancelled UAC prompt returns false and the tab simply stays read-only.
        if (ViewModel.TryRelaunchElevated())
            Application.Current.Exit();
    }

    private void ClearMachineSettings_Click(object sender, RoutedEventArgs e) => ViewModel.ClearMachineSettings();

    // ── List editors ─────────────────────────────────────────────

    private void AddExclusion_Click(object sender, RoutedEventArgs e) => ViewModel.AddExclusion();
    private void AddDeletableAdmin_Click(object sender, RoutedEventArgs e) => ViewModel.AddDeletableAdmin();
    private void AddTermDate_Click(object sender, RoutedEventArgs e) => ViewModel.AddTermDate();

    private void AddExclusionBox_KeyDown(object sender, KeyRoutedEventArgs e) => OnEnter(e, ViewModel.AddExclusion);
    private void AddDeletableAdminBox_KeyDown(object sender, KeyRoutedEventArgs e) => OnEnter(e, ViewModel.AddDeletableAdmin);
    private void AddTermDateBox_KeyDown(object sender, KeyRoutedEventArgs e) => OnEnter(e, ViewModel.AddTermDate);

    private static void OnEnter(KeyRoutedEventArgs e, Action add)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        add();
    }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string entry } button) return;
        DependencyObject? node = button;
        while (node != null && node is not ListView)
            node = VisualTreeHelper.GetParent(node);
        ViewModel.ListNamed((node as ListView)?.Tag as string)?.Remove(entry);
    }

    // ── Rules editor ─────────────────────────────────────────────

    private void AddRule_Click(object sender, RoutedEventArgs e) => ViewModel.AddRule();

    private void MoveRuleUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RuleItem rule }) ViewModel.MoveRule(rule, -1);
    }

    private void MoveRuleDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RuleItem rule }) ViewModel.MoveRule(rule, +1);
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RuleItem rule }) ViewModel.Rules.Remove(rule);
    }
}
