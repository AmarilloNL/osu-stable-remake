using System.Diagnostics.Tracing;

namespace OsuStableRemake;

// Diagnostic (ALLOC_PROFILE=1 on a test run): samples .NET allocation ticks (~every 100KB) by type during play,
// printed at exit - for keeping per-frame allocations (GC hitches) out of gameplay.
sealed class AllocProfile : EventListener
{
    public static AllocProfile? Instance;
    readonly Dictionary<string, long> bytes = [];
    public volatile bool On;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!On || e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload == null) return;
        int ti = e.PayloadNames!.IndexOf("TypeName"), ai = e.PayloadNames.IndexOf("AllocationAmount64");
        string type = ti >= 0 ? e.Payload[ti]?.ToString() ?? "?" : "?";
        long amount = ai >= 0 ? Convert.ToInt64(e.Payload[ai]) : 100_000;
        lock (bytes) bytes[type] = bytes.GetValueOrDefault(type) + amount;
    }

    public void Print()
    {
        lock (bytes)
            foreach (var (t, b) in bytes.OrderByDescending(p => p.Value).Take(25))
                Console.Error.WriteLine($"alloc {b / 1048576.0,8:0.0} MB  {t}");
    }
}
