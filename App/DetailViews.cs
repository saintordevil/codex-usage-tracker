using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UsageDock.Core;

namespace UsageDock;

public sealed partial class MainWindow
{
    internal FrameworkElement ModelDetail(string title, List<DayUsage> period)
    {
        var panel = new StackPanel { Width = 250, Margin = new Thickness(14) };
        var heading = Label(title, HeaderSize, Ui.Text, FontWeights.SemiBold); heading.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(heading);
        var models = period.GroupBy(x => x.Model).Select(g => new { Name = g.Key, Cost = g.Sum(x => x.EstimatedCost), Tokens = g.Sum(x => x.Tokens), Unknown = g.Sum(x => x.UnpricedRequests) > 0 }).OrderByDescending(x => x.Cost).ToArray();
        var cost = models.Sum(x => x.Cost); var tokens = models.Sum(x => x.Tokens);
        var shares = models.Select(m => cost > 0 && models.All(x => !x.Unknown) ? m.Cost / cost : tokens > 0 ? (double)m.Tokens / tokens : 0).ToArray();
        var percentages = shares.Select(x => (int)Math.Floor(x * 100)).ToArray();
        foreach (var index in Enumerable.Range(0, shares.Length).OrderByDescending(i => shares[i] * 100 - percentages[i]).Take(Math.Max(0, 100 - percentages.Sum()))) percentages[index]++;
        for (int i = 0; i < models.Length; i++)
        {
            var model = models[i]; var row = new StackPanel { Margin = new Thickness(0, S.Compact ? 4 : 6, 0, S.Compact ? 4 : 6) };
            row.Children.Add(Pair(model.Name, model.Unknown ? "Unpriced" : Ui.Money(model.Cost), Supporting, true));
            var share = Pair(percentages[i] + "%", Number(model.Tokens) + " tokens", Supporting); share.Margin = new Thickness(0, 2, 0, 0); row.Children.Add(share);
            row.Children.Add(Bar(shares[i] * 100, Ui.Blue, S.Compact ? 4 : 5, new Thickness(0, 4, 0, 0))); panel.Children.Add(row);
        }
        var note = Label(title == "All Time" ? "Since " + period.Min(x => x.Day) + " · saved locally\nAPI-equivalent estimate from available history" : "From your Codex logs (estimated)", 10, Ui.Secondary); note.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(note);
        return new ScrollViewer { Content = panel, MaxHeight = SystemParameters.WorkArea.Height - 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private static Grid Pair(string left, string right, double size, bool bold = false)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Label(left, size, bold ? Ui.Text : Ui.Secondary, bold ? FontWeights.SemiBold : FontWeights.Normal); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(name);
        var value = Label(right, size, bold ? Ui.Text : Ui.Secondary); value.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
    }
    internal FrameworkElement TrendDetail()
    {
        var panel = new StackPanel { Width = 214, Margin = new Thickness(12) };
        var peak = Controller.History?.Days.GroupBy(x => x.Day).Select(g => g.Sum(x => x.Tokens)).DefaultIfEmpty(0).Max() ?? 0;
        panel.Children.Add(Pair("Usage Trend", "peak " + Number(peak) + " tokens", 11, true));
        var chart = Chart(214, 76); chart.Margin = new Thickness(0, 8, 0, 8); panel.Children.Add(chart);
        panel.Children.Add(Pair(DateTime.Today.AddDays(-29).ToString("MMM d"), DateTime.Today.ToString("MMM d"), 10));
        var note = Label("From your Codex logs (estimated)", 10, Ui.Secondary); note.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(note); return panel;
    }
    private FrameworkElement ResetDetail()
    {
        var panel = new StackPanel { Width = 240, Margin = new Thickness(14) };
        panel.Children.Add(Label("Rate Limit Resets", HeaderSize, Ui.Text, FontWeights.SemiBold));
        var count = Controller.Usage?.ResetCredits;
        var detail = Label(count is null ? "Reset credits are unavailable." : count == 0 ? "You have no rate limit resets." : $"{count} reset credit{(count == 1 ? "" : "s")} available.", Supporting); detail.Margin = new Thickness(0, 8, 0, 8); panel.Children.Add(detail);
        if (Controller.Usage?.ResetExpiries is { Count: > 0 } expiries)
        {
            int number = 1;
            foreach (var date in expiries)
            {
                var time = date - DateTimeOffset.UtcNow;
                var row = Pair(number++ + ". " + date.LocalDateTime.ToString("MMM d, HH:mm"), time.TotalSeconds > 0 ? Ui.Duration(time) : "Expired", Supporting);
                row.Margin = new Thickness(0, 5, 0, 5); panel.Children.Add(row);
            }
        }
        else if (count > 0) panel.Children.Add(Label("Expiry times are unavailable.", 11, Ui.Secondary));
        var note = Label("Use a reset from Codex. This monitor only reads the balance.", 10, Ui.Secondary); note.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(note); return panel;
    }
    private void ShowNotice(string message, bool success = false)
    {
        noticeTimer.Stop();
        if (notice is null)
        {
            notice = new Border { CornerRadius = new CornerRadius(8), Background = Ui.Card, BorderBrush = Ui.OuterLine, BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(12, 0, 12, 10), MaxWidth = PanelWidth - 48,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(notice, "TransientNotice");
            System.Windows.Automation.AutomationProperties.SetLiveSetting(notice, System.Windows.Automation.AutomationLiveSetting.Polite);
            Grid.SetRow(notice, 1); Panel.SetZIndex(notice, 10); root.Children.Add(notice);
        }
        notice.Child = Label(message, 12, success ? Ui.Green : Ui.Warning, FontWeights.SemiBold);
        notice.Visibility = Visibility.Visible;
        noticeTimer.Start();
    }
    private void DismissNotice() { noticeTimer.Stop(); if (notice is not null) { notice.Visibility = Visibility.Collapsed; notice.Child = null; } }
    // QA replaces only the sink; production uses the normal Windows clipboard.
    internal Action<BitmapSource> CopyImage { get; set; } = Clipboard.SetImage;
    private void ShareScreenshot()
    {
        DismissNotice();
        try
        {
            var host = (FrameworkElement)Content;
            var image = RenderImage(host);
            var bounds = shell.TransformToAncestor(host).TransformBounds(new Rect(shell.RenderSize));
            var dpi = VisualTreeHelper.GetDpi(host);
            var panel = new CroppedBitmap(image, new Int32Rect((int)Math.Round(bounds.X * dpi.DpiScaleX), (int)Math.Round(bounds.Y * dpi.DpiScaleY),
                (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY)));
            panel.Freeze(); CopyImage(panel); ShowNotice("Copied to clipboard", success: true);
        }
        catch (Exception) { ShowNotice("The screenshot could not be copied. Try again."); }
    }
    internal static BitmapSource RenderImage(FrameworkElement visual, double scale = 1)
    {
        visual.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX * scale), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY * scale), dpi.PixelsPerInchX * scale, dpi.PixelsPerInchY * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog { Title = "Export Codex usage", FileName = $"codex-usage-{DateTime.Today:yyyy-MM-dd}.csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { CsvExport.Write(dialog.FileName, Controller.History?.Days ?? []); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowNotice("The file could not be written. Try another location."); }
    }
    private void ShowAbout()
    {
        MessageBox.Show(this, "Usage Dock " + Program.Version + "\nPersonal Windows adaptation of OpenUsage.\n\nOriginal source: Robin Ebers and contributors (MIT).\nSource commit: 66aada23bfbac5c5988617fdd6a0f8edbe106764\n\nNo model requests, analytics, or automatic reset redemption.\nPrices are local API-equivalent estimates, not subscription charges.\n\nThis is an independent derivative, not an official OpenUsage release.", "About Usage Dock", MessageBoxButton.OK, MessageBoxImage.None);
    }
}
