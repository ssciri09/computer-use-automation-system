namespace Cua.Core.Hitl;

/// <summary>
/// Who currently controls the live session. The automation must hold control
/// to act; during a human takeover the engine refuses to drive the surface,
/// and the surface records what the human does instead.
/// </summary>
public enum ControlHolder { Automation, Human }

public sealed class SessionControl
{
    private readonly object _lock = new();
    public ControlHolder Holder { get; private set; } = ControlHolder.Automation;

    public event Action<ControlHolder>? Changed;

    public void TransferTo(ControlHolder holder)
    {
        lock (_lock)
        {
            if (Holder == holder) return;
            Holder = holder;
        }
        Changed?.Invoke(holder);
    }

    public void AssertAutomationHasControl()
    {
        if (Holder != ControlHolder.Automation)
            throw new InvalidOperationException(
                "automation attempted to act while a human holds session control");
    }
}

/// <summary>Everything an operator needs to act on an intervention without the run's context.</summary>
public sealed record InterventionRequest
{
    public required string RunId { get; init; }
    public required string CapabilityId { get; init; }
    public required string Goal { get; init; }
    public required string StepId { get; init; }
    public required string Reason { get; init; }
    public string? Detail { get; init; }
    public string? ScreenshotPath { get; init; }
    /// <summary>Compact digest of the current UI state (already redacted).</summary>
    public string? ObservationDigest { get; init; }
    /// <summary>What the run will do once control is handed back.</summary>
    public string? ResumePlan { get; init; }
    public DateTimeOffset RaisedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record InterventionResolution
{
    public required bool Resolved { get; init; }
    public string? OperatorNotes { get; init; }
}

/// <summary>
/// The seam to the operator side. Console implementation = a live attended
/// takeover of the same browser session; queue implementation = unattended
/// mode, where the request is persisted and the run ends EscalationPending.
/// A production operator console plugs in here without touching the engine.
/// </summary>
public interface IOperatorChannel
{
    Task<InterventionResolution> RequestInterventionAsync(
        InterventionRequest request, SessionControl control, CancellationToken ct);
}

/// <summary>
/// Attended mode: hands control of the live (headed) browser to the operator
/// at the terminal, waits for them to finish, then hands control back.
/// </summary>
public sealed class ConsoleOperatorChannel : IOperatorChannel
{
    public Task<InterventionResolution> RequestInterventionAsync(
        InterventionRequest request, SessionControl control, CancellationToken ct)
    {
        control.TransferTo(ControlHolder.Human);
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────  HUMAN INTERVENTION REQUIRED  ──────────────────────────");
        Console.WriteLine($"│ capability : {request.CapabilityId}   run {request.RunId}");
        Console.WriteLine($"│ goal       : {request.Goal}");
        Console.WriteLine($"│ at step    : {request.StepId}");
        Console.WriteLine($"│ reason     : {request.Reason}");
        if (request.Detail is not null) Console.WriteLine($"│ detail     : {request.Detail}");
        if (request.ScreenshotPath is not null) Console.WriteLine($"│ screenshot : {request.ScreenshotPath}");
        if (request.ResumePlan is not null) Console.WriteLine($"│ on resume  : {request.ResumePlan}");
        Console.WriteLine("│");
        Console.WriteLine("│ You now control the LIVE browser session the automation was using.");
        Console.WriteLine("│ Perform the manual steps in that window. Your actions are recorded.");
        Console.WriteLine("│ Then type notes (optional) and press ENTER to hand control back,");
        Console.WriteLine("│ or type 'abort' to abandon the run.");
        Console.WriteLine("└────────────────────────────────────────────────────────────────────────────────────");
        Console.Write("operator> ");
        var input = Console.ReadLine()?.Trim() ?? "";
        control.TransferTo(ControlHolder.Automation);
        var aborted = input.Equals("abort", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new InterventionResolution
        {
            Resolved = !aborted,
            OperatorNotes = aborted ? "operator aborted the run" : (input.Length > 0 ? input : null),
        });
    }
}

/// <summary>
/// Unattended mode: persist the request (a real operator console would consume
/// this queue) and report the run as EscalationPending. Session context and
/// evidence stay on disk for the operator.
/// </summary>
public sealed class QueueOperatorChannel(string queueDir) : IOperatorChannel
{
    public Task<InterventionResolution> RequestInterventionAsync(
        InterventionRequest request, SessionControl control, CancellationToken ct)
    {
        Directory.CreateDirectory(queueDir);
        var path = Path.Combine(queueDir, $"intervention-{request.RunId}-{request.StepId}.json");
        File.WriteAllText(path, Artifacts.CuaJson.Serialize(request));
        Console.WriteLine($"  [escalation] intervention request queued at {path}");
        return Task.FromResult(new InterventionResolution
        {
            Resolved = false,
            OperatorNotes = $"queued for operator at {path}",
        });
    }
}
