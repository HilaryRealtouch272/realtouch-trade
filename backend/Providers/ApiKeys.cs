namespace RealtouchSmartTrade.Api.Providers;

// Shared helper: reads "<Section>:ApiKey" and/or "<Section>:ApiKeys" from
// configuration, where either may hold a single key or a comma-separated
// list. Used by every provider that supports key rotation on failure.
public static class ApiKeys
{
    public static List<string> Get(IConfiguration config, string section)
    {
        var keys = new List<string>();
        foreach (var source in new[] { config[$"{section}:ApiKey"], config[$"{section}:ApiKeys"] })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            foreach (var k in source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (!keys.Contains(k)) keys.Add(k);
        }
        return keys;
    }
}
