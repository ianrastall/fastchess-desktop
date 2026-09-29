using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FastchessDesktop.App.Services;

/// <summary>
/// Engine list colors by rating tier (EngineViewModel.TierOf). The brushes are RatingTier0Brush to
/// RatingTier6Brush in App.xaml, which the legend on the Tournament page uses as well. Used through
/// x:Bind function binding.
/// </summary>
public static class TierColors
{
    public static Brush For(int tier) => (Brush)Application.Current.Resources[$"RatingTier{Math.Clamp(tier, 0, 6)}Brush"];
}
