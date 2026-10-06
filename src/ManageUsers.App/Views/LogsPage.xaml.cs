using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using ManageUsers.App.ViewModels;

namespace ManageUsers.App.Views;

public sealed partial class LogsPage : Page
{
    private readonly LogsViewModel _vm = new();

    public LogsPage()
    {
        InitializeComponent();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        LogFileList.ItemsSource = _vm.LogFiles;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A run on the Run tab may have added a day or lines since this tab was last shown.
        _vm.Refresh();
        LogFileList.SelectedItem = _vm.SelectedLog;
        _vm.Reload();
    }

    // ── Event Handlers ───────────────────────────────────────────

    private void LogFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogFileList.SelectedItem is LogFile file)
            _vm.SelectedLog = file;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        => _vm.FilterText = FilterBox.Text;

    private void OpenEditor_Click(object sender, RoutedEventArgs e) => _vm.OpenInEditor();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenFolder();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _vm.Refresh();
        LogFileList.SelectedItem = _vm.SelectedLog;
        _vm.Reload();
    }

    // ── UI State ─────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.FilteredLines))
            UpdateLogContent();
    }

    private void UpdateLogContent()
    {
        EmptyState.Visibility = _vm.SelectedLog is null ? Visibility.Visible : Visibility.Collapsed;
        OpenEditorBtn.IsEnabled = _vm.SelectedLog is not null;

        LogOutput.Blocks.Clear();
        foreach (var line in _vm.FilteredLines)
        {
            var paragraph = new Paragraph { Foreground = LogBrushes.For(line.Level), Margin = new Thickness(0, 1, 0, 1) };
            paragraph.Inlines.Add(new Run { Text = line.Text });
            LogOutput.Blocks.Add(paragraph);
        }
        DispatcherQueue.TryEnqueue(() => LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null));
    }
}
