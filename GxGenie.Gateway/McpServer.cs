using System.Text.Json;
using System.Text.Json.Nodes;

namespace GxGenie.Gateway;

/// <summary>
/// Hand-rolled JSON-RPC 2.0 server speaking the Model Context Protocol over stdio.
/// Implements only the methods Claude Code actually invokes: <c>initialize</c>,
/// <c>tools/list</c>, <c>tools/call</c>, plus notifications (no response).
/// Spec reference: https://modelcontextprotocol.io (protocol version 2024-11-05).
/// </summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "gxmcp";
    private const string ServerVersion = "0.3.0";

    private readonly WorkerProxy _worker;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Action<string> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public McpServer(WorkerProxy worker, TextReader input, TextWriter output, Action<string>? log = null)
    {
        _worker = worker;
        _input = input;
        _output = output;
        _log = log ?? (_ => { });
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log($"MCP server ready (protocol {ProtocolVersion}, {ToolSchemas.All.Count} tools)");
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (Exception ex)
            {
                await SendErrorAsync(null, -32700, $"Parse error: {ex.Message}").ConfigureAwait(false);
                continue;
            }
            if (request is not JsonObject reqObj)
            {
                await SendErrorAsync(null, -32600, "Invalid Request: not a JSON object").ConfigureAwait(false);
                continue;
            }

            await HandleAsync(reqObj, ct).ConfigureAwait(false);
        }
        _log("MCP server exiting (stdin closed)");
    }

    private async Task HandleAsync(JsonObject req, CancellationToken ct)
    {
        var idNode = req["id"];
        var method = req["method"]?.GetValue<string>();
        var paramsNode = req["params"];
        bool isNotification = idNode is null;

        if (string.IsNullOrEmpty(method))
        {
            if (!isNotification) await SendErrorAsync(idNode, -32600, "Missing 'method'").ConfigureAwait(false);
            return;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    await SendResultAsync(idNode, BuildInitializeResult()).ConfigureAwait(false);
                    break;

                case "initialized":
                case "notifications/initialized":
                    // No-op notification.
                    break;

                case "tools/list":
                    await SendResultAsync(idNode, BuildToolsList()).ConfigureAwait(false);
                    break;

                case "tools/call":
                    var callResult = await HandleToolCallAsync(paramsNode, ct).ConfigureAwait(false);
                    await SendResultAsync(idNode, callResult).ConfigureAwait(false);
                    break;

                case "ping":
                    await SendResultAsync(idNode, new JsonObject()).ConfigureAwait(false);
                    break;

                case "shutdown":
                    await SendResultAsync(idNode, new JsonObject()).ConfigureAwait(false);
                    break;

                default:
                    if (!isNotification)
                        await SendErrorAsync(idNode, -32601, $"Method not found: {method}").ConfigureAwait(false);
                    else
                        _log($"Ignoring unknown notification: {method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"Handler error in {method}: {ex.GetType().Name}: {ex.Message}");
            if (!isNotification)
                await SendErrorAsync(idNode, -32603, $"Internal error: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = ServerVersion,
        },
    };

    private static JsonObject BuildToolsList()
    {
        var tools = new JsonArray();
        foreach (var t in ToolSchemas.All)
        {
            tools.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> HandleToolCallAsync(JsonNode? paramsNode, CancellationToken ct)
    {
        if (paramsNode is not JsonObject p)
            return ToolError("tools/call: missing params");

        var name = p["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return ToolError("tools/call: missing 'name'");

        var tool = ToolSchemas.FindByName(name!);
        if (tool is null)
            return ToolError($"Unknown tool: {name}");

        JsonElement? arguments = null;
        if (p["arguments"] is JsonNode args)
        {
            // Round-trip through JsonDocument so the Worker receives a clean JsonElement.
            var argsJson = args.ToJsonString(JsonOpts);
            arguments = JsonDocument.Parse(argsJson).RootElement.Clone();
        }

        JsonElement workerResp;
        try
        {
            workerResp = await _worker.CallAsync(tool.WorkerTool, arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolError($"Worker call failed: {ex.GetType().Name}: {ex.Message}");
        }

        var success = workerResp.TryGetProperty("success", out var sEl) && sEl.GetBoolean();
        if (!success)
        {
            var err = workerResp.TryGetProperty("error", out var eEl) ? eEl.GetString() : "(no error message)";
            return ToolError($"Worker error: {err}");
        }

        var payload = workerResp.TryGetProperty("data", out var dEl)
            ? JsonSerializer.Serialize(dEl, new JsonSerializerOptions { WriteIndented = true })
            : "(no data)";

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload,
                },
            },
            ["isError"] = false,
        };
    }

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message,
            },
        },
        ["isError"] = true,
    };

    private Task SendResultAsync(JsonNode? id, JsonNode result) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        });

    private Task SendErrorAsync(JsonNode? id, int code, string message) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        });

    private async Task WriteAsync(JsonObject envelope)
    {
        var s = envelope.ToJsonString(JsonOpts);
        await _output.WriteLineAsync(s).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }
}
