using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageDock.Core;

public sealed class LogCache
{
    public int Version { get; set; } = 2;
    public long Offset { get; set; }
    public long ObservedLength { get; set; }
    public long WriteTicks { get; set; }
    public string PrefixHash { get; set; } = "";
    public int PrefixLength { get; set; }
    public LogParserState State { get; set; } = new();
    public List<UsageEvent> Events { get; set; } = [];
    public int Skipped { get; set; }
}

public sealed class HistoryReader(AppStorage storage, Pricing pricing)
{
    public const int MaxRecordBytes = 4 * 1024 * 1024;
    public const int MaxEvents = 200000;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<HistorySnapshot> ScanAsync(string home, CancellationToken token, IProgress<string>? progress = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => new UsageLedger(storage, pricing).Scan(home, token, progress), token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public static LogCache ReadFile(string path, LogCache? cache, DateTimeOffset cutoff, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        var length = stream.Length;
        var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        bool valid = cache is { Version: 2 } && cache.Offset <= length && cache.ObservedLength <= length &&
            (cache.ObservedLength != length || cache.WriteTicks == writeTicks) && cache.PrefixLength <= length;
        if (valid)
        {
            var prefix = new byte[cache!.PrefixLength];
            stream.ReadExactly(prefix);
            valid = Convert.ToHexString(SHA256.HashData(prefix)) == cache.PrefixHash;
        }
        if (!valid)
        {
            stream.Position = 0;
            var prefix = new byte[(int)Math.Min(2048, length)];
            stream.ReadExactly(prefix);
            cache = new() { PrefixHash = Convert.ToHexString(SHA256.HashData(prefix)), PrefixLength = prefix.Length };
        }
        var result = cache!;
        result.Events.RemoveAll(e => e.At < cutoff || e.Model.Contains("spark", StringComparison.OrdinalIgnoreCase));
        stream.Position = result.Offset;
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var line = new MemoryStream();
        bool overflow = false, irrelevant = false; long position = result.Offset; long sinceYield = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    Append(buffer.AsSpan(start, i - start));
                    position += i - start + 1;
                    if (irrelevant) { /* Known conversation content needs no parsing or large buffer. */ }
                    else if (overflow) { if (MayContainUsage(line.GetBuffer().AsSpan(0, (int)line.Length))) result.Skipped++; }
                    else if (Relevant(line.GetBuffer().AsSpan(0, (int)line.Length)))
                    {
                        try
                        {
                            var e = LogParser.Parse(line.GetBuffer().AsMemory(0, (int)line.Length), result.State);
                            if (e is not null && e.At >= cutoff)
                            {
                                if (result.Events.Count >= MaxEvents) throw new InvalidDataException("Per-file history limit exceeded.");
                                result.Events.Add(e);
                            }
                        }
                        catch (JsonException) { result.Skipped++; }
                    }
                    line.SetLength(0); overflow = false; irrelevant = false;
                    result.Offset = position;
                    start = i + 1;
                }
                Append(buffer.AsSpan(start, read - start));
                position += read - start;
                sinceYield += read;
                if (sinceYield >= 4 * 1024 * 1024) { Thread.Sleep(1); sinceYield = 0; }
            }
            // Incomplete tail is deliberately retried on the next scan, including partial UTF-8.
            result.ObservedLength = length;
            result.WriteTicks = writeTicks;
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        void Append(ReadOnlySpan<byte> bytes)
        {
            if (overflow || irrelevant) return;
            if (line.Length + bytes.Length > MaxRecordBytes) { overflow = true; line.SetLength(Math.Min(line.Length, 4096)); return; }
            line.Write(bytes);
            if (line.Length >= 4096 && !MayContainUsage(line.GetBuffer().AsSpan(0, 4096)))
            { irrelevant = true; line.SetLength(0); }
        }
    }
    internal static bool Relevant(ReadOnlySpan<byte> data) => data.IndexOf("\"token_usage_record\""u8) >= 0 || data.IndexOf("\"token_count\""u8) >= 0 || data.IndexOf("\"turn_context\""u8) >= 0 ||
        data.IndexOf("\"session_meta\""u8) >= 0 || data.IndexOf("\"task_started\""u8) >= 0 || data.IndexOf("\"thread_settings_applied\""u8) >= 0;
    internal static bool MayContainUsage(ReadOnlySpan<byte> prefix)
    {
        try
        {
            var reader = new Utf8JsonReader(prefix, false, default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("type"u8)) continue;
                var depth = reader.CurrentDepth;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) continue;
                var type = reader.GetString();
                if (depth == 1 && type != "event_msg") return type is "turn_context" or "session_meta" or "token_usage_record";
                if (depth == 2) return type is "token_count" or "task_started" or "thread_settings_applied";
            }
        }
        catch (JsonException) { }
        return true;
    }

}
