using FastchessDesktop.Core.Settings;
using FastchessDesktop.ViewModels.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FastchessDesktop.App.Services;

public sealed class DispatcherQueueDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action) => queue.TryEnqueue(() => action());
}

public sealed class AppEnvironment : IAppEnvironment
{
    public string AppDirectory => AppContext.BaseDirectory;

    public string DataDirectory => SettingsStore.DataDirectory;
}

/// <summary>File pickers and content dialogs for an unpackaged WinUI 3 window.</summary>
public sealed class WinUiDialogService(Window window) : IDialogService
{
    private bool _dialogOpen;

    public async Task<string?> PickOpenFileAsync(IReadOnlyList<FileFilter> filters)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        foreach (var ext in filters.SelectMany(f => f.Extensions).Distinct()) picker.FileTypeFilter.Add(ext);
        if (!picker.FileTypeFilter.Contains("*")) picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileFilter> filters)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        foreach (var f in filters)
        {
            var extensions = f.Extensions.Where(e => e != "*").ToList();
            if (extensions.Count > 0) picker.FileTypeChoices.Add(f.Description, extensions);
        }
        if (picker.FileTypeChoices.Count == 0) picker.FileTypeChoices.Add("File", [Path.GetExtension(suggestedName)]);
        InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = CreateDialog(title, message);
        dialog.PrimaryButtonText = confirmText;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message);
        dialog.CloseButtonText = "OK";
        await ShowAsync(dialog);
    }

    private ContentDialog CreateDialog(string title, string message) => new()
    {
        Title = title,
        Content = new ScrollViewer
        {
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            MaxHeight = 400,
        },
        XamlRoot = window.Content.XamlRoot,
        RequestedTheme = ElementTheme.Dark,
    };

    // Only one ContentDialog may be open at a time; a second one would throw.
    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        while (_dialogOpen) await Task.Delay(100);
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void InitializeWithWindow(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
}
