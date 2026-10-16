using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UsageDock;

internal static class ExamplePreview
{
    internal static async Task RenderAsync(AppController controller, MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            controller.Settings.ReduceAnimations = true; controller.Settings.Expanded = true;
            controller.Settings.Theme = "Dark"; window.Navigate("dashboard");
            await Draw(); Save("example-dark.png");
            window.Buttons["Month"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Draw(); Save("example-month.png");
            window.Buttons["Today"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Buttons["QuickTheme"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Draw(); Save("example-light.png");
            window.Buttons["QuickTheme"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.ShowSettings(); await Draw(); Save("example-settings.png");
            await File.WriteAllTextAsync(Path.Combine(directory, "example-check.json"), JsonSerializer.Serialize(new {
                passed = controller.Demo && controller.Error is null && controller.History?.Days.Count == 270,
                synthetic = true, windowShown = window.IsVisible, days = controller.History?.Days.Count,
                periodChanged = controller.Settings.PeriodDays == 1, themeRestored = controller.Settings.Theme == "Dark"
            }));
        }
        finally { await controller.QuitAsync(); }
        async Task Draw()
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, window.Height));
            content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        }
        void Save(string name)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(MainWindow.RenderImage((FrameworkElement)window.Content, 2)));
            using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        }
    }
}
