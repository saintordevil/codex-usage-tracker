// Adapted from OpenUsage CodexUsageMapper.swift, copyright (c) 2026 Robin Ebers, MIT.
namespace UsageDock.Core;

using System.Globalization;
using System.Text.Json;

public static class QuotaMapper
{
    public static UsageSnapshot Parse(JsonElement body, DateTimeOffset now, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Codex returned an invalid usage response.");
        var windows = new List<LimitWindow>();
        AddWindows(J.Get(body, "rate_limit"), "", windows, now, headers);
        foreach (var extra in J.Array(J.Get(body, "additional_rate_limits")))
        {
            var name = J.Text(extra, "limit_name") ?? J.Text(extra, "metered_feature") ?? "Model";
            if (name.Contains("spark", StringComparison.OrdinalIgnoreCase) || (J.Text(extra, "metered_feature")?.Contains("spark", StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            AddWindows(J.Get(extra, "rate_limit"), name + " ", windows, now);
        }
        var creditsObject = J.Get(body, "credits");
        var credits = J.Number(creditsObject, "balance");
        if (credits is null && J.Get(creditsObject, "has_credits").ValueKind == JsonValueKind.False) credits = 0;
        if (credits is null && headers?.TryGetValue("x-codex-credits-balance", out var raw) == true && double.TryParse(raw, CultureInfo.InvariantCulture, out var n)) credits = n;
        var resets = J.Number(J.Get(body, "rate_limit_reset_credits"), "available_count");
        if (windows.Count == 0 && J.Get(body, "rate_limit").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex did not return subscription limits. Open Codex and check your sign-in.");
        return new(PlanName(J.Text(body, "plan_type")), windows, credits, resets >= 0 ? (int)resets : null, now);
    }

    private static void AddWindows(JsonElement limits, string prefix, List<LimitWindow> output, DateTimeOffset now,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var candidates = new List<(JsonElement Value, string Fallback, double? Used)>();
        foreach (var (slot, fallback, header) in new[] { ("primary_window", "Session", "x-codex-primary-used-percent"), ("secondary_window", "Weekly", "x-codex-secondary-used-percent") })
        {
            var value = J.Get(limits, slot);
            double? used = J.Number(value, "used_percent");
            if (used is null && headers?.TryGetValue(header, out var h) == true && double.TryParse(h, CultureInfo.InvariantCulture, out var n)) used = n;
            if (value.ValueKind == JsonValueKind.Object || used is not null) candidates.Add((value, fallback, used));
        }
        foreach (var (kind, seconds) in new[] { ("Session", 18000L), ("Weekly", 604800L) })
        {
            var matching = candidates.Where(x => J.Number(x.Value, "limit_window_seconds") == seconds).ToList();
            if (matching.Count == 0) matching = candidates.Where(x => J.Number(x.Value, "limit_window_seconds") is not (18000 or 604800) && x.Fallback == kind).ToList();
            if (matching.FirstOrDefault() is { Used: { } used } found)
            {
                var reset = J.Date(J.Get(found.Value, "reset_at"));
                if (reset is null && J.Number(found.Value, "reset_after_seconds") is { } after && Math.Abs(after) < 315360000) reset = now.AddSeconds(after);
                output.Add(new(prefix + kind, used, (long)(J.Number(found.Value, "limit_window_seconds") ?? seconds), reset));
            }
        }
    }

    public static string PlanName(string? raw) => raw?.ToLowerInvariant() switch
    {
        "prolite" => "Pro 100", "pro" => "Pro 200", "promax" => "Pro 500", "self_serve_business_prolite" => "Business Premium",
        null or "" => "ChatGPT", _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' '))
    };

    public static UsageSnapshot FromAppServer(JsonElement result, string plan, DateTimeOffset now)
    {
        var windows = new List<LimitWindow>();
        var buckets = J.Get(result, "rateLimitsByLimitId");
        if (buckets.ValueKind != JsonValueKind.Object || !buckets.EnumerateObject().Any())
            Map(J.Get(result, "rateLimits"), "");
        else foreach (var bucket in buckets.EnumerateObject()) Map(bucket.Value, bucket.Name == "codex" ? "" : (J.Text(bucket.Value, "limitName") ?? bucket.Name) + " ");
        if (windows.Count == 0) throw new InvalidDataException("Codex did not return any subscription limit windows.");
        var resets = J.Number(J.Get(result, "rateLimitResetCredits"), "availableCount");
        var rate = J.Get(result, "rateLimits");
        var credits = J.Number(J.Get(rate, "credits"), "balance");
        var expiries = J.Array(J.Get(J.Get(result, "rateLimitResetCredits"), "credits")).Where(c => J.Text(c, "status") is null or "available")
            .Select(c => J.Date(J.Get(c, "expiresAt"))).Where(x => x is not null).Select(x => x!.Value).Order().ToList();
        return new(PlanName(plan), windows, credits, resets >= 0 ? (int)resets : null, now, "Codex app-server", expiries);
        void Map(JsonElement limit, string prefix)
        {
            if (prefix.Contains("spark", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var (key, fallback) in new[] { ("primary", "Session"), ("secondary", "Weekly") })
            {
                var w = J.Get(limit, key);
                if (J.Number(w, "usedPercent") is not { } used) continue;
                var duration = (long)(J.Number(w, "windowDurationMins") ?? (fallback == "Session" ? 300 : 10080)) * 60;
                var name = duration == 604800 ? "Weekly" : duration == 18000 ? "Session" : fallback;
                windows.Add(new(prefix + name, used, duration, J.Date(J.Get(w, "resetsAt"))));
            }
        }
    }
}
