using Cua.Hosting;
using Cua.Mcp;

// MCP stdio server: newline-delimited JSON-RPC on stdin/stdout.
// stdout carries the protocol, so every diagnostic goes to stderr.
DotEnv.Load();

var capabilities = ArgValue("--capabilities") ?? "capabilities";
var evidence = ArgValue("--evidence") ?? "evidence";

Console.Error.WriteLine($"[cua-mcp] serving {Path.GetFullPath(capabilities)}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new CapabilityServer(capabilities, evidence);
await server.RunAsync(Console.In, Console.Out, cts.Token);
return 0;

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
