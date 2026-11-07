using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealtouchSmartTrade.Api.Services;

internal static class ScanSnapshotCache
{
    public static Dictionary<(string Symbol, string Timeframe), OrchestratorResult> Load(string outputPath, string cachePath)
    {
        var path = File.Exists(outputPath) ? outputPath : cachePath;
        if (!File.Exists(path)) return new();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var results = document.RootElement.GetProperty("results").Deserialize<List<OrchestratorResult>>(options) ?? new();
        var snapshot = new Dictionary<(string, string), OrchestratorResult>();
        foreach (var result in results) snapshot[(result.InstrumentSymbol, result.Timeframe)] = result;
        return snapshot;
    }
}
