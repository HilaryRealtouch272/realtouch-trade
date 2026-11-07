using System.Text.Json;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class ScanSnapshotCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-snapshot-" + Guid.NewGuid().ToString("N"));

    private string Write(string name, params OrchestratorResult[] results)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new { results }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return path;
    }

    [Fact]
    public void StatelessRunsRestorePriorResultsFromThePersistedCache()
    {
        var cached = Write("prior-signals.json", new OrchestratorResult(false, null, "Earlier evaluation", "GBP/USD", "Daily"));
        var restored = ScanSnapshotCache.Load(Path.Combine(_root, "signals.json"), cached);
        Assert.Equal("Earlier evaluation", restored[("GBP/USD", "Daily")].Reason);
        Assert.Null(restored[("GBP/USD", "Daily")].Candles);
    }

    [Fact]
    public void ACurrentOutputTakesPrecedenceAndEmptyFirstRunsStayEmpty()
    {
        var missing = Path.Combine(_root, "missing.json");
        Assert.Empty(ScanSnapshotCache.Load(missing, missing));
        var cached = Write("prior.json", new OrchestratorResult(false, null, "Cached", "EUR/USD", "Daily"));
        var current = Write("signals.json", new OrchestratorResult(false, null, "Current", "EUR/USD", "Daily"));
        Assert.Equal("Current", ScanSnapshotCache.Load(current, cached)[("EUR/USD", "Daily")].Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
