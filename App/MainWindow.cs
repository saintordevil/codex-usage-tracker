using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using UsageDock.Core;

namespace UsageDock;

// Windows host for the source-defined 320pt OpenUsage popover. Design constants live in Ui.cs.
public sealed partial class MainWindow : Window
{
    internal readonly AppController Controller;
    private readonly Grid root = new();
    private StackPanel content = new();
    private TextBlock refreshStatus = new();
    private string screen = "dashboard";
    private readonly List<Popup> popups = [];
    private readonly List<(TextBlock Label, LimitWindow Window)> resetLabels = [];
    private Border shell = new();
    private bool fitPending;
    private bool animateNext;
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private Border? notice;
    internal bool NoticeVisible => notice?.Visibility == Visibility.Visible;
    internal int ContentRowCount => content.Children.Count;
    internal int NoticeCount => root.Children.OfType<Border>().Count(x => ReferenceEquals(x, notice));
    internal int NativeCornerResult { get; private set; }
    internal int NativeBorderResult { get; private set; }
    public const double PanelWidth = 340;
    private const double ShadowGutter = 12;
    public bool AllowClose { get; set; }
    public Dictionary<string, Button> Buttons { get; } = [];
    internal ContextMenu? ActiveMenu { get; private set; }
    internal IReadOnlyList<Popup> DetailPopups => popups;
    private AppSettings S => Controller.Settings;
    private double Supporting => S.Compact ? 11 : 12;
    private double HeaderSize => S.Compact ? 13 : 14;
    private double LabelSize => S.Compact ? 12 : 13;
    public MainWindow(AppController controller)
    {
        Controller = controller;
        Title = "Usage Dock"; Width = PanelWidth + ShadowGutter * 2; Height = 500; MinWidth = MaxWidth = Width;
        MinHeight = 180; MaxHeight = SystemParameters.WorkArea.Height - 36;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; AllowsTransparency = true; Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13; UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - Width - 16; Top = SystemParameters.WorkArea.Bottom - Height - 12;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // This layered window already draws its rounded surface and shadow. A DWM
            // frame surrounds the transparent gutter and is absent from content-only previews.
            int corners = 1, border = -2; // DWMWCP_DONOTROUND, DWMWA_COLOR_NONE
            NativeCornerResult = DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
            NativeBorderResult = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            ApplyNativeTheme();
        };
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); ClosePopups(); } };
        noticeTimer.Tick += (_, _) => DismissNotice();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ClosePopups(); DismissNotice(); } else { UpdateTime(); Fit(); } };
        Closed += (_, _) => { DismissNotice(); Controller.Changed -= Render; };
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (e.Key == Key.Escape) { if (screen != "dashboard") Navigate("dashboard"); else Hide(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Q) { _ = controller.QuitAsync(); e.Handled = true; }
            else if (e.Key == Key.F5 || ctrl && e.Key == Key.R) { _ = controller.RefreshAsync(); e.Handled = true; }
            else if (ctrl && e.Key == Key.OemComma) { ShowSettings(); e.Handled = true; }
            else if (e.Key == Key.Enter && screen == "dashboard") { Navigate("customize"); e.Handled = true; }
        };
        controller.Changed += Render; Render();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private void ApplyNativeTheme() { int dark = S.Theme == "Light" ? 0 : 1; if (new WindowInteropHelper(this).Handle is var h && h != IntPtr.Zero) _ = DwmSetWindowAttribute(h, 20, ref dark, sizeof(int)); }
    public static SolidColorBrush Brush(string hex) => Ui.Brush(hex);
    public static TextBlock Label(string text, double size = 12, Brush? brush = null, FontWeight? weight = null) => Ui.Label(text, size, brush, weight);
    public static string Number(long number) => Ui.Number(number);
    public Button ActionButton(string id, string text, Action action, bool selected = false)
    {
        var button = Ui.Button(text, action, selected);
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, id); Buttons[id] = button; return button;
    }
    public void ShowSettings() => Navigate("settings");
    public void Navigate(string page) { screen = page; animateNext = true; Render(); }
    public void Render()
    {
        DismissNotice(); notice = null; ClosePopups(); Ui.SetTheme(S.Theme); Foreground = Ui.Text;
        ApplyNativeTheme(); Topmost = S.AlwaysOnTop;
        Buttons.Clear(); resetLabels.Clear(); root.Children.Clear(); root.RowDefinitions.Clear();
        root.Background = Brushes.Transparent; content = new StackPanel(); refreshStatus = Label("", 10, Ui.Secondary); refreshStatus.LineHeight = 13;
        // Reuse the outer host; the Grid cannot be attached to two logical parents.
        if (Content is null)
        {
            shell = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(ShadowGutter), Child = root,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = .16, Color = Colors.Black, RenderingBias = RenderingBias.Performance } };
            var host = new Grid { Background = Brushes.Transparent }; host.Children.Add(shell); Content = host;
        }
        shell.Background = Ui.Page; shell.BorderBrush = Ui.OuterLine;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (screen != "dashboard")
        {
            var nav = new Grid { Height = 44, Margin = new Thickness(14, 0, 14, 0) };
            var back = ActionButton("Back", "", () => Navigate(screen == "metrics" ? "customize" : "dashboard"));
            back.Content = Ui.Glyph("back", 13); back.Width = 28; back.Height = 28; back.HorizontalAlignment = HorizontalAlignment.Left;
            back.SetValue(Ui.ButtonCornerRadiusProperty, new CornerRadius(14));
            var heading = Label(screen == "metrics" ? "Codex" : screen == "settings" ? "Settings" : "Customize", 14, Ui.Text, FontWeights.SemiBold);
            heading.Height = 22; // Even 96-DPI line box shares the 28px back button's center.
            heading.HorizontalAlignment = HorizontalAlignment.Center; heading.VerticalAlignment = VerticalAlignment.Center;
            nav.Children.Add(heading); nav.Children.Add(back); root.Children.Add(nav);
        }
        content.Margin = new Thickness(14, screen == "dashboard" ? 7 : 12, 14, 12);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        if (screen == "dashboard") BuildDashboard(); else if (screen == "settings") BuildSettings(); else if (screen == "customize") BuildCustomize(); else BuildMetrics();
        if (screen is "dashboard" or "settings") BuildFooter();
        if (animateNext && !S.ReduceAnimations && SystemParameters.ClientAreaAnimation)
        {
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var motion = new TranslateTransform(10, 0); content.RenderTransform = motion;
            motion.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        animateNext = false;
        UpdateTime(); Fit();
    }
    private void Fit()
    {
        if (fitPending) return; fitPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            fitPending = false; content.Measure(new Size(PanelWidth - 2, double.PositiveInfinity));
            var height = content.DesiredSize.Height + (screen == "dashboard" ? 53 : screen == "settings" ? 97 : 44) + 2 + ShadowGutter * 2;
            Height = Math.Clamp(height, MinHeight, MaxHeight);
            var area = SystemParameters.WorkArea;
            Left = Math.Clamp(Left, area.Left + 8, area.Right - Width - 8);
            Top = Math.Clamp(area.Bottom - Height - 12, area.Top + 8, area.Bottom - Height - 8);
        }, DispatcherPriority.Loaded);
    }
    private void BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(14, 12, 14, 12), MinHeight = 28 };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var version = Label("Usage Dock " + Program.Version, 10, Ui.Secondary); version.LineHeight = 13; identity.Children.Add(version);
        var refresh = ActionButton("Refresh", "", () => _ = Controller.RefreshAsync()); refresh.Content = refreshStatus; refresh.Padding = new Thickness(0); refresh.Background = Brushes.Transparent; refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.IsEnabled = !Controller.Refreshing; refresh.ToolTip = "Refresh now (Ctrl+R)"; identity.Children.Add(refresh); footer.Children.Add(identity);
        if (screen == "dashboard")
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var theme = ActionButton("QuickTheme", "", () => { S.Theme = S.Theme == "Light" ? "Dark" : "Light"; ChangedDisplay(); });
            theme.Width = theme.Height = 28; theme.Padding = new Thickness(0); theme.Margin = new Thickness(0, 0, 7, 0);
            theme.SetValue(Ui.ButtonCornerRadiusProperty, new CornerRadius(14));
            theme.Background = Ui.Page; theme.BorderBrush = Ui.OuterLine; theme.BorderThickness = new Thickness(1);
            theme.Content = Ui.Glyph(S.Theme == "Light" ? "moon" : "sun", 14, Ui.Secondary);
            theme.ToolTip = S.Theme == "Light" ? "Switch to dark mode" : "Switch to light mode";
            System.Windows.Automation.AutomationProperties.SetName(theme, (string)theme.ToolTip);
            actions.Children.Add(theme);
            var options = ActionButton("Options", "", () => { }); options.Padding = new Thickness(14, 0, 12, 0); options.Height = 28; options.FontWeight = FontWeights.SemiBold;
            options.SetValue(Ui.ButtonCornerRadiusProperty, new CornerRadius(14));
            var optionLabel = new StackPanel { Orientation = Orientation.Horizontal };
            optionLabel.Children.Add(Label("Options", 13, Ui.Text, FontWeights.SemiBold)); var caret = Ui.Glyph("down", 10, Ui.Text); caret.Margin = new Thickness(5, 0, 0, 0); optionLabel.Children.Add(caret); options.Content = optionLabel;
            options.Background = Ui.Page; options.BorderBrush = Ui.OuterLine; options.BorderThickness = new Thickness(1);
            options.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = .08, Color = Colors.Black };
            options.Click += (_, _) => OpenMenu(options, ("Customize", () => Navigate("customize")), ("Settings", ShowSettings), ("Share Screenshot", ShareScreenshot), ("Export Usage CSV", ExportCsv),
                ("Refresh Now", () => _ = Controller.RefreshAsync()), ("About Usage Dock", ShowAbout), ("Hide", Hide), ("Quit Usage Dock", () => _ = Controller.QuitAsync()));
            actions.Children.Add(options); Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        }
        footer.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock && Mouse.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        var footerSurface = new Border { BorderBrush = Ui.OuterLine, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        Grid.SetRow(footerSurface, 2); root.Children.Add(footerSurface);
    }
    public void UpdateTime()
    {
        foreach (var (label, window) in resetLabels) label.Text = ResetText(window.ResetsAt);
        var seconds = Math.Max(0, (Controller.NextRefresh - DateTimeOffset.UtcNow).TotalSeconds);
        refreshStatus.Text = Controller.Demo ? "Offline example data" : Controller.Refreshing ? "Updating..." : seconds >= 60 ? $"Next update in {Math.Ceiling(seconds / 60):0}m" : $"Next update in {Math.Ceiling(seconds):0}s";
    }
    private string ResetText(DateTimeOffset? time)
    {
        if (time is null) return "Reset unavailable";
        if (S.AbsoluteResets) return "Resets " + time.Value.LocalDateTime.ToString(S.TimeFormat == "12-hour" ? "ddd h:mm tt" : "ddd HH:mm");
        if (time <= DateTimeOffset.UtcNow) return "Awaiting reset";
        return "Resets in " + Ui.Duration(time.Value - DateTimeOffset.UtcNow);
    }
    private void ChangedDisplay() { Controller.SaveDisplayPreferences(); animateNext = true; Render(); }
    private void OpenMenu(FrameworkElement target, params (string Label, Action Action)[] choices)
    {
        var menu = Ui.Menu(); menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom;
        foreach (var (title, action) in choices) { var item = new MenuItem { Header = title, Padding = new Thickness(10, 5, 10, 5) }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        ActiveMenu = menu; menu.IsOpen = true;
    }
    private void ClosePopups() { if (ActiveMenu is not null) ActiveMenu.IsOpen = false; foreach (var p in popups) p.IsOpen = false; popups.Clear(); }
    private void AttachDetail(FrameworkElement target, Func<FrameworkElement> build)
    {
        Popup? popup = null; var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        close.Tick += (_, _) => { close.Stop(); if (popup is not null && !target.IsMouseOver && (popup.Child as UIElement)?.IsMouseOver != true) popup.IsOpen = false; };
        void Open()
        {
            close.Stop();
            if (popup is null)
            {
                var box = new Border { Background = Ui.Card, BorderBrush = Ui.OuterLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = build() };
                popup = new Popup { Child = box, PlacementTarget = target, Placement = PlacementMode.Right, HorizontalOffset = 5, AllowsTransparency = true, StaysOpen = true, PopupAnimation = S.ReduceAnimations ? PopupAnimation.None : PopupAnimation.Fade };
                box.MouseEnter += (_, _) => close.Stop(); box.MouseLeave += (_, _) => close.Start(); popup.Closed += (_, _) => close.Stop(); popups.Add(popup);
            }
            popup.IsOpen = true;
        }
        target.MouseEnter += (_, _) => Open(); target.MouseLeave += (_, _) => close.Start(); target.MouseLeftButtonUp += (_, _) => Open();
        target.Unloaded += (_, _) => { close.Stop(); if (popup is not null) popup.IsOpen = false; };
    }
}
