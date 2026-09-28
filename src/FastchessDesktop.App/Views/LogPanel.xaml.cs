using FastchessDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FastchessDesktop.App.Views;

/// <summary>Shows a <see cref="LogViewModel"/> and follows new output unless paused.</summary>
public sealed partial class LogPanel : UserControl
{
    public static readonly DependencyProperty LogProperty = DependencyProperty.Register(
        nameof(Log), typeof(LogViewModel), typeof(LogPanel), new PropertyMetadata(null, OnLogChanged));

    public LogPanel() => InitializeComponent();

    public LogViewModel? Log
    {
        get => (LogViewModel?)GetValue(LogProperty);
        set => SetValue(LogProperty, value);
    }

    private static void OnLogChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (LogPanel)d;
        if (e.OldValue is LogViewModel oldLog) oldLog.LinesAppended -= panel.OnLinesAppended;
        if (e.NewValue is LogViewModel newLog)
        {
            newLog.LinesAppended += panel.OnLinesAppended;
            panel.Lines.ItemsSource = newLog.Lines;
        }
    }

    private void OnLinesAppended(object? sender, EventArgs e)
    {
        if (AutoScroll.IsChecked == true && Log is { Lines.Count: > 0 } log)
            Lines.ScrollIntoView(log.Lines[^1]);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (Log is null) return;
        var package = new DataPackage();
        package.SetText(Log.AllText);
        Clipboard.SetContent(package);
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => Log?.ClearCommand.Execute(null);
}
