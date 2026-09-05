using System.Text.Json;
using Anthropic.Models.Messages;
using Cua.Core.Artifacts;

namespace Cua.Engine.Discovery;

/// <summary>Tool surface the discovery model drives the live surface through. Every call passes the policy gate before touching the surface. Schema is the same for every SurfaceKind; only descriptions change.</summary>
public static class AgentTools
{
    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);

    private static object FramePathProp(SurfaceKind kind) => new
    {
        type = "array",
        items = new { type = "string" },
        description = kind == SurfaceKind.Desktop
            ? "Window/pane name path from the main window, e.g. [] for main, [\"Fee Management\"] for a child pane."
            : "Frame name path from the top document, e.g. [] for top, [\"wrkFrm\",\"modFrm\"] for a nested module iframe.",
    };

    private static object ByProp(SurfaceKind kind) => new
    {
        type = "string",
        @enum = new[] { "css", "text", "xpath" },
        description = kind == SurfaceKind.Desktop
            ? "Locator strategy. Prefer css with an #id (mapped to AutomationId); use text for Name-property controls."
            : "Locator strategy. Prefer css with an #id; use text for controls without ids.",
    };

    private static readonly object ProbeProp = new
    {
        type = "boolean",
        description = "true = exploratory probe to OBSERVE an outcome state (e.g. trying a variant input to see its error text). Probe actions are EXCLUDED from the recorded flow and must not be referenced from declare_capability.",
    };

    public static List<ToolUnion> Build(SurfaceKind kind = SurfaceKind.Web)
    {
        var framePath = FramePathProp(kind);
        var by = ByProp(kind);
        return
    [
        Make("click",
            "Click an element. Set risk=irreversible for anything that posts, commits, waives, reverses or otherwise cannot be undone.",
            new Dictionary<string, object>
            {
                ["frame_path"] = framePath,
                ["by"] = by,
                ["value"] = new { type = "string", description = "Selector or visible text." },
                ["within"] = new { type = "string", description = "Optional CSS scope for text locators, e.g. \"span.x-btn-text\"." },
                ["risk"] = new { type = "string", @enum = new[] { "safe", "write", "irreversible" } },
                ["probe"] = ProbeProp,
                ["note"] = new { type = "string", description = "Short human-readable purpose of this click." },
            }, ["frame_path", "by", "value"]),

        Make("type",
            "Type into a field (clears it first unless clear_first=false). For credential fields use the placeholders {{credential:username}} / {{credential:password}} — the runtime substitutes real values you never see.",
            new Dictionary<string, object>
            {
                ["frame_path"] = framePath,
                ["by"] = by,
                ["value"] = new { type = "string" },
                ["text"] = new { type = "string", description = "Text to type (or a credential placeholder)." },
                ["clear_first"] = new { type = "boolean" },
                ["probe"] = ProbeProp,
                ["note"] = new { type = "string" },
            }, ["frame_path", "by", "value", "text"]),

        Make("select",
            "Choose an option in a select element by option value (falls back to label).",
            new Dictionary<string, object>
            {
                ["frame_path"] = framePath,
                ["by"] = by,
                ["value"] = new { type = "string" },
                ["option"] = new { type = "string" },
                ["probe"] = ProbeProp,
            }, ["frame_path", "by", "value", "option"]),

        Make("read",
            "Read the text of an element into a named output (use for values the capability should return).",
            new Dictionary<string, object>
            {
                ["frame_path"] = framePath,
                ["by"] = by,
                ["value"] = new { type = "string" },
                ["output_name"] = new { type = "string" },
                ["probe"] = ProbeProp,
            }, ["frame_path", "by", "value", "output_name"]),

        Make("wait_for",
            "Wait for an element state. Use state=absent on the busy indicator (e.g. #loading-spinner) after any action that triggers a slow host call, BEFORE trusting the observation.",
            new Dictionary<string, object>
            {
                ["frame_path"] = framePath,
                ["by"] = by,
                ["value"] = new { type = "string" },
                ["state"] = new { type = "string", @enum = new[] { "visible", "absent" } },
                ["timeout_ms"] = new { type = "integer" },
            }, ["frame_path", "by", "value", "state"]),

        Make("navigate", kind == SurfaceKind.Desktop
            ? "Launch or attach the desktop application (must be inside the allowlist). Use an exe path, file:// URI, or app://WindowTitle."
            : "Navigate the top document to a URL (must be inside the allowlist).",
            new Dictionary<string, object> { ["url"] = new { type = "string" } }, ["url"]),

        Make("observe", "Take a fresh observation of every frame without acting.",
            new Dictionary<string, object>(), []),

        Make("escalate_to_human",
            "You are stuck (unexpected modal you cannot clear, permission denied, repeated failures). Hands the live session to a human operator.",
            new Dictionary<string, object> { ["reason"] = new { type = "string" } }, ["reason"]),

        Make("give_up", "Abort discovery: the goal cannot be met.",
            new Dictionary<string, object> { ["reason"] = new { type = "string" } }, ["reason"]),

        Make("declare_capability",
            "TERMINAL. Call once, when the goal is met and the final confirmed state is on screen. Declares the reusable capability contract that will be compiled into the replay artifact. Be exhaustive about outcome classification: business outcomes are legitimate app answers (record not found, account closed) the caller must receive as results; recoverable conditions are transient (host busy banners) and carry a retry policy; escalation conditions need a human (security/override modals — note these may appear in a DIFFERENT frame, often the top document).",
            new Dictionary<string, object>
            {
                ["display_name"] = new { type = "string" },
                ["description"] = new { type = "string" },
                ["auth_steps"] = new
                {
                    type = "array", items = new { type = "string" },
                    description = "Step ids that authenticate the session (sign-on fields/submit and any mandatory post-login acknowledgement). Used to auto-recover from session expiry.",
                },
                ["outputs"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["name"] = new { type = "string" },
                            ["type"] = new { type = "string", @enum = new[] { "string", "enum" } },
                            ["enum_values"] = new { type = "array", items = new { type = "string" } },
                        },
                        required = new[] { "name", "type" },
                    },
                },
                ["checkpoint"] = new
                {
                    type = "object",
                    description = "The condition proving the goal state was reached, plus regex extractions for output values (regexes run over the frame's full text; use a capture group for the value).",
                    properties = new Dictionary<string, object>
                    {
                        ["frame_path"] = framePath,
                        ["by"] = new { type = "string", @enum = new[] { "css", "text_contains", "text_equals" } },
                        ["value"] = new { type = "string" },
                        ["emit"] = new { type = "object", description = "Output values to set on success, e.g. {\"outcome\":\"waived\"}." },
                        ["extracts"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["name"] = new { type = "string" },
                                    ["regex"] = new { type = "string" },
                                },
                                required = new[] { "name", "regex" },
                            },
                        },
                    },
                    required = new[] { "frame_path", "by", "value" },
                },
                ["guarded_steps"] = new
                {
                    type = "array",
                    description = "Outcome classification for steps whose result varies at runtime (typically each step that triggers a host call).",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["after_step"] = new { type = "string", description = "The recorded step id this classification guards." },
                            ["success_when"] = Cond(kind, "Condition proving the step succeeded and the flow may proceed."),
                            ["business_outcomes"] = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["when"] = Cond(kind, null),
                                        ["emit"] = new { type = "object", description = "e.g. {\"outcome\":\"not_found\"}" },
                                        ["note"] = new { type = "string" },
                                    },
                                    required = new[] { "when", "emit" },
                                },
                            },
                            ["recoverable"] = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["when"] = Cond(kind, null),
                                        ["max_attempts"] = new { type = "integer" },
                                        ["backoff_ms"] = new { type = "array", items = new { type = "integer" } },
                                        ["note"] = new { type = "string" },
                                    },
                                    required = new[] { "when" },
                                },
                            },
                            ["escalate"] = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["when"] = Cond(kind, "Remember: security modals may be injected into the TOP frame — set frame_path accordingly."),
                                        ["reason"] = new { type = "string" },
                                        ["resume_at"] = new { type = "string", description = "Step id to resume at after the human resolves it (\"checkpoint\" for the final verification step)." },
                                        ["note"] = new { type = "string" },
                                    },
                                    required = new[] { "when", "reason" },
                                },
                            },
                        },
                        required = new[] { "after_step" },
                    },
                },
                ["robustness_notes"] = new { type = "string", description = "Your reasoning about locator stability and what might drift across vendor releases/tenants." },
            },
            ["display_name", "outputs", "checkpoint", "guarded_steps"]),
    ];
    }

    private static object Cond(SurfaceKind kind, string? description) => new
    {
        type = "object",
        description = description ?? "A detectable condition.",
        properties = new Dictionary<string, object>
        {
            ["frame_path"] = FramePathProp(kind),
            ["by"] = new { type = "string", @enum = new[] { "css", "text_contains", "text_equals" } },
            ["value"] = new { type = "string" },
        },
        required = new[] { "by", "value" },
    };

    private static ToolUnion Make(string name, string description,
        Dictionary<string, object> props, string[] required) =>
        new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new()
            {
                Properties = props.ToDictionary(kv => kv.Key, kv => El(kv.Value)),
                Required = required,
            },
        };
}
