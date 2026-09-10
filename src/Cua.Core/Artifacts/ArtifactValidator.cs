using System.Text.RegularExpressions;

namespace Cua.Core.Artifacts;

/// <summary>
/// Structural and semantic validation for the executable artifact contract.
/// Deserialization alone cannot catch dangling resume targets, malformed
/// locator chains, or output values that contradict the declared schema.
/// </summary>
public static class ArtifactValidator
{
    public static void Validate(CapabilityArtifact artifact)
    {
        var errors = new List<string>();

        if (artifact.SchemaVersion != "1.0")
            errors.Add($"unsupported schema_version '{artifact.SchemaVersion}'");
        if (string.IsNullOrWhiteSpace(artifact.CapabilityId))
            errors.Add("capability_id is required");
        if (artifact.CapabilityVersion < 1)
            errors.Add("capability_version must be positive");
        if (artifact.Surface.Allowlist.Count == 0)
            errors.Add("surface.allowlist must contain at least one target");
        if (artifact.Surface.AllowedActions.Count == 0)
            errors.Add("surface.allowed_actions must contain at least one action");
        if (string.IsNullOrWhiteSpace(artifact.Surface.EntryUrl))
            errors.Add("surface.entry_url is required");
        if (artifact.Steps.Count == 0)
            errors.Add("at least one step is required");

        DuplicateNames(artifact.Inputs.Select(x => x.Name), "input", errors);
        DuplicateNames(artifact.Outputs.Select(x => x.Name), "output", errors);
        DuplicateNames(artifact.Steps.Select(x => x.Id), "step", errors);

        var stepIds = artifact.Steps.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var outputDefs = artifact.Outputs
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var input in artifact.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                errors.Add("input name cannot be empty");
            ValidateRegex(input.Pattern, $"input '{input.Name}' pattern", errors);
        }

        foreach (var step in artifact.Steps)
        {
            var label = string.IsNullOrWhiteSpace(step.Id) ? "(empty)" : step.Id;
            if (string.IsNullOrWhiteSpace(step.Id))
                errors.Add("step id cannot be empty");
            if (step.TimeoutMs <= 0)
                errors.Add($"step '{label}' timeout_ms must be positive");
            if (!artifact.Surface.AllowedActions.Contains(step.Action))
                errors.Add($"step '{label}' uses action {step.Action} outside surface.allowed_actions");

            if (step.Action is StepAction.Click or StepAction.Type or StepAction.Select or StepAction.Read)
            {
                if (step.Locator is null || step.Locator.Candidates.Count == 0)
                    errors.Add($"step '{label}' action {step.Action} requires a nonempty locator chain");
            }
            if (step.Action == StepAction.Navigate && string.IsNullOrWhiteSpace(step.Url))
                errors.Add($"step '{label}' navigate action requires url");
            if (step.Action is StepAction.Type or StepAction.Select &&
                step.Value is null && step.ValueRef is null)
                errors.Add($"step '{label}' action {step.Action} requires value or value_ref");

            if (step.WaitAfter is { } wait)
            {
                if (string.IsNullOrWhiteSpace(wait.Locator.Value))
                    errors.Add($"step '{label}' wait locator value cannot be empty");
                if (wait.TimeoutMs <= 0)
                    errors.Add($"step '{label}' wait timeout_ms must be positive");
            }

            foreach (var assertion in step.Assertions)
            {
                if (string.IsNullOrWhiteSpace(assertion.When.Value))
                    errors.Add($"step '{label}' assertion condition value cannot be empty");
                if (assertion.Retry is { } retry)
                {
                    if (assertion.Classify != AssertionClass.Recoverable)
                        errors.Add($"step '{label}' retry is only valid for a recoverable assertion");
                    if (retry.MaxAttempts < 0 || retry.BackoffMs.Any(x => x < 0))
                        errors.Add($"step '{label}' retry values cannot be negative");
                }
                if (assertion.Escalate?.ResumeAt is { } resumeAt && !stepIds.Contains(resumeAt))
                    errors.Add($"step '{label}' resume_at references unknown step '{resumeAt}'");

                if (artifact.Outputs.Count > 0 && assertion.Emit is { } emit)
                {
                    foreach (var (name, value) in emit)
                    {
                        if (!outputDefs.TryGetValue(name, out var output))
                        {
                            errors.Add($"step '{label}' emits undeclared output '{name}'");
                            continue;
                        }
                        if (output.EnumValues is { Count: > 0 } allowed && !allowed.Contains(value))
                            errors.Add($"step '{label}' emits invalid value '{value}' for output '{name}'");
                    }
                }
            }

            foreach (var extract in step.Extracts)
            {
                if (string.IsNullOrWhiteSpace(extract.Name))
                    errors.Add($"step '{label}' extract name cannot be empty");
                ValidateRegex(extract.Regex, $"step '{label}' extract '{extract.Name}'", errors);
                if (artifact.Outputs.Count > 0 && !outputDefs.ContainsKey(extract.Name))
                    errors.Add($"step '{label}' extracts undeclared output '{extract.Name}'");
            }
        }

        foreach (var guard in artifact.Guards)
        {
            if (string.IsNullOrWhiteSpace(guard.Id))
                errors.Add("guard id cannot be empty");
            if (string.IsNullOrWhiteSpace(guard.When.Value))
                errors.Add($"guard '{guard.Id}' condition value cannot be empty");
            if (guard.Escalation?.ResumeAt is { } resumeAt && !stepIds.Contains(resumeAt))
                errors.Add($"guard '{guard.Id}' resume_at references unknown step '{resumeAt}'");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "artifact validation failed: " + string.Join("; ", errors));
    }

    private static void DuplicateNames(
        IEnumerable<string> names, string kind, ICollection<string> errors)
    {
        foreach (var duplicate in names.Where(x => !string.IsNullOrWhiteSpace(x))
                     .GroupBy(x => x, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
            errors.Add($"duplicate {kind} '{duplicate}'");
    }

    private static void ValidateRegex(string? pattern, string label, ICollection<string> errors)
    {
        if (pattern is null) return;
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{label} is invalid: {ex.Message}");
        }
    }
}
