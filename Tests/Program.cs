using System.Net;
using System.Text;
using System.Text.Json;
using UsageDock.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action test) => tests.Add((name, () => { test(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> test) => tests.Add((name, test));
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
var now = DateTimeOffset.UtcNow;
string stamp = now.ToString("O");
string root = Path.Combine(Path.GetTempPath(), "UsageDock-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Event(long total, long last = 100, string? at = null) => JsonSerializer.Serialize(new { timestamp = at ?? stamp, type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = total, output_tokens = 0, total_tokens = total }, last_token_usage = new { input_tokens = last, total_tokens = last } } } });
string Context(string model = "gpt-6-astra") => JsonSerializer.Serialize(new { type = "turn_context", payload = new { model } });
UsageEvent? Parse(string text, LogParserState state) => LogParser.Parse(Encoding.UTF8.GetBytes(text), state);

Test("Weekly primary is not mislabeled as a session", () =>
{
    var result = QuotaMapper.Parse(Json("""{"plan_type":"prolite","rate_limit":{"primary_window":{"used_percent":37,"limit_window_seconds":604800,"reset_after_seconds":500},"secondary_window":null},"credits":{"balance":"0"},"rate_limit_reset_credits":{"available_count":1}}"""), now);
    Equal(1, result.Windows.Count); Equal("Weekly", result.Windows[0].Name); Equal(63d, result.Windows[0].Remaining); Equal("Pro 100", result.Plan); Equal(1, result.ResetCredits);
});
Test("Both windows and missing percentages", () =>
{
    var result = QuotaMapper.Parse(Json("""{"rate_limit":{"primary_window":{"used_percent":0,"limit_window_seconds":18000},"secondary_window":{"limit_window_seconds":604800}}}"""), now);
    Equal(1, result.Windows.Count); Equal(100d, result.Windows[0].Remaining);
});
Test("Header fallback and numeric strings", () =>
{
    var result = QuotaMapper.Parse(Json("""{"rate_limit":{"primary_window":{"used_percent":"12.5","reset_at":1900000000},"secondary_window":{}}}"""), now, new Dictionary<string, string> { ["x-codex-secondary-used-percent"] = "20" });
    Equal(2, result.Windows.Count); Equal(12.5, result.Windows[0].UsedPercent); Equal(20d, result.Windows[1].UsedPercent);
});
Test("Removed Spark buckets do not enter the data", () =>
{
    var result = QuotaMapper.Parse(Json("""{"rate_limit":{},"additional_rate_limits":[null,{"limit_name":"GPT-5.3-Codex-Spark","rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":18000}}}]}"""), now);
    Equal(0, result.Windows.Count);
});
Test("Raw over-limit values retained while remaining clamps", () =>
{
    var result = QuotaMapper.Parse(Json("""{"rate_limit":{"primary_window":{"used_percent":105}}}"""), now);
    Equal(105d, result.Windows[0].UsedPercent); Equal(0d, result.Windows[0].Remaining);
});
Test("Spark is removed from saved preferences and history without changing later usage", () =>
{
    var settings = new AppSettings();
    settings.MetricOrder.Add("Spark"); settings.HiddenMetrics.Add("Spark Weekly"); settings.PinnedMetrics.Add("Spark"); settings.OnDemandMetrics.Add("Spark Weekly"); settings.Normalize();
    Assert(!settings.MetricOrder.Concat(settings.HiddenMetrics).Concat(settings.PinnedMetrics).Concat(settings.OnDemandMetrics).Any(x => x.Contains("spark", StringComparison.OrdinalIgnoreCase)), "Spark preference survived");
    var state = new LogParserState(); Parse(Context("gpt-5.3-codex-spark"), state);
    Assert(Parse(Event(100), state) is null, "Spark history retained"); Parse(Context(), state);
    Equal(100L, Parse(Event(200), state)!.Total);
    var file = Path.Combine(root, "spark-cache.jsonl"); File.WriteAllText(file, Context() + "\n" + Event(100) + "\n");
    var cache = HistoryReader.ReadFile(file, null, now.AddDays(-1), default);
    cache.Events.Add(new(now, "gpt-5.3-codex-spark", 100, 0, 0, 0, 100, "standard"));
    cache = HistoryReader.ReadFile(file, cache, now.AddDays(-1), default); Equal(1, cache.Events.Count);
});
Test("Official account-reader mapping", () =>
{
    var result = QuotaMapper.FromAppServer(Json("""{"rateLimits":{"primary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1900000000}},"rateLimitResetCredits":{"availableCount":2}}"""), "pro", now);
    Equal("Weekly", result.Windows.Single().Name); Equal(2, result.ResetCredits);
});
Test("Repeated cumulative snapshots count once", () =>
{
    var s = new LogParserState(); Parse(Context(), s);
    Equal(100L, Parse(Event(100), s)!.Total); Assert(Parse(Event(100), s) is null, "Repeated event was counted");
    Equal(100L, Parse(Event(200), s)!.Total);
});
Test("Cumulative delta fallback", () =>
{
    var s = new LogParserState(); Parse(Context(), s);
    var first = JsonSerializer.Serialize(new { timestamp = stamp, type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 150, output_tokens = 20, total_tokens = 170 } } } });
    Equal(170L, Parse(first, s)!.Total);
    var next = first.Replace("150", "200").Replace("170", "220"); Equal(50L, Parse(next, s)!.Total);
});
Test("Fork replay seeds totals without charging parent history", () =>
{
    var s = new LogParserState();
    Parse(JsonSerializer.Serialize(new { timestamp = stamp, type = "session_meta", payload = new { id = "child", forked_from_id = "parent" } }), s);
    Assert(Parse(Event(500), s) is null, "Parent replay counted");
    Parse(JsonSerializer.Serialize(new { timestamp = stamp, type = "event_msg", payload = new { type = "task_started", started_at = now.ToUnixTimeSeconds() } }), s);
    Equal(100L, Parse(Event(600), s)!.Total);
});
Test("Null fork metadata leaves root sessions live", () =>
{
    var s = new LogParserState(); Parse("""{"type":"session_meta","payload":{"id":"root","forked_from_id":null}}""", s);
    Equal(100L, Parse(Event(100), s)!.Total);
});
Test("Partial JSONL tail is retried after append", () =>
{
    var file = Path.Combine(root, "tail.jsonl"); var text = Context() + "\n" + Event(100);
    File.WriteAllText(file, text[..^6]); var cache = HistoryReader.ReadFile(file, null, now.AddDays(-1), default); Equal(0, cache.Events.Count);
    File.AppendAllText(file, text[^6..] + "\n"); cache = HistoryReader.ReadFile(file, cache, now.AddDays(-1), default); Equal(1, cache.Events.Count);
    cache = HistoryReader.ReadFile(file, cache, now.AddDays(-1), default); Equal(1, cache.Events.Count);
});
Test("Truncated and rewritten logs rebuild the cache", () =>
{
    var file = Path.Combine(root, "rewrite.jsonl"); File.WriteAllText(file, Context() + "\n" + Event(100) + "\n" + Event(200) + "\n");
    var cache = HistoryReader.ReadFile(file, null, now.AddDays(-1), default); Equal(2, cache.Events.Count);
    File.WriteAllText(file, Context() + "\n" + Event(70, 70) + "\n"); cache = HistoryReader.ReadFile(file, cache, now.AddDays(-1), default);
    Equal(1, cache.Events.Count); Equal(70L, cache.Events[0].Total);
});
Test("Oversized line remains bounded and next usage survives", () =>
{
    var file = Path.Combine(root, "large.jsonl"); File.WriteAllText(file, new string('x', HistoryReader.MaxRecordBytes + 2) + "\n" + Context() + "\n" + Event(100) + "\n");
    var cache = HistoryReader.ReadFile(file, null, now.AddDays(-1), default); Equal(1, cache.Skipped); Equal(1, cache.Events.Count);
});
Test("Cached tokens are not billed twice", () =>
{
    var e = new UsageEvent(now, "gpt-6-astra", 1_000, 800, 100, 0, 1100, "standard");
    Assert(Math.Abs(new Pricing().Cost(e)!.Value - .0078) < 1e-10, "Cached price mismatch");
});
Test("Oversized conversation content does not imply missing usage", () =>
{
    var file = Path.Combine(root, "conversation.jsonl");
    File.WriteAllText(file, "{\"type\":\"response_item\",\"payload\":{\"text\":\"" + new string('x', HistoryReader.MaxRecordBytes + 1) + "\"}}\n" + Context() + "\n" + Event(100) + "\n");
    var cache = HistoryReader.ReadFile(file, null, now.AddDays(-1), default); Equal(0, cache.Skipped); Equal(1, cache.Events.Count);
});
Test("Long context and Ultrafast use published request-wide rates", () =>
{
    var pricing = new Pricing(); var e = new UsageEvent(now, "gpt-6.1-sol", 300000, 100000, 1000, 0, 301000, "ultrafast");
    Assert(Math.Abs(pricing.Cost(e)!.Value - 5.01) < 1e-10, "Long-context/Ultrafast mismatch");
});
Test("Auto-review is free after the upstream cutoff and unknown models stay unknown", () =>
{
    var pricing = new Pricing(); var e = new UsageEvent(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), "codex-auto-review", 500, 0, 50, 0, 550, "fast");
    Equal(0d, pricing.Cost(e)); Assert(pricing.Cost(e with { Model = "future-unpriced-model" }) is null, "Unknown price invented");
});
AsyncTest("Copied session files deduplicate across active and archived history", async () =>
{
    var home = Path.Combine(root, "home"); Directory.CreateDirectory(Path.Combine(home, "sessions")); Directory.CreateDirectory(Path.Combine(home, "archived_sessions"));
    var text = Context() + "\n" + Event(100) + "\n";
    File.WriteAllText(Path.Combine(home, "sessions", "a.jsonl"), text); File.WriteAllText(Path.Combine(home, "archived_sessions", "b.jsonl"), text);
    var reader = new HistoryReader(new AppStorage(Path.Combine(root, "state")), new Pricing());
    var result = await reader.ScanAsync(home, default); Equal(100L, result.Days.Sum(x => x.Tokens)); Equal(2, result.Files);
    var again = await reader.ScanAsync(home, default); Equal(100L, again.Days.Sum(x => x.Tokens));
});
AsyncTest("Usage transport makes only the expected GET with no model request", async () =>
{
    var home = Path.Combine(root, "auth"); Directory.CreateDirectory(home); File.WriteAllText(Path.Combine(home, "auth.json"), """{"tokens":{"access_token":"fixture-token","account_id":"fixture-account"}}""");
    var handler = new CaptureHandler(); using var client = new CodexClient(handler);
    var result = await client.ReadAsync(home, default);
    Equal(2, handler.Count); Equal(HttpMethod.Get, handler.Method); Equal("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", handler.Url);
    Equal(40d, result.Windows.Single().UsedPercent);
});
Test("CSV escapes formulas and retains complete token totals", () =>
{
    var file = Path.Combine(root, "export.csv"); CsvExport.Write(file, [new() { Day = "2026-10-12", Model = "=HYPERLINK(\"x\")", Tokens = 5 }]);
    var text = File.ReadAllText(file); Assert(text.Contains("\"'=HYPERLINK"), "Formula injection not neutralized"); Equal(2, File.ReadAllLines(file).Length);
});
Test("Malformed cache safely falls back", () =>
{
    var storage = new AppStorage(Path.Combine(root, "bad")); File.WriteAllText(Path.Combine(storage.Root, "settings.json"), "{broken"); Assert(storage.Read<AppSettings>("settings.json") is null, "Corrupt cache not rejected");
});

Test("Token fallback includes reasoning only once and preserves cache writes", () =>
{
    var t = TokenCounts.Parse(Json("""{"input_tokens":100,"output_tokens":40,"reasoning_output_tokens":30,"cache_write_input_tokens":20}"""));
    Equal(140L, t.Total); Equal(20L, t.Written);
});
Test("Historical API rates and explicit cache writes are retained", () =>
{
    var pricing = new Pricing();
    var e = new UsageEvent(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), "gpt-5.6-sol", 100000, 20000, 1000, 0, 101000, "standard", 10000);
    Assert(Math.Abs(pricing.Cost(e)!.Value - .4525) < 1e-10, "Historical rates/cache writes differ");
    Assert(pricing.Cost(e with { At = now }) < pricing.Cost(e), "Price-cut date ignored");
});
Test("Existing layouts gain All Time immediately after Last 30 Days", () =>
{
    var s = new AppSettings { MetricOrder = ["Weekly", "Last 30 Days", "Today"], OnDemandMetrics = ["Last 30 Days"] };
    s.Normalize(); s.Normalize(); Equal("All Time", s.MetricOrder[2]); Equal(1, s.MetricOrder.Count(x => x == "All Time")); Assert(s.OnDemandMetrics.Contains("All Time"), "Placement not migrated");
});
AsyncTest("Compressed old history survives source removal and does not enter Today", async () =>
{
    var home = Path.Combine(root, "old-compressed"); Directory.CreateDirectory(Path.Combine(home,"sessions"));
    var source = Path.Combine(home,"sessions","old.jsonl.zst");
    var text = Context() + "\n" + Event(140,140,now.AddMonths(-5).ToString("O")) + "\n";
    using (var file = File.Create(source)) using (var zstd = new ZstdSharp.CompressionStream(file)) zstd.Write(Encoding.UTF8.GetBytes(text));
    var reader = new HistoryReader(new AppStorage(Path.Combine(root,"old-compressed-state")),new Pricing());
    var scan = await reader.ScanAsync(home,default); Equal(140L,scan.Days.Sum(x=>x.Tokens)); Equal(0,scan.Period(1).Count); Assert(scan.AllTimeReady,"All-time not ready");
    File.Delete(source); var again = await reader.ScanAsync(home,default); Equal(140L,again.Days.Sum(x=>x.Tokens));
});
AsyncTest("Ledger resumes incomplete tails and raw-to-compressed migration without duplicates", async () =>
{
    var home=Path.Combine(root,"migration"); Directory.CreateDirectory(Path.Combine(home,"sessions")); var path=Path.Combine(home,"sessions","file.jsonl");
    var reader=new HistoryReader(new AppStorage(Path.Combine(root,"migration-state")),new Pricing());
    File.WriteAllText(path,Context()+"\n"+Event(100)+"\n"+Event(200)[..^2]);
    Equal(100L,(await reader.ScanAsync(home,default)).Days.Sum(x=>x.Tokens));
    File.AppendAllText(path,Event(200)[^2..]+"\n"); Equal(200L,(await reader.ScanAsync(home,default)).Days.Sum(x=>x.Tokens));
    using(var input=File.OpenRead(path)) using(var output=File.Create(path+".zst")) using(var zstd=new ZstdSharp.CompressionStream(output)) input.CopyTo(zstd);
    File.Delete(path); Equal(200L,(await reader.ScanAsync(home,default)).Days.Sum(x=>x.Tokens));
});
AsyncTest("Fork copies lacking model context do not inflate all-time tokens", async () =>
{
    var home=Path.Combine(root,"lineage"); Directory.CreateDirectory(Path.Combine(home,"sessions"));
    File.WriteAllText(Path.Combine(home,"sessions","a.jsonl"),"""{"type":"session_meta","payload":{"id":"parent"}}"""+"\n"+Context()+"\n"+Event(100)+"\n");
    File.WriteAllText(Path.Combine(home,"sessions","b.jsonl"),"""{"type":"session_meta","payload":{"id":"child","forked_from_id":"parent"}}"""+"\n"+Event(100,100,now.AddMinutes(1).ToString("O"))+"\n"+Context()+"\n"+Event(200,100,now.AddMinutes(2).ToString("O"))+"\n");
    var scan=await new HistoryReader(new AppStorage(Path.Combine(root,"lineage-state")),new Pricing()).ScanAsync(home,default);
    Equal(200L,scan.Days.Sum(x=>x.Tokens)); Assert(scan.Days.All(x=>x.Model!="unknown"),"Inherited unknown baseline charged");
});
AsyncTest("Independent equal numeric requests remain distinct", async () =>
{
    var home=Path.Combine(root,"independent"); Directory.CreateDirectory(Path.Combine(home,"sessions"));
    foreach(var i in new[]{0,1}) File.WriteAllText(Path.Combine(home,"sessions",i+".jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id="independent"+i}})+"\n"+Context()+"\n"+Event(100,100,now.AddMinutes(i).ToString("O"))+"\n");
    var scan=await new HistoryReader(new AppStorage(Path.Combine(root,"independent-state")),new Pricing()).ScanAsync(home,default); Equal(200L,scan.Days.Sum(x=>x.Tokens));
});
AsyncTest("Response-only compaction usage is included and paired records count once", async () =>
{
    var home=Path.Combine(root,"response"); Directory.CreateDirectory(Path.Combine(home,"sessions")); var path=Path.Combine(home,"sessions","a.jsonl");
    string Response(string id,long input) => JsonSerializer.Serialize(new {timestamp=stamp,type="token_usage_record",payload=new{response_id=id,usage=new{input_tokens=input,total_tokens=input}}});
    File.WriteAllText(path,Context()+"\n"+Response("paired",100)+"\n"+Event(100)+"\n"+Response("only",70)+"\n");
    var reader=new HistoryReader(new AppStorage(Path.Combine(root,"response-state")),new Pricing());
    Equal(170L,(await reader.ScanAsync(home,default)).Days.Sum(x=>x.Tokens));
    File.Copy(path,Path.Combine(home,"sessions","copy.jsonl")); Equal(170L,(await reader.ScanAsync(home,default)).Days.Sum(x=>x.Tokens));
});
AsyncTest("Cumulative gaps and resets retain actual input and output", async () =>
{
    var home=Path.Combine(root,"gaps"); Directory.CreateDirectory(Path.Combine(home,"sessions"));
    File.WriteAllText(Path.Combine(home,"sessions","a.jsonl"),Context()+"\n"+Event(100)+"\n"+Event(300,100)+"\n"+Event(70,70)+"\n");
    var result=await new HistoryReader(new AppStorage(Path.Combine(root,"gaps-state")),new Pricing()).ScanAsync(home,default); Equal(370L,result.Days.Sum(x=>x.Tokens));
});

int failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
Directory.Delete(root, true);
return failed == 0 ? 0 : 1;

sealed class CaptureHandler : HttpMessageHandler
{
    public int Count; public HttpMethod? Method; public string? Url;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++; Method = request.Method; Url = request.RequestUri?.AbsoluteUri;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"rate_limit":{"primary_window":{"used_percent":40}}}""") });
    }
}
