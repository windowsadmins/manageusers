using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ManageUsers.App.ViewModels;
using ManageUsers.Models;
using ManageUsers.Services;

namespace ManageUsers.App.Views;

public sealed partial class RunPage : Page
{
    private readonly RunViewModel _vm;

    public RunPage()
    {
        InitializeComponent();

        _vm = new RunViewModel(DispatcherQueue.GetForCurrentThread());
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.OutputLines.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                ConsoleOutput.Blocks.Clear();
            else if (e.NewItems != null)
                foreach (RunViewModel.OutputLine line in e.NewItems)
                    AddConsoleLine(line);
            ScrollToBottom();
        };
        PlanList.ItemsSource = _vm.PlanItems;
        PlanCaption.Text = _vm.PlanCaption;
        if (_vm.IsElevated)
            ElevationNote.Text = "Running as administrator.";
    }

    // ── Button Handlers ─────────────────────────────────────────

    private async void SimulateButton_Click(object sender, RoutedEventArgs e) => await _vm.SimulateAsync();

    private async void LiveButton_Click(object sender, RoutedEventArgs e) => await _vm.LiveCleanupAsync(ConfirmAsync);

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _vm.Clear();

    /// <summary>
    /// Lists exactly what the live run will remove. The run is limited to these names, so
    /// anything that became due after the simulation waits for the next run.
    /// </summary>
    private async Task<bool> ConfirmAsync(IReadOnlyList<PlanItem> items)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var item in items)
        {
            var row = new StackPanel { Spacing = 0 };
            row.Children.Add(new TextBlock
            {
                Text = $"{item.Name}  ({KindText(item.Kind)})",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Reason,
                TextWrapping = TextWrapping.WrapWholeWords,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            list.Children.Add(row);
        }

        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = "These accounts and profiles, and everything in them, will be permanently deleted from this device:",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        content.Children.Add(new TextBlock
        {
            Text = "Nothing else is deleted, even if more becomes due before the run finishes. The run also clears recycle bins and scheduled tasks left behind by accounts that no longer exist.",
            TextWrapping = TextWrapping.WrapWholeWords,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = items.Count == 1 ? "Delete 1 item?" : $"Delete {items.Count} items?",
            Content = content,
            PrimaryButtonText = items.Count == 1 ? "Delete 1 item" : $"Delete {items.Count} items",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string KindText(string kind) => kind switch
    {
        PlanItem.Orphan => "account with no profile",
        PlanItem.Profile => "profile with no account",
        _ => "account and profile"
    };

    // ── UI State Sync ────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunViewModel.IsRunning):
            case nameof(RunViewModel.Phase):
                UpdateRunningState();
                break;
            case nameof(RunViewModel.Result):
                UpdateResultBanner();
                break;
            case nameof(RunViewModel.PlanCaption):
                PlanCaption.Text = _vm.PlanCaption;
                break;
        }
    }

    private void UpdateRunningState()
    {
        SimulateButton.IsEnabled = !_vm.IsRunning;
        LiveButton.IsEnabled = !_vm.IsRunning;
        RunningProgress.IsActive = _vm.IsRunning;
        RunningLabel.Text = _vm.Phase + "...";
        RunningLabel.Visibility = _vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.Visibility = _vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = !_vm.IsRunning && _vm.OutputLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.IsRunning) ResultBanner.IsOpen = false;
    }

    private void UpdateResultBanner()
    {
        if (_vm.Result == RunViewModel.ResultKind.None)
        {
            ResultBanner.IsOpen = false;
            return;
        }
        ResultBanner.Severity = _vm.Result switch
        {
            RunViewModel.ResultKind.Success => InfoBarSeverity.Success,
            RunViewModel.ResultKind.Warning => InfoBarSeverity.Warning,
            RunViewModel.ResultKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };
        ResultBanner.Title = _vm.ResultTitle;
        ResultBanner.Message = _vm.ResultMessage;
        ResultBanner.IsOpen = true;
    }

    private void AddConsoleLine(RunViewModel.OutputLine line)
    {
        var paragraph = new Paragraph { Foreground = LogBrushes.For(line.Level), Margin = new Thickness(0, 1, 0, 1) };
        paragraph.Inlines.Add(new Run { Text = line.Text });
        ConsoleOutput.Blocks.Add(paragraph);
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
            ConsoleScroller.ChangeView(null, ConsoleScroller.ScrollableHeight, null));
    }
}

/// <summary>Line colours shared by the Run and Logs tabs.</summary>
internal static class LogBrushes
{
    public static Brush For(LogLineLevel level) => level switch
    {
        LogLineLevel.Error   => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        LogLineLevel.Warning => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        LogLineLevel.Audit   => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
        LogLineLevel.Debug   => new SolidColorBrush(Windows.UI.Color.FromArgb(204, 128, 128, 128)),
        LogLineLevel.Header  => new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
        _ => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
    };
}
