using System.Globalization;
using System.Text.Json;

namespace UsageDock.Core;

public sealed record LimitWindow(string Name, double UsedPercent, long DurationSeconds, DateTimeOffset? ResetsAt)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
}
public sealed record UsageSnapshot(string Plan, List<LimitWindow> Windows, double? Credits, int? ResetCredits,
    DateTimeOffset FetchedAt, string Source = "Codex subscription", List<DateTimeOffset>? ResetExpiries = null);
public sealed record UsageEvent(DateTimeOffset At, string Model, long Input, long Cached, long Output,
    long Reasoning, long Total, string Tier, long Written = 0, long? RequestInput = null);
public sealed record TokenCounts(long Input, long Cached, long Output, long Reasoning, long Total, long Written = 0)
{
    public static TokenCounts Parse(JsonElement value)
    {
        long N(params string[] names) => names.Select(n => J.Number(value, n)).FirstOrDefault(x => x.HasValue) is { } n ? Math.Max(0, (long)n) : 0;
        var input = N("input_tokens", "prompt_tokens", "input");
        var output = N("output_tokens", "completion_tokens", "output");
        var reasoning = N("reasoning_output_tokens", "reasoning_tokens");
        return new(input, N("cached_input_tokens", "cache_read_input_tokens", "cached_tokens"), output,
            reasoning, J.Number(value, "total_tokens") is { } total ? Math.Max(0, (long)total) : input + output, N("cache_write_input_tokens"));
    }
    public TokenCounts Subtract(TokenCounts? previous) => new(Math.Max(0, Input - (previous?.Input ?? 0)),
        Math.Max(0, Cached - (previous?.Cached ?? 0)), Math.Max(0, Output - (previous?.Output ?? 0)),
        Math.Max(0, Reasoning - (previous?.Reasoning ?? 0)), Math.Max(0, Total - (previous?.Total ?? 0)), Math.Max(0, Written - (previous?.Written ?? 0)));
}
public sealed class DayUsage
{
    public string Day { get; set; } = "";
    public string Model { get; set; } = "";
    public long Tokens { get; set; }
    public long Input { get; set; }
    public long Cached { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public int Requests { get; set; }
    public double EstimatedCost { get; set; }
    public int UnpricedRequests { get; set; }
}
public sealed record HistorySnapshot(List<DayUsage> Days, DateTimeOffset ScannedAt, int Files, int SkippedRecords, string? Warning)
{
    public bool AllTimeReady { get; init; }
    public string? FirstDay => Days.Count == 0 ? null : Days.Min(x => x.Day);
    public List<DayUsage> Period(int days, bool yesterday = false)
    {
        var end = DateTime.Today.AddDays(yesterday ? -1 : 0);
        var start = end.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var finish = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Days.Where(x => string.CompareOrdinal(x.Day, start) >= 0 && string.CompareOrdinal(x.Day, finish) <= 0).ToList();
    }
}
public static class J
{
    public static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) ? p : default;
    public static string? Text(JsonElement value, string key) => Text(Get(value, key));
    public static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static double? Number(JsonElement value, string key) => Number(Get(value, key));
    public static double? Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n)) return n;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) ? n : null;
    }
    public static IEnumerable<JsonElement> Array(JsonElement v) => v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    public static DateTimeOffset? Date(JsonElement v)
    {
        if (Text(v) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        if (Number(v) is { } n && n >= -62135596800 && n <= 253402300799) return DateTimeOffset.FromUnixTimeSeconds((long)n);
        return null;
    }
}
