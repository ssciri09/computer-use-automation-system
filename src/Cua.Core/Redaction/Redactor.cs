using System.Text.RegularExpressions;

namespace Cua.Core.Redaction;

/// <summary>
/// Scrubs sensitive values before anything is persisted — log lines, model
/// transcripts, artifacts, result payloads. Redaction happens at the write
/// boundary, so nothing downstream has to remember to be careful.
/// </summary>
public sealed class Redactor
{
    public const string Mask = "▮▮REDACTED▮▮";

    private readonly List<Regex> _patterns = [];
    private readonly List<Regex> _documentPatterns = [];
    private readonly List<string> _literals = [];

    /// <summary>Built-in patterns for regulated-data hygiene, always on.</summary>
    public static Redactor CreateDefault()
    {
        var r = new Redactor();
        // API keys / bearer tokens
        r.AddPattern(@"sk-[A-Za-z0-9\-_]{16,}");
        r.AddPattern(@"(?i)(authorization\s*[:=]\s*)\S+");
        // password form parameters that might appear in URLs or transcripts
        r.AddPattern(@"(?i)((?:j_)?password['""]?\s*[:=]\s*)[^\s&'""]+");
        // SSN-shaped
        r.AddPattern(@"\b\d{3}-\d{2}-\d{4}\b");
        // Bystander protection for captured screen content: account-shaped
        // digit runs (other customers' identifiers on shared screens).
        // Document-only, so run ids and timestamps in log lines stay legible.
        r.AddDocumentPattern(@"\b\d{5,16}\b");
        return r;
    }

    /// <summary>Patterns applied only to captured screen content (ApplyToDocument), not to every log line.</summary>
    public void AddDocumentPattern(string regex) =>
        _documentPatterns.Add(new Regex(regex, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

    public void AddPattern(string regex) =>
        _patterns.Add(new Regex(regex, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

    /// <summary>Exact secret values (resolved credentials, sensitive input values) that must never appear in output.</summary>
    public void AddLiteral(string value)
    {
        if (!string.IsNullOrEmpty(value)) _literals.Add(value);
    }

    public string Apply(string text) => Apply(text, _patterns);

    /// <summary>
    /// For persisted captures of screen content (failure observation dumps,
    /// escalation digests): everything Apply masks, plus the document-only
    /// patterns protecting bystander data visible on shared screens.
    /// </summary>
    public string ApplyToDocument(string text) => Apply(Apply(text, _patterns), _documentPatterns);

    private string Apply(string text, List<Regex> patterns)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var lit in _literals)
            text = text.Replace(lit, Mask, StringComparison.Ordinal);
        foreach (var p in patterns)
        {
            try
            {
                text = p.Replace(text, m =>
                    m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value + Mask : Mask);
            }
            catch (RegexMatchTimeoutException) { /* leave text as-is rather than hang */ }
        }
        return text;
    }
}
