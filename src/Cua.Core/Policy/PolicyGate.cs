using System.Text.RegularExpressions;
using Cua.Core.Artifacts;
using Cua.Core.Surface;

namespace Cua.Core.Policy;

/// <summary>How the executing side treats risky/irreversible actions.</summary>
public enum RiskyActionMode
{
    /// <summary>Refuse the action outright.</summary>
    Block,
    /// <summary>Pause and ask a human before executing.</summary>
    Confirm,
    /// <summary>Execute, but record the risk prominently in the log and artifact.</summary>
    Flag,
}

public sealed record PolicyConfig
{
    /// <summary>
    /// Allowlist tokens: host[:port] optionally followed by a path prefix for
    /// web kinds (e.g. "127.0.0.1:8080/portal" restricts to routes under
    /// /portal; a bare host allows all routes); process/app name for desktop.
    /// </summary>
    public required IReadOnlyList<string> AllowedHosts { get; init; }
    public SurfaceKind SurfaceKind { get; init; } = SurfaceKind.Web;
    public IReadOnlyList<StepAction> AllowedActions { get; init; } =
        [StepAction.Navigate, StepAction.Click, StepAction.Type, StepAction.Select, StepAction.Read, StepAction.Checkpoint];
    public RiskyActionMode RiskyMode { get; init; } = RiskyActionMode.Flag;
    /// <summary>Regexes matched against a clicked control's text: matching clicks are upgraded to Irreversible even if the model under-declares them.</summary>
    public IReadOnlyList<string> RiskyTextPatterns { get; init; } =
    [
        @"(?i)\bwaive\b", @"(?i)\bstop payment\b", @"(?i)\bcommit\b", @"(?i)\bconfirm\b",
        @"(?i)\bdelete\b", @"(?i)\bpost\b", @"(?i)\breverse\b", @"(?i)\bauthorize\b",
        @"(?i)\bsubmit\b", @"(?i)\bplace\b",
    ];
    public int MaxSteps { get; init; } = 40;
    public TimeSpan MaxRunTime { get; init; } = TimeSpan.FromMinutes(15);
}

public enum PolicyVerdict { Allowed, AllowedWithFlag, NeedsConfirmation, Blocked }

public sealed record PolicyDecision(PolicyVerdict Verdict, RiskLevel EffectiveRisk, string? Reason)
{
    public bool IsBlocked => Verdict == PolicyVerdict.Blocked;
}

/// <summary>
/// The gate every action passes through — model-proposed during discovery and
/// artifact-recorded during replay alike. The model never talks to the surface
/// directly; the gate sits between decision and execution.
/// </summary>
public sealed class PolicyGate(PolicyConfig config)
{
    public PolicyConfig Config { get; } = config;

    public PolicyDecision CheckNavigation(string url)
    {
        if (Config.SurfaceKind == SurfaceKind.Desktop)
            return CheckToken(SurfaceTarget.IdentityOf(url), "target");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new(PolicyVerdict.Blocked, RiskLevel.Safe, $"unparseable url: {url}");
        return IsHostAllowed(uri)
            ? new(PolicyVerdict.Allowed, RiskLevel.Safe, null)
            : new(PolicyVerdict.Blocked, RiskLevel.Safe,
                $"host '{uri.Authority}' is not in the allowlist [{string.Join(", ", Config.AllowedHosts)}]");
    }

    public PolicyDecision CheckAction(StepAction action, string currentUrl, RiskLevel declaredRisk, string? targetText)
    {
        if (!Config.AllowedActions.Contains(action))
            return new(PolicyVerdict.Blocked, declaredRisk, $"action '{action}' is not permitted by policy");

        if (Config.SurfaceKind == SurfaceKind.Desktop)
        {
            var token = string.IsNullOrWhiteSpace(currentUrl) ? "" : SurfaceTarget.IdentityOf(currentUrl);
            if (token.Length > 0)
            {
                var gate = CheckToken(token, "current app");
                if (gate.IsBlocked)
                    return new(PolicyVerdict.Blocked, declaredRisk, gate.Reason);
            }
        }
        else if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) && !IsHostAllowed(uri))
            return new(PolicyVerdict.Blocked, declaredRisk,
                $"current page host '{uri.Authority}' is outside the allowlist; refusing to act");

        var risk = EffectiveRisk(action, declaredRisk, targetText);
        if (risk != RiskLevel.Irreversible)
            return new(PolicyVerdict.Allowed, risk, null);

        return Config.RiskyMode switch
        {
            RiskyActionMode.Block => new(PolicyVerdict.Blocked, risk,
                $"irreversible action ('{targetText}') blocked by policy (risky_mode=block)"),
            RiskyActionMode.Confirm => new(PolicyVerdict.NeedsConfirmation, risk,
                $"irreversible action ('{targetText}') requires confirmation"),
            _ => new(PolicyVerdict.AllowedWithFlag, risk,
                $"irreversible action ('{targetText}') executed and flagged"),
        };
    }

    /// <summary>Risk can be upgraded by target-text patterns, never downgraded below the declared level.</summary>
    public RiskLevel EffectiveRisk(StepAction action, RiskLevel declared, string? targetText)
    {
        if (action != StepAction.Click || string.IsNullOrWhiteSpace(targetText)) return declared;
        var matched = Config.RiskyTextPatterns.Any(p => Regex.IsMatch(targetText, p));
        return matched && declared < RiskLevel.Irreversible ? RiskLevel.Irreversible : declared;
    }

    private PolicyDecision CheckToken(string token, string what)
    {
        if (IsTokenAllowed(token))
            return new(PolicyVerdict.Allowed, RiskLevel.Safe, null);
        return new(PolicyVerdict.Blocked, RiskLevel.Safe,
            $"{what} '{token}' is not in the allowlist [{string.Join(", ", Config.AllowedHosts)}]");
    }

    private bool IsTokenAllowed(string token) =>
        Config.AllowedHosts.Any(h => string.Equals(h, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Host must match an allowlist entry; when the matching entries carry a
    /// path prefix, the URL's path must fall under at least one of them. A
    /// bare-host entry allows every route on that host.
    /// </summary>
    private bool IsHostAllowed(Uri uri)
    {
        var anyHostMatch = false;
        foreach (var entry in Config.AllowedHosts)
        {
            var slash = entry.IndexOf('/');
            var authority = slash < 0 ? entry : entry[..slash];
            var pathPrefix = slash < 0 ? null : entry[slash..].TrimEnd('*');

            if (!string.Equals(authority, uri.Authority, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(authority, uri.Host, StringComparison.OrdinalIgnoreCase))
                continue;
            anyHostMatch = true;

            if (pathPrefix is null || pathPrefix == "/")
                return true;
            if (uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        _ = anyHostMatch; // host matched but every entry was route-scoped and none matched → blocked
        return false;
    }
}
