using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace UsageDock;

public sealed partial class MainWindow
{
    // Grouped settings and Customize rows follow SettingsScreen.swift / CustomizeRow.swift.
    private void BuildSettings()
    {
        Section("General",
            SettingRow("Show Total Spend", Toggle("ShowTotal", S.ShowTotalSpend, v => { S.ShowTotalSpend = v; ChangedDisplay(); })),
            SettingRow("Keep on Top", Toggle("Topmost", S.AlwaysOnTop, v => { S.AlwaysOnTop = v; ChangedDisplay(); })));
        Section("Appearance",
            SettingRow("Theme", Picker("Theme", S.Theme, ["Dark", "Light"], v => S.Theme = v)),
            SettingRow("Density", Picker("Density", S.Compact ? "Compact" : "Default", ["Default", "Compact"], v => S.Compact = v == "Compact")),
            SettingRow("Reduce Animations", Toggle("Animations", S.ReduceAnimations, v => { S.ReduceAnimations = v; ChangedDisplay(); })),
            SettingRow("Time Format", Picker("TimeFormat", S.TimeFormat, ["12-hour", "24-hour"], v => S.TimeFormat = v)));
        Section("Usage Display",
            SettingRow("Show Usage As", Picker("UsageStyle", S.ShowRemaining ? "Left" : "Used", ["Left", "Used"], v => S.ShowRemaining = v == "Left")),
            SettingRow("Reset Times", Picker("ResetStyle", S.AbsoluteResets ? "Exact" : "Countdown", ["Countdown", "Exact"], v => S.AbsoluteResets = v == "Exact")),
            SettingRow("Always Show Pacing", Toggle("Pacing", S.AlwaysShowPacing, v => { S.AlwaysShowPacing = v; ChangedDisplay(); })));
        Section("Notifications", SettingRow("Low Remaining Usage", Toggle("Notifications", S.LowUsageNotification, v => { S.LowUsageNotification = v; ChangedDisplay(); })));
        var advanced = new StackPanel();
        advanced.Children.Add(SettingRow("Refresh Interval", Picker("Interval", S.RefreshMinutes + " min", ["1 min", "5 min", "10 min", "30 min"], v => { S.RefreshMinutes = int.Parse(v.Split(' ')[0]); })));
        var choose = ActionButton("Browse", "Choose Folder...", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Select your Codex home", InitialDirectory = Controller.Home };
            if (dialog.ShowDialog(this) == true) { S.CodexHome = dialog.FolderName; Controller.SaveSettings(S); Render(); }
        });
        advanced.Children.Add(SettingRow("Codex Home", choose));
        var home = Label(Controller.Demo ? "Offline preview, no Codex folder is read" : Controller.Home, 10, Ui.Secondary); home.Margin = new Thickness(12, 0, 12, 9); advanced.Children.Add(home);
        Section("Advanced", advanced);
        var info = Label("Usage checks read your subscription. Price estimates run locally and do not spend tokens.\n\nNo analytics or local web API.", 11, Ui.Secondary);
        info.Margin = new Thickness(4, 3, 4, 14); content.Children.Add(info);
        var customize = ActionButton("Customize", "", () => Navigate("customize")); customize.Content = Ui.ButtonLabel("Customize", 12, "right", Ui.Text); customize.Height = 34; content.Children.Add(customize);
    }
    private void Section(string title, params UIElement[] children)
    {
        var heading = Label(title, 11, Ui.Secondary); heading.Margin = new Thickness(4, content.Children.Count == 0 ? 0 : (S.Compact ? 8 : 14), 0, 5); content.Children.Add(heading);
        var rows = new StackPanel(); for (int i = 0; i < children.Length; i++) { rows.Children.Add(children[i]); if (i < children.Length - 1) rows.Children.Add(new Border { Height = .5, Background = Ui.OuterLine, Opacity = .4, Margin = new Thickness(12, 0, 12, 0) }); }
        content.Children.Add(Ui.Surface(rows));
    }
    private FrameworkElement SettingRow(string title, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(12, S.Compact ? 6 : 9, 12, S.Compact ? 6 : 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Label(title, Supporting); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label); control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
    private Button Picker(string id, string selected, string[] choices, Action<string> choose)
    {
        var button = ActionButton(id, "", () => { }); button.Content = Ui.ButtonLabel(selected, Supporting, foreground: Ui.Secondary); button.Background = Brushes.Transparent; button.Foreground = Ui.Secondary; button.FontSize = Supporting; button.Padding = new Thickness(5, 1, 0, 1);
        button.Click += (_, _) => OpenMenu(button, choices.Select(option => (option, (Action)(() => { choose(option); ChangedDisplay(); }))).ToArray()); return button;
    }
    private ToggleButton Toggle(string id, bool value, Action<bool> change)
    {
        var toggle = new ToggleButton { IsChecked = value, Width = 28, Height = 16, Background = Ui.Track, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, id);
        var template = new ControlTemplate(typeof(ToggleButton)); var grid = new FrameworkElementFactory(typeof(Grid));
        var track = new FrameworkElementFactory(typeof(Border), "Track"); track.SetValue(Border.BackgroundProperty, Ui.Track); track.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); grid.AppendChild(track);
        var thumb = new FrameworkElementFactory(typeof(Border), "Knob"); thumb.SetValue(WidthProperty, 12d); thumb.SetValue(HeightProperty, 12d); thumb.SetValue(Border.BackgroundProperty, Brushes.White); thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); thumb.SetValue(MarginProperty, new Thickness(2)); thumb.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left); grid.AppendChild(thumb); template.VisualTree = grid;
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true }; on.Setters.Add(new Setter(Border.BackgroundProperty, Ui.Blue, "Track")); on.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right, "Knob")); template.Triggers.Add(on); toggle.Template = template;
        toggle.Click += (_, _) => change(toggle.IsChecked == true); return toggle;
    }
    private void BuildCustomize()
    {
        var help = Label("Choose what's visible, and pin up to two metrics to the tray tooltip.", 11, Ui.Secondary); help.Margin = new Thickness(4, 0, 4, 12); content.Children.Add(help);
        var button = ActionButton("CodexMetrics", "", () => Navigate("metrics")); button.Background = Brushes.Transparent; button.Padding = new Thickness(12, 12, 12, 12);
        var row = new Grid { Width = 264 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Ui.CodexIcon(18)); var title = Label("Codex", HeaderSize, Ui.Text, FontWeights.SemiBold); title.Margin = new Thickness(9, 0, 0, 0); Grid.SetColumn(title, 1); row.Children.Add(title); var arrow = Ui.Glyph("right", 12); Grid.SetColumn(arrow, 2); row.Children.Add(arrow); button.Content = row; content.Children.Add(Ui.Surface(button));
        var settings = ActionButton("Settings", "", ShowSettings); settings.Content = Ui.ButtonLabel("Settings", 12, "right", Ui.Text); settings.Margin = new Thickness(0, 14, 0, 0); settings.Height = 32; content.Children.Add(settings);
    }
    private void BuildMetrics()
    {
        var note = Label("Drag to reorder. Use the star to pin a value, and the switch to show or hide it.", 11, Ui.Secondary); note.Margin = new Thickness(4, 0, 4, 12); content.Children.Add(note);
        var rows = new StackPanel();
        foreach (var name in S.MetricOrder.ToArray())
        {
            var row = new Grid { Margin = new Thickness(12, S.Compact ? 6 : 9, 12, S.Compact ? 6 : 9), Background = Brushes.Transparent, AllowDrop = true };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            var grip = Ui.Glyph("grip", 12); grip.Cursor = Cursors.SizeAll; row.Children.Add(grip);
            var label = Label(name, Supporting); label.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(label, 1); row.Children.Add(label);
            var star = ActionButton("Pin" + name, "", () => TogglePin(name)); star.Content = Ui.Glyph("star", 12, S.PinnedMetrics.Contains(name) ? Ui.Blue : Ui.Secondary); star.Background = Brushes.Transparent; star.Padding = new Thickness(3); star.ToolTip = S.PinnedMetrics.Contains(name) ? "Unpin" : "Pin to tray tooltip"; Grid.SetColumn(star, 2); row.Children.Add(star);
            var toggle = Toggle("Visible" + name, !S.HiddenMetrics.Contains(name), _ => { ToggleList(S.HiddenMetrics, name); ChangedDisplay(); }); toggle.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(toggle, 3); row.Children.Add(toggle);
            Point? start = null;
            grip.MouseLeftButtonDown += (_, e) => start = e.GetPosition(grip);
            grip.MouseMove += (_, e) => { if (start is { } origin && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(grip) - origin).Length > 5) { start = null; DragDrop.DoDragDrop(grip, new DataObject("UsageDock.Metric", name), DragDropEffects.Move); } };
            row.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("UsageDock.Metric") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            row.Drop += (_, e) => { if (e.Data.GetData("UsageDock.Metric") is string from) MoveMetric(from, name); e.Handled = true; };
            var menu = Ui.Menu();
            foreach (var (text, action) in new (string, Action)[] {
                (S.OnDemandMetrics.Contains(name) ? "Always Visible" : "On Demand", () => { ToggleList(S.OnDemandMetrics, name); ChangedDisplay(); }),
                ("Move Up", () => MoveBy(name, -1)), ("Move Down", () => MoveBy(name, 1)) }) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
            row.ContextMenu = menu; rows.Children.Add(row);
            var placement = ActionButton("Placement" + name, "", () => { ToggleList(S.OnDemandMetrics, name); ChangedDisplay(); });
            placement.Content = Ui.ButtonLabel(S.OnDemandMetrics.Contains(name) ? "On Demand" : "Always Visible", 10, foreground: Ui.Secondary);
            placement.Background = Brushes.Transparent; placement.Foreground = Ui.Secondary; placement.FontSize = 10; placement.Padding = new Thickness(0, 0, 0, 0); placement.HorizontalAlignment = HorizontalAlignment.Left; placement.Margin = new Thickness(34, -5, 0, 4); rows.Children.Add(placement);
        }
        content.Children.Add(Ui.Surface(rows));
        var reset = ActionButton("ResetLayout", "Reset Layout", () =>
        {
            if (MessageBox.Show(this, "Restore the default Codex metric layout?", "Reset Layout", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var defaults = new Core.AppSettings(); S.MetricOrder = defaults.MetricOrder; S.HiddenMetrics = defaults.HiddenMetrics; S.OnDemandMetrics = defaults.OnDemandMetrics; S.PinnedMetrics = defaults.PinnedMetrics; ChangedDisplay();
        }); reset.Margin = new Thickness(0, 14, 0, 0); content.Children.Add(reset);
    }
    internal void MoveMetric(string from, string to)
    {
        if (from == to || !S.MetricOrder.Contains(from) || !S.MetricOrder.Contains(to)) return;
        S.MetricOrder.Remove(from); S.MetricOrder.Insert(S.MetricOrder.IndexOf(to), from); ChangedDisplay();
    }
    private void MoveBy(string name, int delta)
    {
        var index = S.MetricOrder.IndexOf(name); var target = index + delta;
        if (index < 0 || target < 0 || target >= S.MetricOrder.Count) return;
        S.MetricOrder.RemoveAt(index); S.MetricOrder.Insert(target, name); ChangedDisplay();
    }
}
