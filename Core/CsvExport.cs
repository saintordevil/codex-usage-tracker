using System.Globalization;
using System.Text;

namespace UsageDock.Core;

public static class CsvExport
{
    public static void Write(string path, IEnumerable<DayUsage> days)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Date,Model,Total tokens,Input tokens,Cached input tokens,Output tokens,Reasoning tokens,Usage records,Estimated USD,Unpriced records");
        foreach (var d in days)
        {
            var model = d.Model;
            if (model.Length > 0 && "=+-@\t\r".Contains(model[0])) model = "'" + model;
            writer.WriteLine(string.Join(",", d.Day, "\"" + model.Replace("\"", "\"\"") + "\"", d.Tokens.ToString(CultureInfo.InvariantCulture), d.Input.ToString(CultureInfo.InvariantCulture),
                d.Cached.ToString(CultureInfo.InvariantCulture), d.Output.ToString(CultureInfo.InvariantCulture), d.Reasoning.ToString(CultureInfo.InvariantCulture),
                d.Requests.ToString(CultureInfo.InvariantCulture), d.EstimatedCost.ToString("0.000000", CultureInfo.InvariantCulture), d.UnpricedRequests.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
