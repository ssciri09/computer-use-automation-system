using Cua.Core.Artifacts;
using Cua.Core.Redaction;

namespace Cua.Core.Evidence;

/// <summary>
/// Structured evidence for a run: a JSONL event log (redacted at the write
/// boundary), plus screenshots and auxiliary files in the run's evidence dir.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private readonly StreamWriter _log;
    private readonly Redactor _redactor;
    private readonly object _lock = new();
    private int _shotIndex;

    public string RunId { get; }
    public string Dir { get; }
    public bool Quiet { get; set; }

    public RunLogger(string evidenceRoot, string runKind, Redactor redactor, string? runId = null)
    {
        RunId = runId ?? $"{runKind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        Dir = Path.Combine(evidenceRoot, RunId);
        Directory.CreateDirectory(Dir);
        _redactor = redactor;
        _log = new StreamWriter(Path.Combine(Dir, "log.jsonl"), append: false) { AutoFlush = true };
    }

    public void Log(string kind, object payload, bool echo = true)
    {
        var line = CuaJson.SerializeCompact(new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["run_id"] = RunId,
            ["kind"] = kind,
            ["data"] = payload,
        });
        line = _redactor.Apply(line);
        lock (_lock) _log.WriteLine(line);
        if (echo && !Quiet)
        {
            var summary = line.Length > 220 ? line[..220] + "…" : line;
            Console.WriteLine($"  [{kind}] {Trim(summary)}");
        }
    }

    public string SaveScreenshot(byte[] png, string label)
    {
        var name = $"{Interlocked.Increment(ref _shotIndex):D2}-{Sanitize(label)}.png";
        var path = Path.Combine(Dir, name);
        File.WriteAllBytes(path, png);
        Log("screenshot", new { label, path = name }, echo: false);
        return path;
    }

    public string SaveText(string fileName, string content)
    {
        // Saved documents are captured screen content or result copies —
        // they get the stricter document tier (bystander digit masking).
        var path = Path.Combine(Dir, fileName);
        File.WriteAllText(path, _redactor.ApplyToDocument(content));
        return path;
    }

    /// <summary>Append one redacted JSONL record to a named auxiliary stream (e.g. transcript, human actions).</summary>
    public void AppendJsonl(string fileName, object payload)
    {
        var line = _redactor.Apply(CuaJson.SerializeCompact(payload));
        lock (_lock) File.AppendAllText(Path.Combine(Dir, fileName), line + Environment.NewLine);
    }

    private static string Sanitize(string s) =>
        new([.. s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

    private static string Trim(string s)
    {
        // keep console output single-line and narrow
        return s.Replace("\\n", " ").Replace("\n", " ");
    }

    public void Dispose() => _log.Dispose();
}
