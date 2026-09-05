using System.Text.Json;
using System.Text.Json.Nodes;
using Cua.Core.Artifacts;
using Cua.Core.Contracts;
using Cua.Core.Evidence;
using Cua.Core.Hitl;
using Cua.Core.Redaction;
using Cua.Engine.Replay;
using Cua.Hosting;

namespace Cua.Mcp;

/// <summary>
/// Exposes the capability catalog to an AI agent over MCP (JSON-RPC 2.0 on
/// stdio). Recorded artifacts become callable tools: the agent discovers them
/// by name with typed arguments, invokes one, and gets the replay result
/// contract back — success, a business outcome, a pending escalation, or a
/// hard failure.
///
/// Two deliberate constraints make this safe to hand an agent:
///   - only *approved* artifacts are listed and callable; drafts are
///     discoverable as resources for review but cannot be invoked;
///   - invocation is unattended, so an escalation parks the run and returns
///     escalation_pending rather than blocking on a human at a terminal.
///
/// The protocol layer is hand-rolled: it is ~200 lines of well-understood
/// JSON-RPC, which is a smaller commitment than a framework for a surface
/// this size.
/// </summary>
public sealed class CapabilityServer(string capabilitiesDir, string evidenceRoot)
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly ArtifactStore _store = new(capabilitiesDir);

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct);
            if (line is null) return;                    // stdin closed: client went away
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(output, Error(null, -32700, "parse error"));
                continue;
            }
            if (request is null) continue;

            var id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? "";

            // Notifications carry no id and take no response.
            if (id is null)
            {
                Log($"notification {method}");
                continue;
            }

            JsonNode response;
            try
            {
                response = await DispatchAsync(method, request["params"], id, ct);
            }
            catch (Exception ex)
            {
                Log($"handler error on {method}: {ex.Message}");
                response = Error(id, -32603, $"internal error: {ex.Message}");
            }
            await WriteAsync(output, response);
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonNode? args, JsonNode id, CancellationToken ct) =>
        method switch
        {
            "initialize" => Initialize(id),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => ToolsList(id),
            "tools/call" => await ToolsCallAsync(id, args, ct),
            "resources/list" => ResourcesList(id),
            "resources/read" => ResourcesRead(id, args),
            _ => Error(id, -32601, $"method not found: {method}"),
        };

    // ------------------------------------------------------------- handshake

    private static JsonNode Initialize(JsonNode id) => Result(id, new JsonObject
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
            ["resources"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "cua-capabilities",
            ["version"] = "1.0.0",
        },
        ["instructions"] =
            "Each tool is a recorded, human-approved automation of a legacy banking workflow, " +
            "replayed deterministically with no model in the loop. Results distinguish success " +
            "from expected business outcomes (e.g. account closed) — treat a business outcome as " +
            "an answer, not an error. A pending escalation means a human operator was asked to " +
            "intervene; the run is parked, not failed.",
    });

    // ----------------------------------------------------------------- tools

    private JsonNode ToolsList(JsonNode id)
    {
        var tools = new JsonArray();
        foreach (var (path, artifact) in Approved())
            tools.Add(ToolDefinition(path, artifact));
        Log($"tools/list -> {tools.Count} approved capabilities");
        return Result(id, new JsonObject { ["tools"] = tools });
    }

    private static JsonObject ToolDefinition(string path, CapabilityArtifact artifact)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var input in artifact.Inputs)
        {
            var schema = new JsonObject { ["type"] = input.Type == "string" ? "string" : input.Type };
            var description = $"{input.Name}";
            if (input.Sensitivity != Sensitivity.None)
                description += $" (sensitivity: {input.Sensitivity.ToString().ToLowerInvariant()}; redacted in logs)";
            schema["description"] = description;
            if (input.Pattern is not null) schema["pattern"] = input.Pattern;
            properties[input.Name] = schema;
            if (input.Required) required.Add(input.Name);
        }

        var outputs = artifact.Outputs.Select(o =>
            o.EnumValues is { Count: > 0 } v ? $"{o.Name} ({string.Join(" | ", v)})" : o.Name);

        return new JsonObject
        {
            ["name"] = ToolName(artifact),
            ["description"] =
                $"{artifact.DisplayName}. Surface: {artifact.Surface.Kind.ToString().ToLowerInvariant()}" +
                $"{(artifact.Surface.AppBinding is { } b ? $" ({b})" : "")}. " +
                $"Returns: {string.Join(", ", outputs)}. " +
                $"Recorded from: \"{artifact.Goal}\". Artifact v{artifact.CapabilityVersion}, approved.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            },
            ["_meta"] = new JsonObject
            {
                ["artifact"] = Path.GetFileName(path),
                ["capability_id"] = artifact.CapabilityId,
                ["surface_kind"] = artifact.Surface.Kind.ToString().ToLowerInvariant(),
            },
        };
    }

    private async Task<JsonNode> ToolsCallAsync(JsonNode id, JsonNode? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>();
        if (name is null) return Error(id, -32602, "missing tool name");

        var match = Approved().FirstOrDefault(a => ToolName(a.Artifact) == name);
        if (match.Artifact is null)
        {
            // Distinguish "unknown" from "known but not approved" — an agent
            // acting on the difference is the point of the approval gate.
            var draft = _store.List().FirstOrDefault(a => ToolName(a.Artifact) == name);
            return draft.Artifact is not null
                ? ToolError(id, $"capability '{name}' exists but is not approved " +
                                $"(state: {draft.Artifact.Provenance.Approval}); a human must review it first")
                : ToolError(id, $"unknown capability '{name}'");
        }

        var inputs = new Dictionary<string, string>();
        if (args?["arguments"] is JsonObject provided)
            foreach (var (key, value) in provided)
                if (value is not null)
                    inputs[key] = value.GetValueKind() == JsonValueKind.String
                        ? value.GetValue<string>()
                        : value.ToJsonString();

        Log($"tools/call {name} inputs=[{string.Join(", ", inputs.Keys)}]");
        var result = await InvokeAsync(match.Artifact, inputs, ct);

        // The structured result IS the answer; also give a one-line summary so
        // a model reading text content gets the classification immediately.
        var summary = result.Status switch
        {
            RunStatus.Success => $"SUCCESS{(result.HumanAssisted ? " (human-assisted)" : "")}: " +
                                 string.Join(", ", result.Outputs.Select(o => $"{o.Key}={o.Value}")),
            RunStatus.BusinessOutcome => $"BUSINESS OUTCOME: {result.Outcome} — this is a legitimate answer, not a failure",
            RunStatus.EscalationPending => $"ESCALATION PENDING at step {result.Intervention?.AtStepId}: " +
                                           $"{result.Intervention?.Reason}. A human operator has been asked to take over; the run is parked.",
            _ => $"HARD FAILURE at step {result.Failure?.StepId}: expected {result.Failure?.Expected}; observed {result.Failure?.Observed}",
        };

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = summary },
                new JsonObject { ["type"] = "text", ["text"] = CuaJson.Serialize(result) },
            },
            ["isError"] = result.Status == RunStatus.HardFailure,
        });
    }

    private async Task<ReplayResult> InvokeAsync(
        CapabilityArtifact artifact, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        var redactor = Redactor.CreateDefault();
        foreach (var pattern in artifact.Redaction.LogPatterns) redactor.AddPattern(pattern);
        foreach (var def in artifact.Inputs.Where(d => d.RedactInLogs))
            if (inputs.TryGetValue(def.Name, out var v))
                redactor.AddPattern(System.Text.RegularExpressions.Regex.Escape(v));

        using var log = new RunLogger(evidenceRoot, "mcp", redactor) { Quiet = true };
        // Unattended: an escalation parks the run for an operator instead of
        // blocking on a terminal the calling agent cannot see.
        var channel = new QueueOperatorChannel(Path.Combine(evidenceRoot, "operator-queue"));
        await using var surface = await SurfaceFactory.LaunchAsync(artifact.Surface.Kind, headed: false);
        var engine = new ReplayEngine(surface, channel, log, redactor);

        var result = await engine.RunAsync(artifact, inputs, new ReplayOptions
        {
            // Only approved artifacts reach here, so irreversible steps may run
            // unattended — that is precisely what approval authorizes.
            AckRisk = true,
        }, ct);
        log.SaveText("result.json", CuaJson.Serialize(result));
        return result;
    }

    // ------------------------------------------------------------- resources

    /// <summary>Every artifact — including drafts — is readable for review. Reviewability is the point of the schema.</summary>
    private JsonNode ResourcesList(JsonNode id)
    {
        var resources = new JsonArray();
        foreach (var (path, artifact) in _store.List())
            resources.Add(new JsonObject
            {
                ["uri"] = $"cua://artifact/{Path.GetFileName(path)}",
                ["name"] = $"{artifact.CapabilityId} v{artifact.CapabilityVersion} ({artifact.Provenance.Approval})",
                ["description"] = $"{artifact.DisplayName} — {artifact.Steps.Count} steps, surface {artifact.Surface.Kind.ToString().ToLowerInvariant()}",
                ["mimeType"] = "application/json",
            });
        return Result(id, new JsonObject { ["resources"] = resources });
    }

    private JsonNode ResourcesRead(JsonNode id, JsonNode? args)
    {
        var uri = args?["uri"]?.GetValue<string>() ?? "";
        const string prefix = "cua://artifact/";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal))
            return Error(id, -32602, $"unsupported resource uri: {uri}");

        var fileName = Path.GetFileName(uri[prefix.Length..]);   // never trust the path
        var path = Path.Combine(_store.Directory, fileName);
        if (!File.Exists(path)) return Error(id, -32602, $"no such artifact: {fileName}");

        return Result(id, new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = "application/json",
                    ["text"] = File.ReadAllText(path),
                },
            },
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The callable catalog: the highest approved version of each capability
    /// binding. Selection is deterministic — an agent calling a tool by name
    /// today and tomorrow gets the same artifact until a newer one is approved.
    /// </summary>
    private IReadOnlyList<(string Path, CapabilityArtifact Artifact)> Approved() =>
    [
        .. _store.List()
            .Where(a => a.Artifact.Provenance.Approval == "approved")
            .GroupBy(a => ToolName(a.Artifact))
            .Select(g => g.OrderByDescending(a => a.Artifact.CapabilityVersion).First())
            .OrderBy(a => ToolName(a.Artifact), StringComparer.Ordinal),
    ];

    /// <summary>Catalog identity an agent calls by: capability id plus binding, normalized to MCP's tool-name charset.</summary>
    private static string ToolName(CapabilityArtifact artifact)
    {
        var raw = artifact.Surface.AppBinding is { Length: > 0 } binding
            ? $"{artifact.CapabilityId}__{binding}"
            : artifact.CapabilityId;
        return new string([.. raw.Select(c => char.IsLetterOrDigit(c) || c is '_' ? c : '_')]);
    }

    private static JsonNode Result(JsonNode id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonNode Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id ?? JsonValue.Create((string?)null),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>A failed invocation is a tool result, not a protocol error — the agent must see and reason about it.</summary>
    private static JsonNode ToolError(JsonNode id, string message) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true,
    });

    private static async Task WriteAsync(TextWriter output, JsonNode message)
    {
        await output.WriteLineAsync(message.ToJsonString());
        await output.FlushAsync();
    }

    /// <summary>stdout is the protocol channel — diagnostics must go to stderr.</summary>
    private static void Log(string message) => Console.Error.WriteLine($"[cua-mcp] {message}");
}
