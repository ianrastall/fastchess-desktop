using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FastchessDesktop.App.Services;

/// <summary>
/// Engine list colors by rating tier (EngineViewModel.TierOf). Used through x:Bind function binding,
/// so it must not throw: plain brushes, as in LogColors, rather than a resource lookup. The legend
/// uses RatingTier0Brush to RatingTier6Brush in App.xaml, which have the same colors.
/// </summary>
public static class TierColors
{
    private static readonly SolidColorBrush[] Brushes =
    [
        new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)), // not rated
        new(ColorHelper.FromArgb(0xFF, 0xC5, 0x8A, 0xF9)), // 3700 and above
        new(ColorHelper.FromArgb(0xFF, 0x8A, 0xB4, 0xF8)), // 3500
        new(ColorHelper.FromArgb(0xFF, 0x78, 0xD9, 0xEC)), // 3300
        new(ColorHelper.FromArgb(0xFF, 0x81, 0xC9, 0x95)), // 3100
        new(ColorHelper.FromArgb(0xFF, 0xE8, 0xC0, 0x6A)), // 2900
        new(ColorHelper.FromArgb(0xFF, 0xF6, 0xAE, 0x7C)), // below 2900
    ];

    public static SolidColorBrush For(int tier) => Brushes[Math.Clamp(tier, 0, Brushes.Length - 1)];
}
