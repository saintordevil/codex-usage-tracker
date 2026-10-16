using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsageDock;

// Explicit QA only. Geometry and glyph pixels come from the same unmodified render.
internal static class VisualAudit
{
    internal static void Save(FrameworkElement root, BitmapSource bitmap, string path)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        double sx = bitmap.PixelWidth / root.ActualWidth, sy = bitmap.PixelHeight / root.ActualHeight;
        var rows = new List<object>();
        foreach (var text in Descendants(root).OfType<TextBlock>().Where(x => x.IsVisible && !string.IsNullOrWhiteSpace(x.Text)))
        {
            var rect = Bounds(text); if (rect.IsEmpty || text.Foreground is not SolidColorBrush foreground) continue;
            var control = Ancestors(text).OfType<ButtonBase>().FirstOrDefault();
            FrameworkElement? reference = control;
            string kind = "text";
            if (control is not null) kind = "button";
            else if (VisualTreeHelper.GetParent(text) is Grid grid && grid.Children.OfType<ToggleButton>().FirstOrDefault() is { } toggle)
            { reference = toggle; kind = "switch-caption"; }
            else if (VisualTreeHelper.GetParent(text) is Grid field && field.Children.OfType<Button>().FirstOrDefault() is { } fieldButton)
            { reference = fieldButton; kind = "field-caption"; }
            else if (VisualTreeHelper.GetParent(text) is Grid row) { reference = row; kind = "row-text"; }
            var ink = Ink(rect, foreground.Color);
            var box = reference is null ? rect : Bounds(reference);
            rows.Add(new { text = text.Text, kind, controlId = control is null ? null : System.Windows.Automation.AutomationProperties.GetAutomationId(control), rect = Box(rect), reference = Box(box), ink = ink is { } r ? Box(r) : null,
                font = text.FontFamily.Source, size = text.FontSize, weight = text.FontWeight.ToOpenTypeWeight(), baseline = text.BaselineOffset,
                centerErrorDip = ink is { } area ? (double?)Math.Round((area.Top + area.Bottom - box.Top - box.Bottom) / 2, 3) : null,
                topClearanceDip = ink is { } a ? (double?)Math.Round(a.Top - box.Top, 3) : null,
                bottomClearanceDip = ink is { } b ? (double?)Math.Round(box.Bottom - b.Bottom, 3) : null,
                horizontalCenterErrorDip = ink is { } c ? (double?)Math.Round((c.Left + c.Right - box.Left - box.Right) / 2, 3) : null });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { pixelWidth = bitmap.PixelWidth, pixelHeight = bitmap.PixelHeight, scaleX = sx, scaleY = sy, rows }, new JsonSerializerOptions { WriteIndented = true }));
        Rect Bounds(FrameworkElement element)
        {
            try { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
            catch (InvalidOperationException) { return Rect.Empty; }
        }
        Rect? Ink(Rect rect, Color color)
        {
            int left = bitmap.PixelWidth, right = -1, top = bitmap.PixelHeight, bottom = -1;
            for (int y = Math.Max(0, (int)Math.Floor(rect.Top * sy)); y < Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(rect.Bottom * sy)); y++)
                for (int x = Math.Max(0, (int)Math.Floor(rect.Left * sx)); x < Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(rect.Right * sx)); x++)
                {
                    int p = (y * bitmap.PixelWidth + x) * 4;
                    if (pixels[p + 3] < 220 || Math.Abs(pixels[p + 2] - color.R) + Math.Abs(pixels[p + 1] - color.G) + Math.Abs(pixels[p] - color.B) > 115) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            return right < 0 ? null : new Rect(left / sx, top / sy, (right - left + 1) / sx, (bottom - top + 1) / sy);
        }
        static object Box(Rect rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject item)
    {
        while (VisualTreeHelper.GetParent(item) is { } parent) { yield return parent; item = parent; }
    }
}
