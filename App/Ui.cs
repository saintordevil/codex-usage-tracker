using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Xml.Linq;

namespace UsageDock;

// Dimensions: DensitySetting.swift, Theme.swift, DashboardView.swift, WidgetRowView.swift.
public static class Ui
{
    public static readonly DependencyProperty ButtonCornerRadiusProperty = DependencyProperty.RegisterAttached(
        "ButtonCornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(7)));
    public static SolidColorBrush Page { get; private set; } = Brush("#1e1e1e");
    public static SolidColorBrush Card { get; private set; } = Brush("#292929");
    public static SolidColorBrush Text { get; private set; } = Brush("#eeeeef");
    public static SolidColorBrush Secondary { get; private set; } = Brush("#98989d");
    public static SolidColorBrush Track { get; private set; } = Brush("#48484b");
    public static SolidColorBrush OuterLine { get; private set; } = Brush("#49494b");
    public static SolidColorBrush Selected { get; private set; } = Brush("#565659");
    public static SolidColorBrush Blue { get; private set; } = Brush("#007aff");
    public static SolidColorBrush Segment { get; private set; } = Brush("#e9e9e9");
    public static SolidColorBrush Critical { get; private set; } = Brush("#ff3b30");
    public static SolidColorBrush Warning { get; private set; } = Brush("#ffcc00");
    public static SolidColorBrush Green => Brush("#10a37f");
    public static void SetTheme(string theme)
    {
        bool light = theme == "Light";
        Page = Brush(light ? "#ffffff" : "#1c1c1e"); Card = Brush(light ? "#f5f5f5" : "#28282b");
        Text = Brush(light ? "#030712" : "#f2f2f7"); Secondary = Brush(light ? "#6a7282" : "#a8a8b0");
        Track = Brush(light ? "#e1e1e1" : "#3c3c41"); OuterLine = Brush(light ? "#e5e7eb" : "#3a3a3e"); Selected = Brush(light ? "#ffffff" : "#49494f");
        Segment = Brush(light ? "#e9e9e9" : "#343438"); Blue = Brush(light ? "#007aff" : "#0a84ff");
        Critical = Brush(light ? "#ff3b30" : "#ff453a"); Warning = Brush(light ? "#ffcc00" : "#ffd60a");
    }
    public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static TextBlock Label(string text, double size = 12, Brush? color = null, FontWeight? weight = null) => new OpticalTextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, LineHeight = size * 1.5, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = color ?? Text, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public static Border Surface(UIElement child, Thickness? padding = null) => new() { Background = Card, CornerRadius = new CornerRadius(12), Padding = padding ?? new Thickness(0), Child = child };
    public static string Number(long value) => value switch { >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B", >= 1_000_000 => (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M", >= 1000 => (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K", _ => value.ToString("N0", CultureInfo.CurrentCulture) };
    public static string Money(double value, bool compact = false) => "$" + (compact && value >= 1000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "K" : value.ToString("N2", CultureInfo.InvariantCulture));
    public static string Duration(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    public static Button Button(string title, Action action, bool selected = false)
    {
        var b = new Button { FontSize = 12, Foreground = Text, Background = selected ? Selected : Track, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 5, 10, 5), Cursor = System.Windows.Input.Cursors.Hand };
        var text = Label(title);
        text.SetBinding(TextBlock.FontSizeProperty, new Binding("FontSize") { Source = b });
        text.SetBinding(TextBlock.FontWeightProperty, new Binding("FontWeight") { Source = b });
        text.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = b });
        text.SetBinding(TextBlock.LineHeightProperty, new Binding("FontSize") { Source = b, Converter = new LineHeightConverter() });
        b.Content = text;
        var template = new ControlTemplate(typeof(Button)); var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new TemplateBindingExtension(ButtonCornerRadiusProperty)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter); template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .78)); template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .65)); template.Triggers.Add(pressed);
        b.Template = template; b.Click += (_, _) => action(); return b;
    }
    public static FrameworkElement Glyph(string kind, double size = 14, Brush? brush = null)
    {
        var d = kind switch
        {
            "back" => "M 15 6 L 9 12 L 15 18", "down" => "M 6 9 L 12 15 L 18 9", "up" => "M 6 15 L 12 9 L 18 15",
            "share" => "M 8 9 L 4 9 L 4 22 L 20 22 L 20 9 L 16 9 M 12 16 L 12 2 M 7 7 L 12 2 L 17 7",
            "star" => "M 12 2 L 15 9 L 23 9 L 17 14 L 19 22 L 12 17 L 5 22 L 7 14 L 1 9 L 9 9 Z",
            "grip" => "M 3 6 L 21 6 M 3 12 L 21 12 M 3 18 L 21 18",
            "info" => "M 12 10 L 12 18 M 12 5 L 12 7 M 12 1 A 11 11 0 1 0 12 23 A 11 11 0 1 0 12 1",
            "moon" => "M 20.5 13 A 8.5 8.5 0 0 1 11 3.5 A 8.5 8.5 0 1 0 20.5 13 Z",
            "sun" => "M 12 8 A 4 4 0 1 0 12 16 A 4 4 0 1 0 12 8 M 12 2 L 12 4 M 12 20 L 12 22 M 2 12 L 4 12 M 20 12 L 22 12 M 5 5 L 6.5 6.5 M 17.5 17.5 L 19 19 M 5 19 L 6.5 17.5 M 17.5 6.5 L 19 5",
            "flame" => "M 12 3 Q 13 7 16 9.5 T 19 15 A 7 7 0 0 1 5 15 Q 5 12 6 12 A 2.5 2.5 0 0 0 11 12 C 11 10 9.5 9 9.5 7 Q 9.5 5 12 3 Z",
            _ => "M 9 6 L 15 12 L 9 18"
        };
        return new Viewbox { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Fill = kind == "flame" ? brush ?? Critical : null, Stroke = brush ?? Secondary, StrokeThickness = kind is "down" or "up" or "back" or "right" ? 2.5 : 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 24, Height = 24 } };
    }
    public static FrameworkElement ButtonLabel(string title, double size, string icon = "down", Brush? foreground = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Label(title, size, foreground));
        var caret = Glyph(icon, 12, foreground); caret.Margin = new Thickness(5, 0, 0, 0); row.Children.Add(caret); return row;
    }
    public static ContextMenu Menu()
    {
        var menu = new ContextMenu { Background = Page, Foreground = Text, BorderBrush = OuterLine, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Padding = new Thickness(5), MinWidth = 160 };
        var frame = new FrameworkElementFactory(typeof(Border)); frame.SetValue(Border.BackgroundProperty, Page); frame.SetValue(Border.BorderBrushProperty, OuterLine);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1)); frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(10)); frame.SetValue(Border.PaddingProperty, new Thickness(5));
        frame.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); menu.Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = frame };
        var label = new FrameworkElementFactory(typeof(OpticalTextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding()); label.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI"));
        label.SetValue(TextBlock.FontSizeProperty, 12d); label.SetValue(TextBlock.LineHeightProperty, 18d); label.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        label.SetValue(TextBlock.ForegroundProperty, Text); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var data = new DataTemplate { VisualTree = label };
        var itemFrame = new FrameworkElementFactory(typeof(Border), "ItemBackground"); itemFrame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        itemFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5)); itemFrame.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header"); presenter.SetValue(ContentPresenter.ContentTemplateProperty, data); itemFrame.AppendChild(presenter);
        var itemTemplate = new ControlTemplate(typeof(MenuItem)) { VisualTree = itemFrame };
        var highlight = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true }; highlight.Setters.Add(new Setter(Border.BackgroundProperty, Segment, "ItemBackground")); itemTemplate.Triggers.Add(highlight);
        var style = new Style(typeof(MenuItem)); style.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate)); style.Setters.Add(new Setter(Control.ForegroundProperty, Text)); style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 27d));
        menu.ItemContainerStyle = style; return menu;
    }
    private static Geometry? codex;
    public static FrameworkElement CodexIcon(double size)
    {
        if (codex is null)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsageDock.codex.svg")!;
            var xml = XDocument.Load(s); codex = Geometry.Parse(xml.Root!.Element(XName.Get("path", "http://www.w3.org/2000/svg"))!.Attribute("d")!.Value); codex.Freeze();
        }
        return new Viewbox { Width = size, Height = size, Child = new System.Windows.Shapes.Path { Data = codex, Fill = Secondary, Width = 100, Height = 100 } };
    }
    private sealed class LineHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (double)value * 1.5;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
