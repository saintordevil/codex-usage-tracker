using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using UsageDock.Core;
using Forms = System.Windows.Forms;

namespace UsageDock;

public sealed record CachedUsage(string Identity, UsageSnapshot Snapshot);
public sealed record CachedHistory(string Home, HistorySnapshot Snapshot);

public sealed class AppController : IDisposable
{
    public AppStorage Storage { get; }
    public AppSettings Settings { get; private set; }
    public UsageSnapshot? Usage { get; private set; }
    public HistorySnapshot? History { get; private set; }
    public string? Error { get; private set; }
    public string HistoryStatus { get; private set; } = "Reading local history...";
    public bool Refreshing { get; private set; }
    public DateTimeOffset NextRefresh { get; private set; }
    public bool Qa { get; }
    public bool Demo { get; }
    public event Action? Changed;
    private readonly CodexClient client = new();
    private readonly HistoryReader historyReader;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Forms.NotifyIcon tray;
    private readonly EventWaitHandle showEvent;
    private readonly RegisteredWaitHandle showWait;
    private readonly EventWaitHandle quitEvent;
    private readonly RegisteredWaitHandle quitWait;
    private readonly EventWaitHandle refreshEvent;
    private readonly RegisteredWaitHandle refreshWait;
    private readonly MainWindow window;
    private Task? active;
    private bool exiting;
    private bool disposed;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private bool notified;
    private int failures;
    private string selectedHome;

    public AppController(AppStorage storage, bool qa, string[] args)
    {
        Qa = qa; Demo = args.Contains("--demo"); Storage = storage;
        Settings = Storage.Read<AppSettings>("settings.json") ?? new();
        Settings.Normalize();
        selectedHome = Home;
        historyReader = new(storage, new Pricing());
        if (!Demo)
        {
            var identity = CodexClient.Identity(Home);
            var cached = Storage.Read<CachedUsage>("usage.json");
            if (cached?.Identity == identity && identity != "unavailable") Usage = cached.Snapshot;
        }
        Usage?.Windows.RemoveAll(w => w.Name.Contains("spark", StringComparison.OrdinalIgnoreCase));
        var history = Demo ? null : Storage.Read<CachedHistory>("history.json");
        if (history?.Home == Home) History = history.Snapshot;
        if (Demo) LoadExample();
        History?.Days.RemoveAll(d => d.Model.Contains("spark", StringComparison.OrdinalIgnoreCase));
        window = new MainWindow(this);
        var renderExample = Program.Argument(args, "--render-example");
        tray = new Forms.NotifyIcon { Icon = IconAsset.WhiteOpenAI(), Text = "Usage Dock | Codex usage", Visible = renderExample is null };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Usage Dock", null, (_, _) => Show());
        menu.Items.Add("Refresh usage", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add("Settings", null, (_, _) => { Show(); window.ShowSettings(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _ = QuitAsync());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) { if (window.IsVisible) window.Hide(); else Show(); } };
        string eventPrefix = Demo ? "Local\\UsageDock.Demo" : qa ? "Local\\UsageDock.QA" : "Local\\UsageDock";
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventPrefix + ".Show");
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Application.Current.Dispatcher.BeginInvoke(Show), null, -1, false);
        quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventPrefix + ".Quit");
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _ = QuitAsync()), null, -1, false);
        refreshEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventPrefix + ".Refresh");
        refreshWait = ThreadPool.RegisterWaitForSingleObject(refreshEvent, (_, _) => Application.Current.Dispatcher.BeginInvoke(() => _ = RefreshAsync()), null, -1, false);
        timer.Tick += (_, _) => { if (DateTimeOffset.UtcNow >= NextRefresh && !Refreshing) _ = RefreshAsync(); else if (window.IsVisible) window.UpdateTime(); };
        if (renderExample is null) timer.Start();
        if (qa || args.Contains("--no-activate")) window.ShowActivated = false;
        if (!args.Contains("--background") && renderExample is null) window.Show();
        active = RefreshAsync();
        if (renderExample is not null) _ = ExamplePreview.RenderAsync(this, window, renderExample);
        else if (qa) _ = QaHarness.RunAsync(this, window, args);
    }
    public string Home => Storage.CodexHome(Settings);
    public void Show()
    {
        if (exiting) return;
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }
    public async Task RefreshAsync()
    {
        if (exiting || Refreshing || DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromSeconds(15)) return;
        if (Demo) { LoadExample(); UpdateTray(); Changed?.Invoke(); return; }
        Refreshing = true; lastAttempt = DateTimeOffset.UtcNow;
        var home = Home;
        var identity = CodexClient.Identity(home);
        if (Storage.Read<CachedUsage>("usage.json") is { } old && old.Identity != identity) Usage = null;
        Error = null; Changed?.Invoke();
        var quota = FetchQuota(home, identity);
        var local = FetchHistory(home);
        active = Task.WhenAll(quota, local);
        try { await active; }
        finally
        {
            Refreshing = false;
            NextRefresh = DateTimeOffset.UtcNow.AddMinutes(Math.Min(30, Settings.RefreshMinutes * Math.Pow(2, Math.Min(failures, 2))));
            UpdateTray(); Changed?.Invoke();
        }
    }
    private void LoadExample()
    {
        Usage = ExampleData.Quota(); History = ExampleData.History();
        HistoryStatus = "Offline example data"; Error = null;
        NextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
    }
    private async Task FetchQuota(string home, string identity)
    {
        try
        {
            var result = await client.ReadAsync(home, lifetime.Token);
            if (Home != home || identity != CodexClient.Identity(home)) { Error = "Your Codex account changed during refresh. Refresh again."; return; }
            Usage = result; failures = 0; Error = null;
            Storage.Write("usage.json", new CachedUsage(identity, result));
            if (Settings.LowUsageNotification && !notified && result.Windows.Any(x => x.Remaining <= 10))
            {
                tray.ShowBalloonTip(5000, "Codex usage", "A Codex limit has 10% or less remaining.", Forms.ToolTipIcon.Info); notified = true;
            }
            if (result.Windows.All(x => x.Remaining > 10)) notified = false;
            UpdateTray(); Changed?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { Error = Program.SafeError(e); failures++; Changed?.Invoke(); }
    }
    private async Task FetchHistory(string home)
    {
        try
        {
            var result = await historyReader.ScanAsync(home, lifetime.Token);
            if (Home != home) return;
            History = result;
            HistoryStatus = result.Warning ?? $"{result.Files} local session files | Updated {result.ScannedAt.LocalDateTime:t}";
            Storage.Write("history.json", new CachedHistory(home, result));
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { HistoryStatus = "Local history could not be refreshed. Previous totals are retained."; Changed?.Invoke(); }
    }
    private void UpdateTray()
    {
        var values = Settings.PinnedMetrics.Take(2).Select(PinnedValue).Where(x => x is not null);
        var text = "Usage Dock | " + string.Join(" | ", values);
        if (Error is not null) text = "Usage Dock | Reading is stale. Open for details.";
        tray.Text = text.Length > 127 ? text[..127] : text;
        string? PinnedValue(string name)
        {
            if (Usage?.Windows.FirstOrDefault(w => w.Name == name) is { } window)
                return $"{name}: {(Settings.ShowRemaining ? window.Remaining : window.UsedPercent):0}% {(Settings.ShowRemaining ? "left" : "used")}";
            if (name is "Today" or "Yesterday" or "Last 30 Days" or "All Time" && History is { } history)
                return name + ": " + Ui.Money((name == "All Time" ? history.Days : history.Period(name == "Last 30 Days" ? 30 : 1, name == "Yesterday")).Sum(d => d.EstimatedCost));
            return name switch {
                "Extra Usage" when Usage?.Credits is { } credits => $"Extra Usage: {credits:0} credits",
                "Rate Limit Resets" when Usage?.ResetCredits is { } resets => $"Resets: {resets} available",
                "Usage Trend" when History is { } local => "30 Days: " + Ui.Number(local.Period(30).Sum(d => d.Tokens)) + " tokens",
                _ => null
            };
        }
    }
    public void SaveSettings(AppSettings settings)
    {
        var previousHome = selectedHome;
        Settings = settings;
        selectedHome = Home;
        Storage.Write("settings.json", settings);
        window.Topmost = settings.AlwaysOnTop;
        if (Home != previousHome) { Usage = null; History = null; HistoryStatus = "Reading the selected Codex home..."; }
        NextRefresh = DateTimeOffset.UtcNow;
        Changed?.Invoke();
        if (!Refreshing) { lastAttempt = DateTimeOffset.MinValue; _ = RefreshAsync(); }
    }
    public void SaveDisplayPreferences() { Storage.Write("settings.json", Settings); UpdateTray(); }
    public async Task QuitAsync()
    {
        if (exiting) return;
        exiting = true; timer.Stop(); lifetime.Cancel(); tray.Visible = false;
        if (active is not null) try { await active; } catch (OperationCanceledException) { }
        window.AllowClose = true; window.Close(); Application.Current.Shutdown();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        timer.Stop(); lifetime.Cancel(); showWait.Unregister(null); showEvent.Dispose();
        quitWait.Unregister(null); quitEvent.Dispose(); refreshWait.Unregister(null); refreshEvent.Dispose();
        tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose();
        client.Dispose(); lifetime.Dispose();
    }
    internal object TrayEvidence() => TrayInspection.Read(tray);
    internal string TrayText => tray.Text;
}
