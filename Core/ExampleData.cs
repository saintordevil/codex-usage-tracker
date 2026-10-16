namespace UsageDock.Core;

/// <summary>Synthetic, reproducible preview data. Never reads a file or account.</summary>
public static class ExampleData
{
    public static UsageSnapshot Quota() => new("Example", [
        new("Session", 27, 18000, DateTimeOffset.UtcNow.AddHours(3)),
        new("Weekly", 42, 604800, DateTimeOffset.UtcNow.AddDays(4))
    ], 0, 2, DateTimeOffset.UtcNow, "Offline example");

    public static HistorySnapshot History()
    {
        var pricing = new Pricing();
        var rows = new List<DayUsage>();
        for (int day = 0; day < 90; day++)
        {
            var date = DateTime.Today.AddDays(-day);
            foreach (var (model, index) in new[] { ("gpt-6-astra", 1), ("gpt-6.1-sol", 2), ("gpt-6-luna", 3) })
            {
                var requests = 12 + (day * 7 + index * 5) % 31;
                long input = 48000L * requests, cached = 32000L * requests, output = 6000L * requests;
                var usage = new UsageEvent(new DateTimeOffset(date), model, input, cached, output, output / 2, input + output, "standard", RequestInput: 48000);
                rows.Add(new DayUsage { Day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Model = model,
                    Input = input, Cached = cached, Output = output, Reasoning = output / 2, Tokens = input + output,
                    Requests = requests, EstimatedCost = pricing.Cost(usage) ?? 0 });
            }
        }
        return new(rows, DateTimeOffset.UtcNow, 90, 0, null) { AllTimeReady = true };
    }
}
