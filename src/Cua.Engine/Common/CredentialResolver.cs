using Cua.Core.Artifacts;

namespace Cua.Engine.Common;

/// <summary>
/// Resolves a credentials reference at execution time. Artifacts carry only the
/// reference (e.g. "env://CUA"); values come from the runtime environment here,
/// and would come from a vault in production. Resolved values are registered
/// with the redactor by the caller so they can never reach logs or transcripts.
/// </summary>
public static class CredentialResolver
{
    public static string Resolve(CredentialsRef? credentials, string field)
    {
        var reference = credentials?.Ref
            ?? throw new InvalidOperationException(
                $"step references credentials.{field} but the artifact declares no credentials ref");

        if (!reference.StartsWith("env://", StringComparison.Ordinal))
            throw new InvalidOperationException($"unsupported credentials ref scheme: {reference}");

        var prefix = reference["env://".Length..];
        var envVar = $"{prefix}_{field.ToUpperInvariant()}";
        return Environment.GetEnvironmentVariable(envVar)
            ?? throw new InvalidOperationException(
                $"credential env var {envVar} is not set (needed for credentials.{field})");
    }
}
