// OpenUsage Pace.swift, translated to C#. Copyright (c) 2026 Robin Ebers, MIT.
namespace UsageDock.Core;
public sealed record PaceResult(string Status, double ProjectedPercent, double? SecondsToLimit);
public static class Pace
{
    public static PaceResult? Evaluate(LimitWindow window, DateTimeOffset now)
    {
        if (window.UsedPercent <= 0 || window.DurationSeconds <= 0 || window.ResetsAt is not { } reset || now >= reset) return null;
        var elapsed = (now - reset.AddSeconds(-window.DurationSeconds)).TotalSeconds;
        if (elapsed < Math.Max(60, window.DurationSeconds * .01)) return null;
        var projected = window.UsedPercent / elapsed * window.DurationSeconds;
        var status = window.UsedPercent >= 100 || projected > 100 ? "behind" : projected > 90 ? "onTrack" : "ahead";
        var eta = (100 - window.UsedPercent) / (projected / window.DurationSeconds);
        return new(status, projected, eta > 0 && eta < (reset - now).TotalSeconds ? eta : null);
    }
}
