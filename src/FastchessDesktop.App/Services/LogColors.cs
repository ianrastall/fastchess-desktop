using FastchessDesktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FastchessDesktop.App.Services;

/// <summary>Log line colors for the dark theme. Used through x:Bind function binding.</summary>
public static class LogColors
{
    private static readonly SolidColorBrush Info = new(ColorHelper.FromArgb(0xFF, 0xD0, 0xD0, 0xD0));
    private static readonly SolidColorBrush Command = new(ColorHelper.FromArgb(0xFF, 0x8A, 0xB4, 0xF8));
    private static readonly SolidColorBrush Output = new(ColorHelper.FromArgb(0xFF, 0xB8, 0xB8, 0xB8));
    private static readonly SolidColorBrush Error = new(ColorHelper.FromArgb(0xFF, 0xF2, 0x8B, 0x82));
    private static readonly SolidColorBrush Success = new(ColorHelper.FromArgb(0xFF, 0x81, 0xC9, 0x95));
    private static readonly SolidColorBrush Warning = new(ColorHelper.FromArgb(0xFF, 0xE8, 0xC0, 0x6A));

    public static SolidColorBrush For(LogKind kind) => kind switch
    {
        LogKind.Command => Command,
        LogKind.Output => Output,
        LogKind.Error => Error,
        LogKind.Success => Success,
        LogKind.Warning => Warning,
        _ => Info,
    };
}
