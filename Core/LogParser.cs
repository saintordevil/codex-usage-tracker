// Port of OpenUsage CodexLogFileParser.swift and RawUsage (MIT, Robin Ebers).
using System.Text.Json;

namespace UsageDock.Core;

public sealed class LogParserState
{
    public TokenCounts? Previous { get; set; }
    public string Model { get; set; } = "unknown";
    public string Tier { get; set; } = "standard";
    public bool SawMeta { get; set; }
    public bool ReplayGate { get; set; }
    public double? Created { get; set; }
    public string SessionId { get; set; } = "";
}

public static class LogParser
{
    public static UsageEvent? Parse(ReadOnlyMemory<byte> data, LogParserState state)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var type = J.Text(root, "type");
        var payload = J.Get(root, "payload");
        if (type == "turn_context")
        {
            state.Model = Model(payload) ?? state.Model;
            // Newer Codex versions also record the tier directly on turn context.
            state.Tier = J.Text(payload, "service_tier") ?? state.Tier;
            return null;
        }
        if (type == "session_meta" && !state.SawMeta)
        {
            state.SawMeta = true;
            state.SessionId = J.Text(payload, "id") ?? "";
            state.ReplayGate = NonNull(J.Get(payload, "forked_from_id")) || NonNull(J.Get(payload, "parent_thread_id")) ||
                J.Text(payload, "thread_source") == "subagent" || NonNull(J.Get(J.Get(payload, "source"), "subagent"));
            var created = J.Date(J.Get(root, "timestamp"));
            state.Created = created?.ToUnixTimeSeconds();
            return null;
        }
        if (type != "event_msg") return null;
        var eventType = J.Text(payload, "type");
        if (eventType == "thread_settings_applied")
        {
            state.Tier = J.Text(J.Get(payload, "thread_settings"), "service_tier") ?? J.Text(payload, "service_tier") ?? state.Tier;
            return null;
        }
        if (eventType == "task_started")
        {
            var started = J.Number(payload, "started_at");
            var boundary = state.Created ?? J.Date(J.Get(root, "timestamp"))?.ToUnixTimeSeconds();
            if (state.ReplayGate && started >= boundary) state.ReplayGate = false;
            return null;
        }
        if (eventType != "token_count" || J.Date(J.Get(root, "timestamp")) is not { } at) return null;
        var info = J.Get(payload, "info");
        var totalElement = J.Get(info, "total_token_usage");
        var totals = totalElement.ValueKind == JsonValueKind.Object ? TokenCounts.Parse(totalElement) : null;
        if (state.ReplayGate)
        {
            if (totals is not null) state.Previous = totals;
            return null;
        }
        if (totals is not null && totals == state.Previous) return null;
        var last = J.Get(info, "last_token_usage");
        var usage = last.ValueKind == JsonValueKind.Object ? TokenCounts.Parse(last) : totals?.Subtract(state.Previous);
        if (totals is not null) state.Previous = totals;
        if (usage is null || usage.Total == 0) return null;
        state.Model = Model(payload) ?? Model(info) ?? state.Model;
        // Keep cumulative bookkeeping intact when an excluded model preceded a normal turn.
        if (state.Model.Contains("spark", StringComparison.OrdinalIgnoreCase)) return null;
        return new(at, state.Model, usage.Input, Math.Min(usage.Cached, usage.Input), usage.Output, usage.Reasoning, usage.Total, state.Tier);
    }
    private static string? Model(JsonElement p) => J.Text(p, "model") ?? J.Text(p, "model_name") ?? J.Text(J.Get(p, "metadata"), "model");
    private static bool NonNull(JsonElement value) => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));
}
