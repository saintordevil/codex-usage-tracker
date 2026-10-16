using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UsageDock;

// WPF adds leading above BlockLineHeight text. Center the visible single-line glyphs,
// not that asymmetric font box. Native TextBlock layout, accessibility and wrapping remain intact.
internal sealed class OpticalTextBlock : TextBlock
{
    public OpticalTextBlock() { Loaded += (_, _) => AlignInk(); SizeChanged += (_, _) => AlignInk(); }
    static OpticalTextBlock()
    {
        foreach (var property in new[] { TextProperty, FontSizeProperty, FontWeightProperty, FontFamilyProperty, LineHeightProperty })
            property.OverrideMetadata(typeof(OpticalTextBlock), new FrameworkPropertyMetadata(property.GetMetadata(typeof(TextBlock)).DefaultValue,
                (target, _) => { if (target is OpticalTextBlock text && text.IsLoaded) text.QueueAlignment(); }));
    }
    private bool pending;
    private void QueueAlignment()
    {
        if (pending) return; pending = true;
        Dispatcher.BeginInvoke(() => { pending = false; if (IsLoaded) AlignInk(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void AlignInk()
    {
        if (ActualHeight <= 0 || string.IsNullOrEmpty(Text)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var formatted = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, dpi.PixelsPerDip);
        if (Text.Contains('\n') || TextWrapping == TextWrapping.Wrap && formatted.Width > ActualWidth + .5)
        { RenderTransform = Transform.Identity; return; }
        var ink = formatted.BuildGeometry(new Point()).Bounds;
        if (ink.IsEmpty) return;
        var y = (ActualHeight - ink.Height) / 2 - (BaselineOffset - formatted.Baseline + ink.Top);
        // Preserve the font's fractional bounds; rounding here moves short labels
        // to a different raster row at native scale even when their boxes agree.
        RenderTransform = new TranslateTransform(0, y);
    }
}
