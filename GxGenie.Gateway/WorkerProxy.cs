using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GxGenie.Gateway;

/// <summary>
/// Owns a long-lived <see cref="GxGenie.Worker"/> child process. Each request is a single line
/// of JSON written to the worker's stdin; each response is a single line read back from stdout.
/// The Worker's stdio loop is documented in its <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// Calls are serialised by a <see cref="SemaphoreSlim"/> so concurrent <c>tools/call</c>
/// requests from the MCP client don't interleave on the shared pipe. The Worker stays up
/// across calls — restarting per call would re-attach to LocalDB every time.
/// </remarks>
public sealed class WorkerProxy : IDisposable
{
    private readonly string _exePath;
    private readonly int _timeoutSeconds;
    private readonly string _configPath;
    private readonly bool _propagateConfig;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderrBuffer = new();
    private Process? _process;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public WorkerProxy(GatewayConfig config)
    {
        _exePath = config.WorkerExecutablePath;
        _timeoutSeconds = config.WorkerTimeoutSeconds;
        _configPath = config.ResolvedConfigPath;
        _propagateConfig = config.IsExplicitConfig;
    }

    /// <summary>Send one tool request, await one response. Lazily starts the worker on first use.</summary>
    public async Task<JsonElement> CallAsync(string tool, JsonElement? parameters, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var p = _process!;

            var requestId = Guid.NewGuid().ToString("N");
            var requestObj = new Dictionary<string, object?>
            {
                ["tool"] = tool,
                ["id"] = requestId,
            };
            if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
                requestObj["params"] = parameters.Value;

            var requestJson = JsonSerializer.Serialize(requestObj, JsonOpts);
            await p.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);
            await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            // El Worker corre un loop stdin/stdout estrictamente síncrono y FIFO (una request
            // a la vez, una respuesta por línea, en orden). Si una llamada anterior superó el
            // timeout, el Gateway abandonó esa lectura pero el Worker igual terminó de
            // procesarla y escribió su respuesta — que queda sin consumir en el pipe. La
            // próxima llamada leería esa línea vieja primero y la confundiría con la propia
            // (bug reportado: dos tool calls distintas devolviendo el mismo output). Como el
            // Worker siempre ecoa el "id" de la request en su respuesta, correlacionamos por
            // id y descartamos cualquier línea rezagada hasta encontrar la nuestra.
            while (true)
            {
                string? line;
                try
                {
                    line = await p.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Worker did not respond within {_timeoutSeconds}s for tool '{tool}' (request {requestId}). Stderr so far:\n{DrainStderr()}");
                }

                if (line is null)
                    throw new InvalidOperationException($"Worker exited unexpectedly while waiting for '{tool}' (request {requestId}). Stderr:\n{DrainStderr()}");

                JsonElement root;
                try
                {
                    root = JsonDocument.Parse(line).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Worker sent malformed JSON while waiting for '{tool}' (request {requestId}): {ex.Message}\nLine: {line}");
                }

                var responseId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (!string.IsNullOrEmpty(responseId) && !string.Equals(responseId, requestId, StringComparison.Ordinal))
                {
                    // Respuesta rezagada de una llamada anterior que hizo timeout del lado del
                    // Gateway pero terminó igual en el Worker. La descartamos y seguimos
                    // esperando la nuestra — no hay riesgo de loop infinito porque el Worker
                    // procesa una request por vez en el mismo orden en que se encolan.
                    lock (_stderrBuffer)
                    {
                        _stderrBuffer.AppendLine($"[gateway] discarded stale worker response id={responseId} while waiting for '{tool}' (request {requestId})");
                    }
                    continue;
                }

                return root;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false }) return;

        // El Worker debe heredar el cwd del Gateway (que a su vez es el cwd de Claude Code).
        // Esto permite auto-detección "per-KB": cuando Claude se abre en una carpeta de KB,
        // el Worker ve el .gxw allí y arma su config al vuelo, sin necesidad de config.json.
        var userCwd = Environment.CurrentDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = userCwd,
        };
        // Pasamos explícitamente el cwd por env var por si alguna capa intermedia lo cambia.
        psi.Environment["GXGENIE_CWD"] = userCwd;
        // Solo propagamos config.json al Worker si vino de un override EXPLÍCITO del usuario.
        // Si vino de un fallback (al lado del exe o repo root), dejamos que el Worker haga su
        // propia resolución — que prioriza auto-detect por cwd cuando hay un .gxw en la carpeta.
        if (_propagateConfig && !string.IsNullOrEmpty(_configPath))
            psi.Environment["GXGENIE_CONFIG"] = _configPath;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderrBuffer) { _stderrBuffer.AppendLine(e.Data); }
        };
        if (!p.Start())
            throw new InvalidOperationException($"Failed to start worker: {_exePath}");
        p.BeginErrorReadLine();
        _process = p;
    }

    private string DrainStderr()
    {
        lock (_stderrBuffer)
        {
            var s = _stderrBuffer.ToString();
            return string.IsNullOrEmpty(s) ? "(empty)" : s;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_process is { HasExited: false })
            {
                try { _process.StandardInput.Close(); } catch { }
                if (!_process.WaitForExit(2000))
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _gate.Dispose();
        }
    }
}
