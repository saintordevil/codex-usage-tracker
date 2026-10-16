using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using UsageDock.Core;

namespace UsageDock;

public sealed partial class MainWindow
{
    // Direct layout translation of TotalSpendCard, ProviderSectionHeader and WidgetRowView.
    private void BuildDashboard()
    {
        if (S.ShowTotalSpend) BuildTotalSpend();
        var section = new StackPanel { Margin = new Thickness(0, S.ShowTotalSpend ? (S.Compact ? 8 : 14) : 0, 0, 0) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 4, 4) };
        header.Children.Add(Ui.CodexIcon(S.Compact ? 14 : 16));
        var name = Label("Codex", HeaderSize, Ui.Text, FontWeights.SemiBold); name.Margin = new Thickness(5, 0, 5, 0); header.Children.Add(name);
        var plan = Label(Controller.Usage?.Plan ?? "", S.Compact ? 10 : 11, Ui.Secondary); plan.Margin = new Thickness(0, 1, 0, 0); header.Children.Add(plan);
        if (Controller.Error is not null)
        {
            var warning = Label(" !", 11, Brush("#ff9f0a"), FontWeights.Bold); warning.ToolTip = Controller.Error; header.Children.Add(warning);
            var outdated = Label(" Outdated", 10, Ui.Secondary); outdated.ToolTip = Controller.Usage?.FetchedAt.LocalDateTime.ToString("f"); header.Children.Add(outdated);
        }
        else if (Controller.Refreshing) header.Children.Add(Label("  Updating...", 10, Ui.Secondary));
        section.Children.Add(header);
        var rows = new StackPanel { Margin = new Thickness(0, S.Compact ? 3 : 5, 0, S.Compact ? 3 : 5) };
        var order = S.MetricOrder.Concat(Controller.Usage?.Windows.Select(w => w.Name) ?? []).Distinct().Where(x => !S.HiddenMetrics.Contains(x)).ToList();
        var visible = order.Where(x => !S.OnDemandMetrics.Contains(x)).ToList();
        if (visible.Count == 0 && order.Count > 0) visible.Add(order[0]);
        foreach (var metric in visible) rows.Children.Add(Metric(metric));
        var extra = order.Except(visible).ToList();
        if (extra.Count > 0)
        {
            var expand = ActionButton("Expand", "", () => { S.Expanded = !S.Expanded; ChangedDisplay(); });
            expand.Content = Ui.Glyph(S.Expanded ? "up" : "down", 12); expand.Background = Brushes.Transparent; expand.Height = 24;
            expand.ToolTip = S.Expanded ? "Show fewer metrics" : "Show more metrics"; rows.Children.Add(expand);
            if (S.Expanded) foreach (var metric in extra) rows.Children.Add(Metric(metric));
        }
        section.Children.Add(Ui.Surface(rows)); content.Children.Add(section);
        if (Controller.Error is not null && Controller.Usage is null)
        {
            var error = Label(Controller.Error, 11, Brush("#ff9f0a")); error.Margin = new Thickness(4, 8, 4, 0); content.Children.Add(error);
        }
        if (Controller.History?.Warning is { } warningText)
        {
            var warning = Label(warningText, 10, Brush("#ff9f0a")); warning.Margin = new Thickness(4, 8, 4, 0); content.Children.Add(warning);
        }
    }
    private void BuildTotalSpend()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, -4, 6), Height = 24 };
        var share = ActionButton("Share", "", ShareScreenshot); share.Content = Ui.Glyph("share", 15); share.Width = share.Height = 24; share.Background = Brushes.Transparent; share.Padding = new Thickness(0); share.ToolTip = "Copy screenshot"; DockPanel.SetDock(share, Dock.Right); header.Children.Add(share);
        var metric = ActionButton("TotalMetric", S.TotalMetric + "  ⌄", () => { }); metric.Background = Brushes.Transparent; metric.Padding = new Thickness(0); metric.FontSize = HeaderSize; metric.FontWeight = FontWeights.SemiBold;
        var metricTitle = new StackPanel { Orientation = Orientation.Horizontal };
        metricTitle.Children.Add(Label(S.TotalMetric, HeaderSize, Ui.Text, FontWeights.SemiBold)); var metricCaret = Ui.Glyph("down", 10); metricCaret.Margin = new Thickness(4, 0, 0, 0); metricTitle.Children.Add(metricCaret); metric.Content = metricTitle;
        metric.Click += (_, _) => OpenMenu(metric, ("Cost", () => SetMetric("Cost")), ("Cost/MTok", () => SetMetric("Cost/MTok")), ("Tokens", () => SetMetric("Tokens")));
        DockPanel.SetDock(metric, Dock.Left); header.Children.Add(metric);
        var info = Ui.Glyph("info", 11); info.HorizontalAlignment = HorizontalAlignment.Left; info.Margin = new Thickness(5, 0, 0, 0); info.ToolTip = "Includes Codex local history. Dollar values are API-rate estimates, not your subscription bill."; header.Children.Add(info); content.Children.Add(header);
        var body = new StackPanel();
        var picker = new UniformGrid { Rows = 1, Columns = 3 };
        foreach (var (id, title, days, yesterday) in new[] { ("Today", "Today", 1, false), ("Yesterday", "Yesterday", 1, true), ("Month", "30 Days", 30, false) })
        {
            bool active = S.PeriodDays == days && S.Yesterday == yesterday;
            var button = ActionButton(id, title, () => { S.PeriodDays = days; S.Yesterday = yesterday; ChangedDisplay(); }, active);
            button.SetValue(Ui.ButtonCornerRadiusProperty, new CornerRadius(6));
            button.Background = active ? Ui.Selected : Brushes.Transparent; button.FontSize = 11; button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            button.Foreground = active ? Ui.Text : Ui.Secondary; button.Height = 26; button.Padding = new Thickness(8, 0, 8, 0); picker.Children.Add(button);
            if (active) button.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = S.Theme == "Light" ? .12 : .18, Color = Colors.Black };
        }
        body.Children.Add(new Border { Background = Ui.Segment, CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = picker });
        var period = Controller.History?.Period(S.PeriodDays, S.Yesterday) ?? [];
        if (period.Count == 0)
        {
            var empty = Label(Controller.History is null ? "Reading local history..." : "No usage data for this period", Supporting, Ui.Secondary); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 18, 0, 18); body.Children.Add(empty);
        }
        else
        {
            double value = TotalValue(period);
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var ring = new Grid { Width = 104, Height = 104 };
            // RingSectorShape.swift uses the golden-ratio inner radius; one provider is one full arc.
            ring.Children.Add(new Ellipse { Stroke = Ui.Green, StrokeThickness = 19.9, Width = 104, Height = 104, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var number = Label(FormatTotal(value, true), 13, Ui.Text, FontWeights.SemiBold); number.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(number);
            var unit = Label(S.TotalMetric == "Tokens" ? "tokens" : S.TotalMetric == "Cost/MTok" ? "USD / MTok" : "dollars", 9, Ui.Secondary); unit.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(unit); ring.Children.Add(center); ring.ToolTip = FormatTotal(value, false) + " | From your Codex logs (estimated)"; row.Children.Add(ring);
            var legend = new Grid { VerticalAlignment = VerticalAlignment.Center }; legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); legend.ColumnDefinitions.Add(new ColumnDefinition()); legend.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            legend.Children.Add(new Ellipse { Fill = Ui.Green, Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0) }); var codex = Label("Codex", Supporting); Grid.SetColumn(codex, 1); legend.Children.Add(codex);
            var amount = Label(FormatTotal(value, false), Supporting, Ui.Secondary, FontWeights.Medium); amount.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(amount, 2); legend.Children.Add(amount); Grid.SetColumn(legend, 2); row.Children.Add(legend); body.Children.Add(row);
        }
        content.Children.Add(Ui.Surface(body, new Thickness(14, 12, 14, 12)));
        void SetMetric(string value) { S.TotalMetric = value; ChangedDisplay(); }
    }
    private double TotalValue(List<DayUsage> period) => S.TotalMetric switch { "Tokens" => period.Sum(x => (double)x.Tokens), "Cost/MTok" => period.Sum(x => x.Tokens) is var t && t > 0 ? period.Sum(x => x.EstimatedCost) / t * 1_000_000 : 0, _ => period.Sum(x => x.EstimatedCost) };
    private string FormatTotal(double value, bool compact) => S.TotalMetric == "Tokens" ? Number((long)value) : Ui.Money(value, compact);
    private FrameworkElement Metric(string metric)
    {
        var window = Controller.Usage?.Windows.FirstOrDefault(x => x.Name == metric);
        FrameworkElement row = window is not null ? Meter(window) : metric == "Usage Trend" ? TrendRow() : TextMetric(metric);
        var context = Ui.Menu();
        foreach (var (name, action) in new (string, Action)[] { (S.PinnedMetrics.Contains(metric) ? "Unpin" : "Pin to Tray", () => TogglePin(metric)),
            (S.OnDemandMetrics.Contains(metric) ? "Always Visible" : "On Demand", () => { ToggleList(S.OnDemandMetrics, metric); ChangedDisplay(); }),
            ("Hide", () => { S.HiddenMetrics.Add(metric); ChangedDisplay(); }), ("Refresh Codex", () => _ = Controller.RefreshAsync()), ("Customize", () => Navigate("metrics")) })
        { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); context.Items.Add(item); }
        row.ContextMenu = context; return row;
    }
    private FrameworkElement Meter(LimitWindow w)
    {
        var row = new StackPanel { Margin = new Thickness(14, S.Compact ? 5 : 10, 14, S.Compact ? 5 : 10) };
        var labelRow = new DockPanel(); var pace = Pace.Evaluate(w, DateTimeOffset.UtcNow);
        string? projection = w.UsedPercent >= 100 ? "Limit reached" : pace?.Status == "behind" ? "Limit in " + Ui.Duration(TimeSpan.FromSeconds(pace.SecondsToLimit ?? 0)) : pace?.Status == "onTrack" || S.AlwaysShowPacing ? (pace is null ? null : $"~{Math.Max(0, 100 - pace.ProjectedPercent):0}% left at reset") : null;
        if (projection is not null)
        {
            var warning = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (w.UsedPercent >= 100 || pace?.Status == "behind") { var flame = Ui.Glyph("flame", Supporting - 1, Ui.Critical); flame.Margin = new Thickness(0, 0, 3, 0); warning.Children.Add(flame); }
            warning.Children.Add(Label(projection, Supporting, Ui.Secondary)); DockPanel.SetDock(warning, Dock.Right); labelRow.Children.Add(warning);
        }
        labelRow.Children.Add(Label(w.Name, LabelSize, Ui.Text, FontWeights.SemiBold)); row.Children.Add(labelRow);
        var color = w.UsedPercent >= 100 || pace?.Status == "behind" ? Ui.Critical : pace?.Status == "onTrack" ? Ui.Warning : Ui.Blue;
        var meter = (Grid)Bar(S.ShowRemaining ? w.Remaining : w.UsedPercent, color, S.Compact ? 4 : 5, new Thickness(0, S.Compact ? 3 : 4, 0, S.Compact ? 3 : 4));
        if (pace is not null && w.UsedPercent < 100 && (pace.Status != "ahead" || S.AlwaysShowPacing) && w.ResetsAt is { } end)
        {
            var elapsed = Math.Clamp((DateTimeOffset.UtcNow - end.AddSeconds(-w.DurationSeconds)).TotalSeconds / w.DurationSeconds, 0, 1);
            var fraction = S.ShowRemaining ? 1 - elapsed : elapsed;
            var overlay = new Canvas { IsHitTestVisible = false };
            var tick = new Border { Width = 2, Height = meter.Height + 4, CornerRadius = new CornerRadius(1), Background = Ui.Text, Opacity = .55 };
            Canvas.SetTop(tick, -2); overlay.Children.Add(tick); meter.Children.Add(overlay);
            meter.SizeChanged += (_, _) => Canvas.SetLeft(tick, Math.Clamp(meter.ActualWidth * fraction - 1, 0, Math.Max(0, meter.ActualWidth - 2)));
            meter.ToolTip = $"At the current pace: {pace.ProjectedPercent:0}% used at reset. The marker shows an even pace.";
        }
        row.Children.Add(meter);
        var reading = new Grid(); reading.ColumnDefinitions.Add(new ColumnDefinition()); reading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var used = ActionButton("ToggleUsage" + w.Name, $"{(S.ShowRemaining ? w.Remaining : w.UsedPercent):0.#}% {(S.ShowRemaining ? "left" : "used")}", () => { S.ShowRemaining = !S.ShowRemaining; ChangedDisplay(); }); used.Background = Brushes.Transparent; used.Padding = new Thickness(0); used.FontSize = Supporting; used.HorizontalAlignment = HorizontalAlignment.Left; reading.Children.Add(used);
        var resetText = Label(ResetText(w.ResetsAt), Supporting, Ui.Secondary); resetLabels.Add((resetText, w));
        var reset = ActionButton("ToggleReset" + w.Name, "", () => { S.AbsoluteResets = !S.AbsoluteResets; ChangedDisplay(); }); reset.Content = resetText; reset.Background = Brushes.Transparent; reset.Padding = new Thickness(0); reset.ToolTip = w.ResetsAt?.LocalDateTime.ToString("f"); Grid.SetColumn(reset, 1); reading.Children.Add(reset); row.Children.Add(reading); return row;
    }
    private static FrameworkElement Bar(double percent, Brush color, double height, Thickness margin)
    {
        var grid = new Grid { Height = height, Margin = margin }; grid.Children.Add(new Border { Background = Ui.Track, CornerRadius = new CornerRadius(height / 2) });
        var fill = new Border { Background = color, CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        grid.SizeChanged += (_, _) => fill.Width = grid.ActualWidth * Math.Clamp(percent, 0, 100) / 100; grid.Children.Add(fill); return grid;
    }
    private FrameworkElement TextMetric(string metric)
    {
        var row = new Grid { Margin = new Thickness(14, S.Compact ? 4 : 6, 14, S.Compact ? 4 : 6) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(Label(metric, LabelSize, Ui.Text, FontWeights.SemiBold));
        var period = metric == "All Time" ? Controller.History is { AllTimeReady: true } all ? all.Days : null : metric is "Today" or "Yesterday" or "Last 30 Days" ? Controller.History?.Period(metric == "Last 30 Days" ? 30 : 1, metric == "Yesterday") : null;
        var value = period is { Count: > 0 } ? Ui.Money(period.Sum(x => x.EstimatedCost)) + " · " + Number(period.Sum(x => x.Tokens)) + " tokens" : metric switch
        {
            "Extra Usage" when Controller.Usage?.Credits is { } c => Ui.Money(Math.Floor(Math.Max(0, c)) * .04) + " · " + Math.Floor(Math.Max(0, c)).ToString("0") + " credits",
            "Rate Limit Resets" when Controller.Usage?.ResetCredits is { } r => r + " available", "All Time" when Controller.Refreshing => "Indexing history...", _ => "No data"
        };
        var reading = Label(value, Supporting, value == "No data" ? Ui.Secondary : Ui.Text); reading.HorizontalAlignment = HorizontalAlignment.Right; reading.Margin = new Thickness(6, 0, 0, 0); reading.TextWrapping = TextWrapping.NoWrap; Grid.SetColumn(reading, 1); row.Children.Add(reading);
        if (period is { Count: > 0 }) { System.Windows.Automation.AutomationProperties.SetAutomationId(reading, "ModelDetail-" + metric); AttachDetail(reading, () => ModelDetail(metric, period)); }
        if (metric == "Rate Limit Resets") AttachDetail(reading, ResetDetail);
        return row;
    }
    private FrameworkElement TrendRow()
    {
        var row = new DockPanel { Margin = new Thickness(14, S.Compact ? 4 : 6, 14, S.Compact ? 4 : 6) };
        var chart = Chart(150, S.Compact ? 14 : 18); DockPanel.SetDock(chart, Dock.Right); row.Children.Add(chart); row.Children.Add(Label("Usage Trend", Supporting, Ui.Text, FontWeights.SemiBold)); AttachDetail(chart, TrendDetail); return row;
    }
    private FrameworkElement Chart(double width, double height)
    {
        var chart = new Canvas { Width = width, Height = height, Background = Brushes.Transparent };
        var daily = Controller.History?.Days.GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Tokens)) ?? [];
        var amounts = Enumerable.Range(0, 30).Select(i => daily.GetValueOrDefault(DateTime.Today.AddDays(i - 29).ToString("yyyy-MM-dd"))).ToArray(); var max = Math.Max(1L, amounts.Max());
        for (int i = 0; i < 30; i++)
        {
            var bar = new Border { Background = Ui.Blue, Width = Math.Max(2, width / 30 - 1), Height = amounts[i] == 0 ? 2 : Math.Max(height * .18, height * amounts[i] / max), CornerRadius = new CornerRadius(1), ToolTip = $"{DateTime.Today.AddDays(i - 29):MMM d} · {Number(amounts[i])} tokens" };
            Canvas.SetLeft(bar, i * width / 30); Canvas.SetBottom(bar, 0); chart.Children.Add(bar);
        }
        return chart;
    }
    private static void ToggleList(List<string> list, string value) { if (!list.Remove(value)) list.Add(value); }
    private void TogglePin(string metric)
    {
        if (!S.PinnedMetrics.Remove(metric)) { if (S.PinnedMetrics.Count >= 2) { ShowNotice("You can pin up to two Codex metrics."); return; } S.PinnedMetrics.Add(metric); }
        ChangedDisplay();
    }
}
