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
    private readonly List<Regex> _literalPatterns = [];

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

    /// <summary>
    /// Adds a sensitive regex. The complete match is removed, including any
    /// capture groups; artifact extraction regexes commonly put the sensitive
    /// value in group 1, so preserving that group would leak the value.
    /// </summary>
    public void AddPattern(string regex) =>
        _patterns.Add(new Regex(regex, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

    /// <summary>Exact secret values (resolved credentials, sensitive input values) that must never appear in output.</summary>
    public void AddLiteral(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        // Treat the value as a complete token. This still masks query/form
        // values and prose, but a common username such as "operator" cannot
        // corrupt a structured field name such as "operator_notes".
        var escaped = Regex.Escape(value);
        _literalPatterns.Add(new Regex(
            $@"(?<![A-Za-z0-9_]){escaped}(?![A-Za-z0-9_])",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1)));
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
        foreach (var literal in _literalPatterns)
            text = literal.Replace(text, Mask);
        foreach (var p in patterns)
        {
            try
            {
                text = p.Replace(text, Mask);
            }
            catch (RegexMatchTimeoutException)
            {
                // Evidence safety fails closed: an expensive redaction pattern
                // must never cause the original regulated text to be persisted.
                return Mask;
            }
        }
        return text;
    }
}
