namespace AtmCash.Application.Common;

public static class TimeProviderExtensions
{
    /// <summary>Canonical UTC precision for timestamps that round-trip through PostgreSQL.</summary>
    public static DateTimeOffset GetOperationTime(this TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
