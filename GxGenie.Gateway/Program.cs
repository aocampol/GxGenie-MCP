using System.Text;
using GxGenie.Gateway;

// MCP communicates over stdio: stdout MUST contain only JSON-RPC framed messages,
// so all human-readable logging is routed to stderr.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

string? configPath = null;
int idx = Array.IndexOf(args, "--config");
if (idx >= 0 && idx + 1 < args.Length) configPath = args[idx + 1];

if (args.Any(a => a == "--help" || a == "-h"))
{
    Console.Error.WriteLine("GxGenie.Gateway — MCP server (JSON-RPC over stdio) for GeneXus KBs");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  GxGenie.Gateway.exe                     Run MCP loop on stdio");
    Console.Error.WriteLine("  GxGenie.Gateway.exe --config <path>     Override config.json location");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Register with Claude Code:");
    Console.Error.WriteLine("  claude mcp add --transport stdio genexus -- <path-to>\\GxGenie.Gateway.exe");
    return 0;
}

GatewayConfig config;
try
{
    config = GatewayConfig.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[gxmcp.gateway] Config load failed: {ex.Message}");
    return 2;
}

void Log(string msg) => Console.Error.WriteLine($"[gxmcp.gateway] {msg}");

Log($"Worker: {config.WorkerExecutablePath}");
Log($"Config: {(string.IsNullOrEmpty(config.ResolvedConfigPath) ? "(auto-detect from cwd)" : config.ResolvedConfigPath)}");
Log($"Cwd:    {Environment.CurrentDirectory}");

using var worker = new WorkerProxy(config);
var server = new McpServer(worker, Console.In, Console.Out, Log);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token).ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
