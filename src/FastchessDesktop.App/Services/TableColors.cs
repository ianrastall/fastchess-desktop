using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FastchessDesktop.App.Services;

/// <summary>Cell colors for the result tables. Used through x:Bind function binding.</summary>
public static class TableColors
{
    private static readonly SolidColorBrush Normal = new(ColorHelper.FromArgb(0xFF, 0xD0, 0xD0, 0xD0));
    private static readonly SolidColorBrush Failure = new(ColorHelper.FromArgb(0xFF, 0xF2, 0x8B, 0x82));

    /// <summary>Game end reasons that mean an engine failed (time loss, crash, stall, illegal move) are red.</summary>
    public static SolidColorBrush Termination(bool isEngineFailure) => isEngineFailure ? Failure : Normal;
}
