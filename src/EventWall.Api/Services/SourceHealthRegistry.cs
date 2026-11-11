using System.Collections.Concurrent;

namespace EventWall.Api.Services;

public sealed record SourceHealth(string State, string Detail, DateTime? LastSuccessfulUtc);

public sealed class SourceHealthRegistry
{
    private readonly ConcurrentDictionary<string, SourceHealth> _states = new(StringComparer.OrdinalIgnoreCase);

    public SourceHealth? Get(string provider) =>
        _states.TryGetValue(provider, out var health) ? health : null;

    public void Report(string provider, string state, string detail, DateTime? lastSuccessfulUtc = null)
    {
        var previous = Get(provider);
        _states[provider] = new SourceHealth(
            state,
            detail,
            lastSuccessfulUtc ?? previous?.LastSuccessfulUtc);
    }
}
