using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UsageDock;

internal static class QaHarness
{
    public static async Task RunAsync(AppController controller, MainWindow window, string[] args)
    {
        if (args.Contains("--qa-repair")) { await RepairQa.RunAsync(controller, window, args); return; }
        var directory = Program.Argument(args, "--qa-output");
        if (directory is null || Program.Argument(args, "--state") is null) return;
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        try
        {
            for (int i = 0; i < 120 && controller.Refreshing; i++) await Task.Delay(500);
            checks["liveQuota"] = controller.Usage?.Windows.Count > 0 && controller.Error is null;
            checks["liveHistory"] = controller.History?.Days.Count > 0 && controller.History.Warning is null;
            await Draw(); Capture(window, "dashboard.png"); checks["referencePanelWidth340"] = MainWindow.PanelWidth == 340 && window.ActualWidth == 364;
            checks["sparkRemoved"] = !controller.Settings.MetricOrder.Any(x => x.Contains("spark", StringComparison.OrdinalIgnoreCase)) && !controller.Usage!.Windows.Any(x => x.Name.Contains("spark", StringComparison.OrdinalIgnoreCase));
            if (!controller.Settings.Expanded) Click("Expand"); await Draw(); Capture(window, "expanded.png"); checks["expand"] = controller.Settings.Expanded;
            Click("Month"); await Draw(); checks["monthSelection"] = controller.Settings.PeriodDays == 30; Capture(window, "month.png");
            var modelTarget = Find((DependencyObject)window.Content, "ModelDetail-Last 30 Days");
            if (modelTarget is not null)
            {
                modelTarget.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }); await Draw();
                checks["modelHoverPopover"] = window.DetailPopups.Any(p => p.IsOpen);
                if (window.DetailPopups.FirstOrDefault(p => p.IsOpen)?.Child is FrameworkElement detail) Save(MainWindow.RenderImage(detail), "model-detail.png");
            }
            Click("Yesterday"); await Draw(); checks["yesterdaySelection"] = controller.Settings.Yesterday;
            Click("Today"); await Draw(); checks["todaySelection"] = !controller.Settings.Yesterday && controller.Settings.PeriodDays == 1;
            Click("Options"); Menu("Settings"); await Draw(); checks["settingsOpened"] = window.Buttons.ContainsKey("Theme"); Capture(window, "settings.png");
            Click("Density"); Menu("Compact"); await Draw(); checks["compactSetting"] = controller.Settings.Compact;
            Click("Theme"); Menu("Light"); await Draw(); checks["lightTheme"] = controller.Settings.Theme == "Light";
            window.Navigate("dashboard"); await Draw(); Capture(window, "light.png");
            Click("Options"); Menu("Settings"); await Draw(); Click("Theme"); Menu("Dark"); Click("Density"); Menu("Default"); await Draw();
            Click("Customize"); await Draw(); Capture(window, "customize.png"); Click("CodexMetrics"); await Draw(); Capture(window, "metrics.png");
            var initialIndex = controller.Settings.MetricOrder.IndexOf("Weekly"); window.MoveMetric("Weekly", "Session"); await Draw(); checks["reorder"] = controller.Settings.MetricOrder.IndexOf("Weekly") < initialIndex;
            window.MoveMetric("Session", "Weekly"); await Draw();
            Click("PlacementToday"); await Draw(); checks["placement"] = !controller.Settings.OnDemandMetrics.Contains("Today"); Click("PlacementToday");
            Click("PinSession"); Click("PinToday"); await Draw(); checks["historyPinUpdatesTray"] = controller.TrayText.Contains("Today: $");
            Click("PinToday"); Click("PinSession"); await Draw();
            window.Navigate("dashboard"); await Draw();
            var before = controller.Settings.ShowRemaining; Click("ToggleUsageWeekly"); await Draw(); checks["meterToggle"] = controller.Settings.ShowRemaining != before; Click("ToggleUsageWeekly");
            var absolute = controller.Settings.AbsoluteResets; Click("ToggleResetWeekly"); await Draw(); checks["resetToggle"] = controller.Settings.AbsoluteResets != absolute; Click("ToggleResetWeekly");
            window.Hide(); checks["hideToTray"] = !window.IsVisible; window.ShowActivated = false; window.Show(); await Draw(); checks["reopen"] = window.IsVisible;
            Capture(window, "final-dark.png");
            Click("Options"); await Draw(); CaptureMenu("options-dark"); window.ActiveMenu!.IsOpen = false;
            window.ShowSettings(); await Draw(); Capture(window, "settings-dark.png");
            Click("Theme"); await Draw(); CaptureMenu("picker-dark"); window.ActiveMenu!.IsOpen = false;
            window.Navigate("dashboard"); await Draw();
            var darkBounds = window.Buttons["QuickTheme"].TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(window.Buttons["QuickTheme"].RenderSize));
            Click("QuickTheme"); await Draw(); checks["quickLightTheme"] = controller.Settings.Theme == "Light";
            var lightBounds = window.Buttons["QuickTheme"].TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(window.Buttons["QuickTheme"].RenderSize));
            checks["themeButtonDoesNotShift"] = darkBounds == lightBounds;
            Click("QuickTheme"); await Draw(); checks["quickDarkTheme"] = controller.Settings.Theme == "Dark";
            checks["themePersisted"] = controller.Storage.Read<Core.AppSettings>("settings.json")?.Theme == "Dark";
            Click("QuickTheme"); await Draw(); Capture(window, "final-light.png");
            Click("Options"); await Draw(); CaptureMenu("options-light"); window.ActiveMenu!.IsOpen = false;
            window.Navigate("metrics"); await Draw(); Capture(window, "metrics-light.png"); window.Navigate("dashboard"); await Draw();
            if (controller.History is { } history)
            {
                Core.CsvExport.Write(Path.Combine(directory, "export.csv"), history.Days);
                checks["export"] = File.ReadLines(Path.Combine(directory, "export.csv")).Count() == history.Days.Count + 1;
            }
            File.WriteAllBytes(Path.Combine(directory, "white-openai.ico"), IconAsset.IconBytes());
            await File.WriteAllTextAsync(Path.Combine(directory, "qa-result.json"), JsonSerializer.Serialize(new { checks, passed = checks.Values.All(x => x), windows = controller.Usage?.Windows,
                historyFiles = controller.History?.Files, historyWarning = controller.History?.Warning, widthDip = window.ActualWidth, heightDip = window.ActualHeight,
                dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX, tray = controller.TrayEvidence() }, new JsonSerializerOptions { WriteIndented = true }));
            if (args.Contains("--qa-exit")) await controller.QuitAsync(); else window.Hide();
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "qa-result.json"), JsonSerializer.Serialize(new { passed = false, error = e.GetType().Name, detail = e.Message, stack = e.StackTrace, checks }));
            await controller.QuitAsync();
        }
        void Click(string id) => window.Buttons[id].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Menu(string title)
        {
            var menu = window.ActiveMenu ?? throw new InvalidOperationException("Menu did not open");
            var item = menu.Items.OfType<MenuItem>().First(x => (string)x.Header == title); menu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        async Task Draw() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(260); }
        void Save(BitmapSource image, string file) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream); }
        void Capture(Window view, string file)
        {
            var root = (FrameworkElement)view.Content;
            Save(MainWindow.RenderImage(root), file);
            var high = MainWindow.RenderImage(root, 2); Save(high, file.Replace(".png", "-2x.png"));
            VisualAudit.Save(root, high, Path.Combine(directory, file.Replace(".png", "-alignment.json")));
            var normal = MainWindow.RenderImage(root); VisualAudit.Save(root, normal, Path.Combine(directory, file.Replace(".png", "-alignment-1x.json")));
        }
        void CaptureMenu(string file)
        {
            var menu = window.ActiveMenu!; var bitmap = MainWindow.RenderImage(menu, 2); Save(bitmap, file + "-2x.png");
            VisualAudit.Save(menu, bitmap, Path.Combine(directory, file + "-alignment.json"));
        }
    }
    private static UIElement? Find(DependencyObject parent, string id)
    {
        if (parent is UIElement element && AutomationProperties.GetAutomationId(parent) == id) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (Find(VisualTreeHelper.GetChild(parent, i), id) is { } found) return found;
        return null;
    }
}
