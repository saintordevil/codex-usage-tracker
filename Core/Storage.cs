using System.Text.Json;

namespace UsageDock.Core;

public sealed class AppSettings
{
    public string? CodexHome { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public bool ShowRemaining { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool LowUsageNotification { get; set; }
    public int PeriodDays { get; set; } = 1;
    public bool Yesterday { get; set; }
    public bool ShowTotalSpend { get; set; } = true;
    public bool Compact { get; set; }
    public bool AbsoluteResets { get; set; }
    public bool AlwaysShowPacing { get; set; }
    public bool Expanded { get; set; } = true;
    public bool ReduceAnimations { get; set; }
    public string Theme { get; set; } = "Dark";
    public string TotalMetric { get; set; } = "Cost";
    public string TimeFormat { get; set; } = "24-hour";
    public string TrayStyle { get; set; } = "Numbers";
    public List<string> HiddenMetrics { get; set; } = [];
    public List<string> PinnedMetrics { get; set; } = ["Session", "Weekly"];
    public int LayoutVersion { get; set; }
    public List<string> OnDemandMetrics { get; set; } = ["Extra Usage", "Rate Limit Resets", "Today", "Yesterday", "Last 30 Days", "All Time"];
    public List<string> MetricOrder { get; set; } = ["Session", "Weekly", "Usage Trend", "Extra Usage", "Rate Limit Resets", "Today", "Yesterday", "Last 30 Days", "All Time"];
    public void Normalize()
    {
        RefreshMinutes = Math.Clamp(RefreshMinutes, 1, 30);
        MetricOrder.RemoveAll(x => x.Contains("spark", StringComparison.OrdinalIgnoreCase));
        OnDemandMetrics.RemoveAll(x => x.Contains("spark", StringComparison.OrdinalIgnoreCase));
        HiddenMetrics.RemoveAll(x => x.Contains("spark", StringComparison.OrdinalIgnoreCase));
        PinnedMetrics.RemoveAll(x => x.Contains("spark", StringComparison.OrdinalIgnoreCase));
        if (LayoutVersion < 1)
        {
            if (!MetricOrder.Contains("All Time")) MetricOrder.Insert(MetricOrder.IndexOf("Last 30 Days") is var index && index >= 0 ? index + 1 : MetricOrder.Count, "All Time");
            if (!OnDemandMetrics.Contains("All Time")) OnDemandMetrics.Add("All Time");
            LayoutVersion = 1;
        }
    }
}

public sealed class AppStorage
{
    public string Root { get; }
    public AppStorage(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageDock");
        Directory.CreateDirectory(Root);
    }
    public T? Read<T>(string name)
    {
        try
        {
            var path = Path.Combine(Root, name);
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return default;
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(s);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    public void Write<T>(string name, T value)
    {
        var target = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(file, value);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string CodexHome(AppSettings settings) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.CodexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")));
}
