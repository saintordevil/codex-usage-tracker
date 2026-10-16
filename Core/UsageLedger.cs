using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZstdSharp;

namespace UsageDock.Core;

// A metadata-only, durable ledger. Source histories are never written or unpacked to disk.
internal sealed class UsageLedger(AppStorage storage, Pricing pricing)
{
    private sealed class Cursor
    {
        public long Offset { get; set; }
        public long Line { get; set; }
        public string Model { get; set; } = "unknown";
        public string Tier { get; set; } = "standard";
        public string Session { get; set; } = "";
        public TokenCounts? Previous { get; set; }
        public int Skipped { get; set; }
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Vector(TokenCounts? t) => t is null ? "" : $"{t.Input}:{t.Cached}:{t.Written}:{t.Output}:{t.Reasoning}:{t.Total}";
    public HistorySnapshot Scan(string home, CancellationToken cancellation, IProgress<string>? progress)
    {
        var homeKey = Hash(Path.GetFullPath(home).ToUpperInvariant());
        using var db = new LocalDatabase(Path.Combine(storage.Root, "usage-ledger-" + homeKey[..16] + ".sqlite"));
        db.Execute("""
            CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY, length INTEGER, ticks INTEGER, prefix TEXT, compressed INTEGER, cursor TEXT);
            CREATE TABLE IF NOT EXISTS parents(session TEXT PRIMARY KEY, parent TEXT);
            CREATE TABLE IF NOT EXISTS responses(id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY, signature TEXT, family TEXT, file TEXT, line INTEGER, kind TEXT,
                at TEXT, day TEXT, model TEXT, input INTEGER, cached INTEGER, output INTEGER, reasoning INTEGER, total INTEGER, cost REAL, unpriced INTEGER, tier TEXT);
            CREATE INDEX IF NOT EXISTS event_signature ON events(signature);
            CREATE INDEX IF NOT EXISTS event_file_line ON events(file,line);
            CREATE INDEX IF NOT EXISTS event_day_model ON events(day,model);
            """);
        using var getFile = db.Prepare("SELECT length,ticks,prefix,compressed,cursor FROM files WHERE id=?");
        using var putFile = db.Prepare("INSERT OR REPLACE INTO files VALUES(?,?,?,?,?,?)");
        using var putParent = db.Prepare("INSERT OR REPLACE INTO parents VALUES(?,?)");
        using var getParent = db.Prepare("SELECT parent FROM parents WHERE session=?");
        using var signatures = db.Prepare("SELECT family,at,id,model FROM events WHERE signature=?");
        using var responseSeen = db.Prepare("SELECT id FROM responses WHERE id=?");
        using var responseSaved = db.Prepare("INSERT OR IGNORE INTO responses VALUES(?)");
        using var near = db.Prepare("SELECT id FROM events WHERE file=? AND kind=? AND line BETWEEN ? AND ? AND input=? AND cached=? AND output=? AND reasoning=? LIMIT 1");
        using var remove = db.Prepare("DELETE FROM events WHERE id=?");
        using var put = db.Prepare("INSERT OR IGNORE INTO events VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)");
        int files = 0, errors = 0, skipped = 0; bool capped = false;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var sources = new[] { "sessions", "archived_sessions" }.Select(x => Path.Combine(home, x)).Where(Directory.Exists)
            .SelectMany(x => Directory.EnumerateFiles(x, "*.jsonl*", options)).Where(x => x.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).Take(10001).ToArray();
        foreach (var path in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            if (files >= 10000) { capped = true; break; }
            bool compressed = path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
            string fileId = Hash(Path.GetFullPath(compressed ? path[..^4] : path).ToUpperInvariant());
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
                long length = file.Length, ticks = File.GetLastWriteTimeUtc(path).Ticks;
                var prefix = new byte[(int)Math.Min(length, 2048)]; file.ReadExactly(prefix); file.Position = 0;
                var digest = Convert.ToHexString(SHA256.HashData(prefix));
                var cursor = new Cursor();
                if (getFile.Bind(fileId).Step())
                {
                    if (length == getFile.Long(0) && ticks == getFile.Long(1) && digest == getFile.Text(2) && (compressed ? 1 : 0) == getFile.Long(3))
                    { files++; skipped += JsonSerializer.Deserialize<Cursor>(getFile.Text(4))?.Skipped ?? 0; continue; }
                    if (!compressed && getFile.Long(3) == 0 && length >= getFile.Long(0) && digest == getFile.Text(2))
                        cursor = JsonSerializer.Deserialize<Cursor>(getFile.Text(4)) ?? new();
                }
                getFile.Bind(fileId); // Release read cursor before writing.
                using var decoder = compressed ? new DecompressionStream(file, bufferSize: 65536) : null;
                if (decoder is not null) decoder.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 27);
                Stream input = decoder ?? (Stream)file;
                if (!compressed) input.Position = cursor.Offset;
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    Read(input, cursor, Process, cancellation);
                    putFile.Bind(fileId, length, ticks, digest, compressed ? 1 : 0, JsonSerializer.Serialize(cursor)).Step();
                    db.Execute("COMMIT");
                }
                catch { db.Execute("ROLLBACK"); throw; }
                files++; skipped += cursor.Skipped;
                if (files % 20 == 0) progress?.Report($"Indexing local history: {files:N0} / {sources.Length:N0} files");
                void Process(ReadOnlyMemory<byte> line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement; var type = J.Text(root, "type"); var p = J.Get(root, "payload");
                    if (type == "session_meta")
                    {
                        cursor.Session = Hash(J.Text(p, "id") ?? fileId);
                        var parent = J.Text(p, "forked_from_id") ?? J.Text(p, "parent_thread_id") ??
                            J.Text(J.Get(J.Get(J.Get(p, "source"), "subagent"), "thread_spawn"), "parent_thread_id");
                        putParent.Bind(cursor.Session, parent is null ? "" : Hash(parent)).Step(); return;
                    }
                    if (type == "turn_context")
                    {
                        cursor.Model = J.Text(p, "model") ?? J.Text(p, "model_name") ?? cursor.Model;
                        cursor.Tier = J.Text(p, "service_tier") ?? "standard"; return;
                    }
                    var eventType = J.Text(p, "type");
                    if (eventType == "thread_settings_applied")
                    {
                        var settings = J.Get(p, "thread_settings"); if (settings.ValueKind != JsonValueKind.Object) settings = J.Get(p, "settings");
                        cursor.Tier = J.Text(settings, "service_tier") ?? J.Text(p, "service_tier") ?? "standard"; return;
                    }
                    if (J.Date(J.Get(root, "timestamp")) is not { } at) return;
                    TokenCounts usage, last; string signature, kind, id; long? requestInput;
                    if (type == "token_usage_record")
                    {
                        usage = last = TokenCounts.Parse(J.Get(p, "usage"));
                        signature = "response:" + Hash(J.Text(p, "response_id") ?? fileId + ":" + cursor.Line);
                        kind = "response"; id = signature; requestInput = usage.Input;
                        if (responseSeen.Bind(id).Step()) { responseSeen.Bind(id); return; }
                        responseSeen.Bind(id); responseSaved.Bind(id).Step();
                        if (near.Bind(fileId, "snapshot", cursor.Line - 12, cursor.Line + 12, usage.Input, usage.Cached, usage.Output, usage.Reasoning).Step()) return;
                    }
                    else if (type == "event_msg" && eventType == "token_count")
                    {
                        var info = J.Get(p, "info"); var totalElement = J.Get(info, "total_token_usage");
                        var totals = totalElement.ValueKind == JsonValueKind.Object ? TokenCounts.Parse(totalElement) : null;
                        var previous = cursor.Previous; if (totals is not null) cursor.Previous = totals;
                        if (totals is not null && totals == previous) return;
                        var lastElement = J.Get(info, "last_token_usage");
                        last = lastElement.ValueKind == JsonValueKind.Object ? TokenCounts.Parse(lastElement) : totals?.Subtract(previous) ?? new(0,0,0,0,0);
                        bool reset = totals is not null && previous is not null && (totals.Input < previous.Input || totals.Output < previous.Output || totals.Cached < previous.Cached);
                        usage = totals is not null && previous is not null && !reset ? totals.Subtract(previous) : last;
                        cursor.Model = J.Text(p, "model") ?? J.Text(info, "model") ?? cursor.Model;
                        signature = Hash(Vector(totals) + ":" + Vector(last));
                        string family = Family(cursor.Session.Length == 0 ? fileId : cursor.Session);
                        bool duplicate = false; string? replaceUnknown = null;
                        signatures.Bind(signature);
                        while (signatures.Step()) if (Family(signatures.Text(0)) == family || signatures.Text(1) == at.ToString("O"))
                        {
                            if (signatures.Text(3) == "unknown" && cursor.Model != "unknown") replaceUnknown = signatures.Text(2);
                            else if (cursor.Model == "unknown" || signatures.Text(3) == cursor.Model) duplicate = true;
                        }
                        signatures.Bind(signature);
                        if (replaceUnknown is not null) remove.Bind(replaceUnknown).Step();
                        kind = "snapshot"; id = Hash(family + ":" + signature + ":" + cursor.Model); requestInput = last.Input;
                        // A per-response record and its following token snapshot describe the same request.
                        if (near.Bind(fileId, "response", cursor.Line - 12, cursor.Line + 12, last.Input, last.Cached, last.Output, last.Reasoning).Step())
                        { var responseId = near.Text(0); near.Bind(fileId, "response", 0, 0, 0, 0, 0, 0); remove.Bind(responseId).Step(); }
                        if (duplicate) return;
                    }
                    else return;
                    if (usage.Input + usage.Output == 0 || cursor.Model.Contains("spark", StringComparison.OrdinalIgnoreCase)) return;
                    var e = new UsageEvent(at, cursor.Model, usage.Input, Math.Min(usage.Input, usage.Cached), usage.Output, usage.Reasoning,
                        usage.Input + usage.Output, cursor.Tier, usage.Written, requestInput);
                    var cost = pricing.Cost(e);
                    put.Bind(id, signature, cursor.Session.Length == 0 ? fileId : cursor.Session, fileId, cursor.Line, kind, at.ToString("O"), at.LocalDateTime.ToString("yyyy-MM-dd"),
                        e.Model, e.Input, e.Cached, e.Output, e.Reasoning, e.Total, cost, cost is null ? 1 : 0, e.Tier).Step();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ZstdException) { errors++; }
        }
        // Optional numeric-only import supplied locally by the current user.
        if (storage.Read<List<HelperRecord>>("imported-helper-usage.json") is { } imported)
            foreach (var helper in imported.Take(10000)) AddHelper(helper);
        var defaultHome = Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        var helperRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex", "Logs");
        if (string.Equals(Path.GetFullPath(home), defaultHome, StringComparison.OrdinalIgnoreCase) && Directory.Exists(helperRoot))
        foreach (var path in Directory.EnumerateFiles(helperRoot, "*.log", options).Take(1000))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                string key = "helper:" + Hash(path); var length = new FileInfo(path).Length; var ticks = File.GetLastWriteTimeUtc(path).Ticks;
                var cursor = new Cursor();
                if (getFile.Bind(key).Step())
                {
                    if (length == getFile.Long(0) && ticks == getFile.Long(1)) continue;
                    if (length >= getFile.Long(0)) cursor = JsonSerializer.Deserialize<Cursor>(getFile.Text(4)) ?? new();
                }
                getFile.Bind(key);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
                input.Position = cursor.Offset; db.Execute("BEGIN IMMEDIATE");
                try
                {
                    Read(input, cursor, line =>
                    {
                        var text = Encoding.UTF8.GetString(line.Span);
                        var stamp = Regex.Match(text, @"\d{4}-\d{2}-\d{2}T[\d:.]+Z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                        if (!DateTimeOffset.TryParse(stamp.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return;
                        var model = Field("model"); if (model is null) return;
                        AddHelper(new(at, model, Field("feature") ?? "helper", Count("inputTokens", "input_tokens"), Count("cachedInputTokens", "cached_input_tokens"), Count("outputTokens", "output_tokens"), Count("reasoningOutputTokens", "reasoning_output_tokens"), Count("cacheWriteInputTokens", "cache_write_input_tokens")));
                        string? Field(string name) => Regex.Match(text, @"\b" + name + "[=:]\\s*\"?([^\\s,}\"\\]]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) is { Success: true } match ? match.Groups[1].Value : null;
                        long Count(string camel, string snake) => long.TryParse(Field(camel) ?? Field(snake), out var n) ? Math.Max(0, n) : 0;
                    }, cancellation, helper: true);
                    putFile.Bind(key, length, ticks, "", 0, JsonSerializer.Serialize(cursor)).Step(); db.Execute("COMMIT");
                }
                catch { db.Execute("ROLLBACK"); throw; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { errors++; }
        }
        var days = new List<DayUsage>();
        using (var rows = db.Prepare("SELECT day,model,sum(input),sum(cached),sum(output),sum(reasoning),sum(total),count(*),coalesce(sum(cost),0),sum(unpriced) FROM events GROUP BY day,model ORDER BY day,model"))
            while (rows.Step()) days.Add(new() { Day = rows.Text(0), Model = rows.Text(1), Input = rows.Long(2), Cached = rows.Long(3), Output = rows.Long(4), Reasoning = rows.Long(5), Tokens = rows.Long(6), Requests = (int)rows.Long(7), EstimatedCost = rows.Double(8), UnpricedRequests = (int)rows.Long(9) });
        string? warning = capped ? "History reached the file safety limit; totals are partial." : errors > 0 ? $"{errors} history files could not be read; saved usage is retained." : skipped > 0 ? $"{skipped} malformed usage records were skipped; totals may be partial." : null;
        return new(days, DateTimeOffset.UtcNow, files, skipped, warning) { AllTimeReady = true };
        void AddHelper(HelperRecord h)
        {
            if (h.Input + h.Output == 0 || h.Model.Contains("spark", StringComparison.OrdinalIgnoreCase)) return;
            var e = new UsageEvent(h.At, h.Model, h.Input, Math.Min(h.Input,h.Cached), h.Output, h.Reasoning, h.Input + h.Output, "standard", h.Written);
            var id = "helper:" + Hash(h.At.ToUniversalTime().ToString("O") + ":" + h.Model + ":" + h.Feature + ":" + h.Input + ":" + h.Cached + ":" + h.Output);
            var cost = pricing.Cost(e);
            put.Bind(id,id,"helper","helper",0,"helper",h.At.ToUniversalTime().ToString("O"),h.At.LocalDateTime.ToString("yyyy-MM-dd"),h.Model,e.Input,e.Cached,e.Output,e.Reasoning,e.Total,cost,cost is null?1:0,"standard").Step();
        }
        string Family(string session)
        {
            var seen = new HashSet<string>();
            while (seen.Count < 64 && seen.Add(session) && getParent.Bind(session).Step())
            { var parent = getParent.Text(0); if (parent.Length == 0) break; session = parent; }
            getParent.Bind(session); return session;
        }
    }
    private static void Read(Stream input, Cursor cursor, Action<ReadOnlyMemory<byte>> process, CancellationToken token, bool helper = false)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var line = new MemoryStream();
        bool discarded = false, oversized = false; long position = cursor.Offset, yieldBytes = 0;
        try
        {
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested(); int start = 0;
                for (int i = 0; i < count; i++) if (buffer[i] == 10)
                {
                    Append(buffer.AsSpan(start, i - start)); position += i - start + 1; cursor.Line++;
                    if (oversized) cursor.Skipped++;
                    else if (!discarded && line.Length > 0 && (helper ? line.GetBuffer().AsSpan(0, (int)line.Length).IndexOf("ephemeral_generation_token_usage"u8) >= 0 : HistoryReader.Relevant(line.GetBuffer().AsSpan(0, (int)line.Length))))
                        try { process(line.GetBuffer().AsMemory(0, (int)line.Length)); } catch (JsonException) { cursor.Skipped++; }
                    cursor.Offset = position; line.SetLength(0); discarded = oversized = false; start = i + 1;
                }
                Append(buffer.AsSpan(start, count - start)); position += count - start;
                yieldBytes += count; if (yieldBytes >= 4 * 1024 * 1024) { Thread.Sleep(1); yieldBytes = 0; }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        void Append(ReadOnlySpan<byte> data)
        {
            if (discarded || oversized) return;
            if (line.Length + data.Length > HistoryReader.MaxRecordBytes) { oversized = true; line.SetLength(0); return; }
            line.Write(data);
            if (line.Length >= 4096 && (helper ? line.GetBuffer().AsSpan(0, 4096).IndexOf("ephemeral_generation_token_usage"u8) < 0 : !HistoryReader.MayContainUsage(line.GetBuffer().AsSpan(0, 4096)))) { discarded = true; line.SetLength(0); }
        }
    }
}
public sealed record HelperRecord(DateTimeOffset At, string Model, string Feature, long Input, long Cached, long Output, long Reasoning, long Written);
