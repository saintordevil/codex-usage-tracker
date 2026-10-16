// Codex adjustments adapted from OpenUsage CodexUsagePricing.swift (MIT).
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageDock.Core;

public sealed record Price(double Input, double Output, double Cached);
public sealed class Pricing
{
    public const string SnapshotDate = "2026-10-12";
    private readonly Dictionary<string, Price> rates;
    public Pricing()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsageDock.Core.pricing.json")!;
        rates = JsonSerializer.Deserialize<Dictionary<string, Price>>(s) ?? [];
    }
    public double? Cost(UsageEvent e)
    {
        if (e.Model == "codex-auto-review" && e.At >= new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)) return 0;
        var name = e.Model switch { "gpt-reserve" => "gpt-5.6-luna", "daybreak-blue-latest" => "gpt-5.6-sol", _ => e.Model };
        if (name == "codex-auto-review") name = e.At >= new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero) ? "gpt-5.6-luna" : "gpt-5.5";
        bool fastAlias = name.EndsWith("-fast", StringComparison.Ordinal);
        if (fastAlias) name = name[..^5];
        name = Regex.Replace(name, @"-(none|low|medium|high|xhigh|max|ultra)$", "", RegexOptions.CultureInvariant);
        var basis = Regex.Replace(name, @"-\d{4}-?\d{2}-?\d{2}$", "", RegexOptions.CultureInvariant);
        if (!rates.TryGetValue(name, out var price) && !rates.TryGetValue(basis, out price)) return null;
        var input = price.Input; var output = price.Output; var cached = price.Cached;
        // Preserve the historical API counterfactual across the documented 2026 price cuts.
        if (basis == "gpt-5.6-sol" && e.At < new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero)) { input = 5; cached = .5; output = 30; }
        if (basis == "gpt-5.6-terra" && e.At < new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)) { input = 2.5; cached = .25; output = 15; }
        if (basis == "gpt-5.6-luna" && e.At < new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)) { input = 1; cached = .1; output = 6; }
        if ((e.RequestInput ?? e.Input) > 272000)
        {
            var longContext = basis.StartsWith("gpt-5.6-", StringComparison.Ordinal) ? new Price(input * 2, output * 1.5, cached * 2) : basis switch
            {
                "gpt-5.4" => new Price(5, 22.5, .5), "gpt-5.4-pro" or "gpt-5.5-pro" => new Price(60, 270, 60),
                "gpt-5.5" => new Price(10, 45, 1), "gpt-5.6-sol" => new Price(8, 30, .8),
                "gpt-5.6-terra" => new Price(4, 18, .4), "gpt-5.6-luna" => new Price(.4, 1.8, .04),
                "gpt-6-astra" => new Price(20, 75, 2), "gpt-6.1-sol" => new Price(4, 15, .2),
                "gpt-6-sol" => new Price(4, 15, .4), "gpt-6-luna" => new Price(.2, .75, .02), _ => price
            };
            input = longContext.Input; output = longContext.Output; cached = longContext.Cached;
        }
        if (basis is "gpt-5.4-pro" or "gpt-5.5-pro") cached = input;
        var multiplier = e.Tier == "ultrafast" && basis is "gpt-6-astra" or "gpt-6.1-sol" ? 6 :
            fastAlias || e.Tier is "fast" or "priority" or "ultrafast" ? (basis is "gpt-5.5" or "gpt-5.5-pro" ? 2.5 : 2) : 1;
        var read = Math.Min(e.Input, e.Cached);
        var written = Math.Min(Math.Max(0, e.Input - read), e.Written);
        var writeRate = basis.StartsWith("gpt-6", StringComparison.Ordinal) || basis.StartsWith("gpt-5.6-", StringComparison.Ordinal) ? input * 1.25 : input;
        return ((Math.Max(0, e.Input - read - written) * input) + read * cached + written * writeRate + e.Output * output) / 1_000_000 * multiplier;
    }
}
