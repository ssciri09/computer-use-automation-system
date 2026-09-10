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
        try
        {
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
            Console.WriteLine("│ You now control the LIVE session the automation was using.");
            Console.WriteLine("│ Perform the manual steps in that window. Supported surfaces record your actions.");
            Console.WriteLine("│ Then type notes (optional) and press ENTER to hand control back,");
            Console.WriteLine("│ or type 'abort' to abandon the run.");
            Console.WriteLine("└────────────────────────────────────────────────────────────────────────────────────");
            Console.Write("operator> ");
            var input = Console.ReadLine()?.Trim() ?? "";
            var aborted = input.Equals("abort", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new InterventionResolution
            {
                Resolved = !aborted,
                OperatorNotes = aborted ? "operator aborted the run" : (input.Length > 0 ? input : null),
            });
        }
        finally
        {
            control.TransferTo(ControlHolder.Automation);
        }
    }
}

/// <summary>
/// Attended handoff for coordinators that cannot write to the process stdin.
/// The live process remains paused with the human control token until an
/// external operator surface creates the advertised .resume signal file.
/// </summary>
public sealed class SignalFileOperatorChannel(string signalDir) : IOperatorChannel
{
    public async Task<InterventionResolution> RequestInterventionAsync(
        InterventionRequest request, SessionControl control, CancellationToken ct)
    {
        Directory.CreateDirectory(signalDir);
        var stem = $"intervention-{request.RunId}-{request.StepId}";
        var requestPath = Path.Combine(signalDir, stem + ".json");
        var resumePath = Path.Combine(signalDir, stem + ".resume");
        if (File.Exists(resumePath)) File.Delete(resumePath);
        File.WriteAllText(requestPath, Artifacts.CuaJson.Serialize(request));

        control.TransferTo(ControlHolder.Human);
        try
        {
            Console.WriteLine();
            Console.WriteLine("HUMAN INTERVENTION REQUIRED");
            Console.WriteLine($"  request: {requestPath}");
            Console.WriteLine("  operate the live session, then create this signal file:");
            Console.WriteLine($"  resume:  {resumePath}");

            while (!File.Exists(resumePath))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct);
            }

            var input = (await File.ReadAllTextAsync(resumePath, ct)).Trim();
            File.Delete(resumePath);
            var aborted = input.Equals("abort", StringComparison.OrdinalIgnoreCase);
            return new InterventionResolution
            {
                Resolved = !aborted,
                OperatorNotes = aborted
                    ? "operator aborted the run"
                    : (input.Length > 0 ? input : "operator signaled handback"),
            };
        }
        finally
        {
            control.TransferTo(ControlHolder.Automation);
        }
    }
}

/// <summary>
/// Unattended notification mock: persist the request and report the run as
/// EscalationPending. Evidence stays on disk, but the live process/session is
/// not retained after the caller disposes the surface. A resumable operator
/// service would own that lifetime and signal resolution through this seam.
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
