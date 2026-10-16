using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UsageDock;

internal static class RepairQa
{
    internal static async Task RunAsync(AppController controller, MainWindow window, string[] args)
    {
        var directory = Program.Argument(args, "--qa-output");
        if (directory is null || Program.Argument(args, "--state") is null) return;
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        var native = new Dictionary<string, NativeCaptureResult>();
        var resources = new Dictionary<string, object>();
        try
        {
            for (int i = 0; i < 120 && controller.Refreshing; i++) await Task.Delay(500);
            checks["liveData"] = controller.Usage?.Windows.Count > 0 && controller.History?.Days.Count > 0 && controller.Error is null;
            controller.Settings.Theme = "Dark"; controller.Settings.Expanded = true; controller.Settings.ReduceAnimations = true;
            controller.SaveDisplayPreferences(); window.Navigate("dashboard"); await Draw();
            var size = window.RenderSize; int rows = window.ContentRowCount;
            resources["beforeCopies"] = ResourceSample();
            checks["segmentCorners"] = new[] { "Today", "Yesterday", "Month" }.All(id => (CornerRadius)window.Buttons[id].GetValue(Ui.ButtonCornerRadiusProperty) == new CornerRadius(6));
            int copies = 0; BitmapSource? latest = null;
            window.CopyImage = image => { copies++; latest = image; };
            Click("Share"); await Draw(); var first = Pixels(latest!);
            checks["singleCopy"] = copies == 1 && window.NoticeVisible && window.NoticeCount == 1;
            checks["shareExcludesShadowGutter"] = latest!.PixelWidth == (int)Math.Round(MainWindow.PanelWidth * VisualTreeHelper.GetDpi(window).DpiScaleX);
            int w = latest.PixelWidth, h = latest.PixelHeight;
            checks["sharePanelIsNotClipped"] = first[(w / 2) * 4 + 3] > 200 && first[((h - 1) * w + w / 2) * 4 + 3] > 200 &&
                first[(h / 2 * w) * 4 + 3] > 200 && first[(h / 2 * w + w - 1) * 4 + 3] > 200;
            Save(latest, "copied-panel.png");
            native["notice"] = await NativeVisualCapture.CaptureAsync(window, Path.Combine(directory, "live-notice.png"));
            for (int i = 0; i < 20; i++) Click("Share");
            await Draw();
            checks["repeatedCopiesBounded"] = copies == 21 && window.NoticeCount == 1 && window.ContentRowCount == rows && window.RenderSize == size;
            checks["noticeExcludedFromNextCopy"] = first.SequenceEqual(Pixels(latest!));
            resources["afterRepeatedCopies"] = ResourceSample();
            await Task.Delay(1400); Click("Share"); await Task.Delay(1300);
            checks["repeatRestartsTimeout"] = window.NoticeVisible;
            await Task.Delay(1400); checks["noticeAutoDismisses"] = !window.NoticeVisible && window.RenderSize == size;
            window.CopyImage = _ => throw new COMException("Fixture clipboard busy"); Click("Share");
            checks["copyFailureShowsOneNotice"] = window.NoticeVisible && window.NoticeCount == 1 && window.RenderSize == size;
            await Task.Delay(2700); checks["failureNoticeDismisses"] = !window.NoticeVisible;
            window.CopyImage = image => latest = image; Click("Share"); window.Hide();
            checks["hideDismissesNotice"] = !window.NoticeVisible;
            window.ShowActivated = false; window.Show(); await Draw();
            checks["reopenHasNoStaleNotice"] = !window.NoticeVisible && window.RenderSize == size;
            foreach (var id in new[] { "Yesterday", "Month", "Today" }) { Click(id); await Draw(); }
            checks["periodSelectionWorks"] = controller.Settings.PeriodDays == 1 && !controller.Settings.Yesterday;
            checks["allTimeReady"] = controller.History?.AllTimeReady == true && controller.History.Days.Any(d => string.CompareOrdinal(d.Day, DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd")) < 0);
            var texts = Descendants((DependencyObject)window.Content).OfType<TextBlock>().ToArray();
            var allLabel = texts.FirstOrDefault(t => t.Text == "All Time"); var monthLabel = texts.FirstOrDefault(t => t.Text == "Last 30 Days");
            checks["allTimeBelowMonth"] = allLabel is not null && monthLabel is not null && Bounds(allLabel).Top - Bounds(monthLabel).Top >= 30;
            checks["allTimeFitsPanel"] = allLabel is not null && Bounds(allLabel).Bottom < Bounds(window.Buttons["Options"]).Top - 16;
            checks["allTimeNoHorizontalClipping"] = allLabel is not null && VisualTreeHelper.GetParent(allLabel) is Grid allRow && allRow.Children.OfType<TextBlock>().All(t => Bounds(t).Left >= 12 && Bounds(t).Right <= window.ActualWidth - 12);
            Capture("dark"); native["dark"] = await NativeVisualCapture.CaptureAsync(window, Path.Combine(directory, "live-dark.png"));
            var allTimeTarget = texts.FirstOrDefault(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "ModelDetail-All Time");
            if (allTimeTarget is not null)
            {
                allTimeTarget.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent }); await Draw();
                checks["allTimeDetailOpens"] = window.DetailPopups.Any(p => p.IsOpen);
                if (window.DetailPopups.FirstOrDefault(p => p.IsOpen)?.Child is FrameworkElement detail) Save(MainWindow.RenderImage(detail), "all-time-models.png");
            }
            // The acceptance measurement must reject a deliberately misplaced label.
            window.Navigate("dashboard"); await Draw();
            var bad = Descendants(window.Buttons["Options"]).OfType<TextBlock>().Single(); var transform = bad.RenderTransform;
            var group = new TransformGroup(); group.Children.Add(transform); group.Children.Add(new TranslateTransform(0, 3)); bad.RenderTransform = group;
            Capture("known-bad-offset"); bad.RenderTransform = transform;
            window.ShowSettings(); await Draw(); Capture("settings-dark");
            native["settings"] = await NativeVisualCapture.CaptureAsync(window, Path.Combine(directory, "live-settings-dark.png"));
            var settingsScroll = Descendants((DependencyObject)window.Content).OfType<ScrollViewer>().First(); settingsScroll.ScrollToBottom(); await Draw(); Capture("settings-bottom-dark");
            settingsScroll.ScrollToTop(); await Draw();
            var toggles = Descendants((DependencyObject)window.Content).OfType<ToggleButton>().ToArray();
            foreach (var toggle in toggles) toggle.IsChecked = !toggle.IsChecked;
            Capture("settings-switches-opposite-dark");
            Click("Theme"); await Draw();
            if (window.ActiveMenu is { } menu) { Save(MainWindow.RenderImage(menu), "settings-picker-dark.png"); VisualAudit.Save(menu, MainWindow.RenderImage(menu), Path.Combine(directory, "settings-picker-dark-alignment.json")); menu.IsOpen = false; }
            window.Navigate("metrics"); await Draw(); Capture("metrics-dark");
            window.Navigate("dashboard"); await Draw(); Click("QuickTheme"); await Draw();
            checks["lightStillWorks"] = controller.Settings.Theme == "Light"; Capture("light");
            native["light"] = await NativeVisualCapture.CaptureAsync(window, Path.Combine(directory, "live-light.png"));
            Click("QuickTheme"); await Draw();
            checks["darkSaved"] = controller.Storage.Read<Core.AppSettings>("settings.json")?.Theme == "Dark";
            checks["nativeWindowsMatchRenderedPanel"] = native.Values.All(x => x.noExtraNativeFrame && x.meanPixelError < 2 && x.pixelsOverTolerancePercent < 3);
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var idle = Stopwatch.StartNew();
            await Task.Delay(5000); process.Refresh();
            resources["idleCpuPercentNormalized"] = (process.TotalProcessorTime - cpu).TotalSeconds / idle.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
            resources["afterIdle"] = ResourceSample();
            var result = new { passed = checks.Values.All(x => x), checks, native, resources, windowWidth = window.ActualWidth, windowHeight = window.ActualHeight,
                clipboard = "The real share-button path ran with an isolated image sink. The user's Windows clipboard was not read or changed." };
            await File.WriteAllTextAsync(Path.Combine(directory, "repair-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(directory, "repair-result.json"), JsonSerializer.Serialize(new { passed = false, error = e.GetType().Name, detail = e.Message, stack = e.StackTrace, checks })); }
        finally { await controller.QuitAsync(); }
        void Click(string id) => window.Buttons[id].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Rect Bounds(FrameworkElement item) => item.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(item.RenderSize));
        async Task Draw() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(260); }
        void Save(BitmapSource image, string name) { using var stream = File.Create(Path.Combine(directory, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); }
        void Capture(string name)
        {
            var root = (FrameworkElement)window.Content; var bitmap = MainWindow.RenderImage(root);
            Save(bitmap, name + ".png"); VisualAudit.Save(root, bitmap, Path.Combine(directory, name + "-alignment.json"));
            Save(MainWindow.RenderImage(root, 2), name + "-2x.png");
        }
        static byte[] Pixels(BitmapSource image) { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
        static object ResourceSample()
        {
            using var process = Process.GetCurrentProcess();
            return new { workingMiB = process.WorkingSet64 / 1048576d, peakWorkingMiB = process.PeakWorkingSet64 / 1048576d,
                privateCommitMiB = process.PrivateMemorySize64 / 1048576d, handles = process.HandleCount, threads = process.Threads.Count };
        }
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
