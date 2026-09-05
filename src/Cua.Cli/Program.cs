using Cua.Core.Artifacts;
using Cua.Core.Contracts;
using Cua.Core.Evidence;
using Cua.Core.Hitl;
using Cua.Core.Policy;
using Cua.Core.Redaction;
using Cua.Core.Surface;
using Cua.Engine.Discovery;
using Cua.Engine.Replay;

LoadDotEnv();

var args0 = args.Length > 0 ? args[0] : "help";
try
{
    return args0 switch
    {
        "discover" => await DiscoverAsync(Opts.Parse(args[1..])),
        "replay" => await ReplayAsync(Opts.Parse(args[1..])),
        "approve" => Approve(Opts.Parse(args[1..])),
        "list" => ListCapabilities(Opts.Parse(args[1..])),
        "install-browsers" => InstallBrowsers(),
        _ => Help(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

// --------------------------------------------------------------------- verbs

static async Task<int> DiscoverAsync(Opts o)
{
    var goal = o.Require("goal");
    var capabilityId = o.Require("id");
    var parameters = o.Multi("param");
    var evidenceRoot = o.Get("evidence") ?? "evidence";
    var url = o.Get("url") ?? "http://127.0.0.1:8080/";
    var kind = SurfaceKinds.Parse(o.Get("kind"), SurfaceKinds.InferFromEntry(url));
    var allowHosts = ResolveAllowlist(o, url, kind);

    var redactor = Redactor.CreateDefault();

    using var log = new RunLogger(evidenceRoot, "discovery", redactor);
    Console.WriteLine($"discovery run {log.RunId}");
    Console.WriteLine($"  goal: {goal}");
    Console.WriteLine($"  surface: {kind}");

    var policy = new PolicyGate(new PolicyConfig
    {
        AllowedHosts = allowHosts,
        SurfaceKind = kind,
        RiskyMode = ParseRiskyMode(o.Get("risky") ?? "flag"),
        MaxSteps = int.Parse(o.Get("max-steps") ?? "40"),
    });

    await using var surface = await SurfaceFactory.LaunchAsync(kind, o.Has("headed"));
    var agent = new DiscoveryAgent(surface, policy, log, redactor, new ConsoleOperatorChannel());
    var store = new ArtifactStore(o.Get("out") ?? "capabilities");

    var result = await agent.RunAsync(new DiscoveryConfig
    {
        Goal = goal,
        EntryUrl = url,
        CapabilityId = capabilityId,
        Parameters = parameters,
        CredentialsRef = o.Get("cred-ref") ?? "env://CUA",
        Model = o.Get("model") ?? Environment.GetEnvironmentVariable("CUA_MODEL") ?? "claude-opus-5",
        VendorProduct = o.Get("vendor"),
        AppBinding = o.Get("binding"),
        SurfaceKind = kind,
    }, store, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine(result.Succeeded
        ? $"OK  capability recorded: {result.ArtifactPath}"
        : $"FAIL  discovery failed: {result.FailureReason}");
    Console.WriteLine($"  steps: {result.Steps}   tokens: {result.InputTokens} in / {result.OutputTokens} out");
    Console.WriteLine($"  evidence: {result.EvidenceDir}");
    if (result.Succeeded)
        Console.WriteLine($"  next: review the artifact, then 'cua approve --artifact {result.ArtifactPath}'");
    return result.Succeeded ? 0 : 1;
}

static async Task<int> ReplayAsync(Opts o)
{
    var artifactPath = o.Require("artifact");
    var store = new ArtifactStore(Path.GetDirectoryName(Path.GetFullPath(artifactPath)) ?? "capabilities");
    var artifact = store.Load(artifactPath);
    var inputs = o.Multi("input");
    var evidenceRoot = o.Get("evidence") ?? "evidence";

    var redactor = Redactor.CreateDefault();
    foreach (var pattern in artifact.Redaction.LogPatterns) redactor.AddPattern(pattern);
    foreach (var def in artifact.Inputs.Where(d => d.RedactInLogs))
        if (inputs.TryGetValue(def.Name, out var v))
            redactor.AddPattern(System.Text.RegularExpressions.Regex.Escape(v));

    using var log = new RunLogger(evidenceRoot, "replay", redactor);
    Console.WriteLine($"replay run {log.RunId}  ({artifact.CapabilityId} v{artifact.CapabilityVersion} / {artifact.Surface.Kind})");

    IOperatorChannel channel = (o.Get("operator") ?? "console") switch
    {
        "queue" => new QueueOperatorChannel(Path.Combine(evidenceRoot, "operator-queue")),
        _ => new ConsoleOperatorChannel(),
    };

    await using var surface = await SurfaceFactory.LaunchAsync(artifact.Surface.Kind, o.Has("headed"));
    var engine = new ReplayEngine(surface, channel, log, redactor);
    var result = await engine.RunAsync(artifact, inputs, new ReplayOptions
    {
        AckRisk = o.Has("ack-risk"),
        AllowDraft = o.Has("allow-draft"),
    }, CancellationToken.None);

    var resultJson = CuaJson.Serialize(result);
    log.SaveText("result.json", resultJson);
    Console.WriteLine();
    Console.WriteLine(resultJson);
    Console.WriteLine();
    Console.WriteLine(result.Status switch
    {
        RunStatus.Success => $"OK  success{(result.HumanAssisted ? " (human-assisted)" : "")}",
        RunStatus.BusinessOutcome => $"OUTCOME  business outcome: {result.Outcome}",
        RunStatus.EscalationPending => "PENDING  escalation pending — session context preserved for the operator",
        _ => $"FAIL  hard failure at step {result.Failure?.StepId}: {result.Failure?.Observed}",
    });
    return result.Status is RunStatus.Success or RunStatus.BusinessOutcome ? 0 : 1;
}

static int Approve(Opts o)
{
    var path = o.Require("artifact");
    var store = new ArtifactStore(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "capabilities");
    var artifact = store.Load(path);
    if (artifact.Provenance.Approval == "approved")
    {
        Console.WriteLine("already approved");
        return 0;
    }
    var updated = artifact with { Provenance = artifact.Provenance with { Approval = "approved" } };
    File.WriteAllText(path, CuaJson.Serialize(updated));
    Console.WriteLine($"approved: {artifact.CapabilityId} v{artifact.CapabilityVersion}");
    Console.WriteLine("unattended replay of irreversible steps is now permitted with --ack-risk");
    return 0;
}

static int ListCapabilities(Opts o)
{
    var store = new ArtifactStore(o.Get("dir") ?? "capabilities");
    var all = store.List();
    if (all.Count == 0)
    {
        Console.WriteLine("no capabilities recorded yet");
        return 0;
    }
    foreach (var (path, a) in all.OrderBy(x => x.Artifact.CapabilityId).ThenBy(x => x.Artifact.CapabilityVersion))
    {
        Console.WriteLine($"{a.CapabilityId} v{a.CapabilityVersion} [{a.Provenance.Approval}] {a.Surface.Kind} — {a.DisplayName}");
        Console.WriteLine($"    inputs:  {string.Join(", ", a.Inputs.Select(i => $"{i.Name}:{i.Type}"))}");
        Console.WriteLine($"    outputs: {string.Join(", ", a.Outputs.Select(x => x.Name + (x.EnumValues is null ? "" : $"({string.Join("|", x.EnumValues)})")))}");
        Console.WriteLine($"    steps: {a.Steps.Count}   file: {path}");
    }
    return 0;
}

static int InstallBrowsers()
{
    Console.WriteLine("installing Playwright Chromium…");
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);
}

static int Help()
{
    Console.WriteLine("""
        cua — computer-use automation: LLM discovery → capability artifact → deterministic replay

        verbs:
          discover  --goal "…" --id <capability_id> [--url <entry>] [--kind web|legacy_web|desktop] [--binding <app>]
                    [--param name=value]… [--headed] [--model claude-opus-5] [--risky flag|confirm|block]
                    [--allow-host host:port]… [--allow-target name]… [--cred-ref env://CUA] [--out capabilities]
          replay    --artifact <path> [--input name=value]… [--headed]
                    [--operator console|queue] [--ack-risk] [--allow-draft]
          approve   --artifact <path>            mark a reviewed artifact approved
          list      [--dir capabilities]          show the capability catalog
          install-browsers                        one-time Playwright Chromium install

        --kind selects the ISurface adapter (web/legacy_web = Playwright, desktop = UI Automation).
        Omit it to infer: http(s) → web, .exe / file:// / app:// → desktop. Use legacy_web for framesets.
        Replay always uses the kind stamped on the artifact.

        env: ANTHROPIC_API_KEY (discovery), CUA_USERNAME / CUA_PASSWORD (target app sign-on)
        """);
    return 0;
}

/// <summary>Loads .env from the working directory (and its parents, nearest wins) without overriding real environment variables. Values never get logged.</summary>
static void LoadDotEnv()
{
    var dir = Directory.GetCurrentDirectory();
    for (var depth = 0; dir is not null && depth < 4; depth++, dir = Path.GetDirectoryName(dir))
    {
        var file = Path.Combine(dir, ".env");
        if (!File.Exists(file)) continue;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
        return; // nearest .env wins
    }
}

static IReadOnlyList<string> ResolveAllowlist(Opts o, string entryUrl, SurfaceKind kind)
{
    var hosts = o.MultiValues("allow-host").Concat(o.MultiValues("allow-target")).ToList();
    if (kind == SurfaceKind.Desktop)
        hosts.Add(SurfaceTarget.IdentityOf(entryUrl));
    else if (Uri.TryCreate(entryUrl, UriKind.Absolute, out var uri))
        hosts.Add(uri.Authority);
    return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static RiskyActionMode ParseRiskyMode(string s) => s switch
{
    "block" => RiskyActionMode.Block,
    "confirm" => RiskyActionMode.Confirm,
    _ => RiskyActionMode.Flag,
};

/// <summary>Tiny flag parser: --key value, --key (bool), repeated --param name=value.</summary>
internal sealed class Opts
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Opts Parse(string[] args)
    {
        var o = new Opts();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument '{args[i]}'");
            var key = args[i][2..];
            string value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++i];
            if (!o._values.TryGetValue(key, out var list)) o._values[key] = list = [];
            list.Add(value);
        }
        return o;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v[^1] : null;
    public string Require(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");
    public bool Has(string key) => _values.ContainsKey(key);
    public IEnumerable<string> MultiValues(string key) => _values.TryGetValue(key, out var v) ? v : [];

    public Dictionary<string, string> Multi(string key)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in MultiValues(key))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) throw new ArgumentException($"--{key} expects name=value, got '{pair}'");
            result[pair[..idx]] = pair[(idx + 1)..];
        }
        return result;
    }
}
