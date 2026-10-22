using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PersonalAgent.Core.Diagnostics;

public enum AgentOperation { Run, Model, Tool, Mcp, Gui, Screenshot, GuiBusyAdmission, PauseGateClose, Resume, FailureAfterResume, ScheduleDuplicate, DeliveryAttempt, DeliveryRetry }
public enum AgentOperationOutcome { Success, Failed, Blocked, Cancelled, Unknown }

// Fixed enum tags only. No payloads, destinations or high-cardinality run IDs enter metrics.
// Listener failures must never change task/control/dispatch outcomes.
public static class AgentTelemetry
{
    public const string MeterName = "PersonalAgent";
    private static readonly Counter<long>? Operations;
    private static readonly Histogram<double>? Duration;
    private static readonly Counter<long>? Input, Output;
    private static readonly Counter<double>? Cost;
    static AgentTelemetry()
    {
        try
        {
            var meter = new Meter(MeterName, "1.0");
            Operations = meter.CreateCounter<long>("personalagent.operations");
            Duration = meter.CreateHistogram<double>("personalagent.duration", "s");
            Input = meter.CreateCounter<long>("personalagent.tokens.input");
            Output = meter.CreateCounter<long>("personalagent.tokens.output");
            Cost = meter.CreateCounter<double>("personalagent.cost.estimate");
        }
        catch (Exception) { } // InstrumentPublished callbacks cannot break static initialization.
    }

    public static Measurement Measure(AgentOperation operation) => new(operation);
    public static void Record(AgentOperation operation, AgentOperationOutcome outcome = AgentOperationOutcome.Success, TimeSpan? duration = null)
    {
        try
        {
            var tags = new TagList { { "operation", operation.ToString() }, { "outcome", outcome.ToString() } };
            Operations?.Add(1, tags);
            if (duration is { } elapsed && elapsed >= TimeSpan.Zero) Duration?.Record(elapsed.TotalSeconds, tags);
        }
        catch (Exception) { }
    }
    public static void Usage(long input, long output, decimal? cost, string? currency)
    {
        try
        {
            if (input < 0 || output < 0) return;
            Input?.Add(input); Output?.Add(output);
            if (cost is >= 0) Cost?.Add((double)cost.Value, new KeyValuePair<string, object?>("currency", currency is "USD" or "EUR" or "GBP" ? currency : "Other"));
        }
        catch (Exception) { }
    }
    public sealed class Measurement(AgentOperation operation) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public AgentOperationOutcome Outcome { get; set; } = AgentOperationOutcome.Failed;
        public void Dispose() => Record(operation, Outcome, Stopwatch.GetElapsedTime(started));
    }
}
