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

            var requestObj = new Dictionary<string, object?>
            {
                ["tool"] = tool,
                ["id"] = Guid.NewGuid().ToString("N"),
            };
            if (parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
                requestObj["params"] = parameters.Value;

            var requestJson = JsonSerializer.Serialize(requestObj, JsonOpts);
            await p.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);
            await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var readTask = p.StandardOutput.ReadLineAsync(cts.Token).AsTask();
            string? line;
            try
            {
                line = await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Worker did not respond within {_timeoutSeconds}s for tool '{tool}'. Stderr so far:\n{DrainStderr()}");
            }

            if (line is null)
                throw new InvalidOperationException($"Worker exited unexpectedly. Stderr:\n{DrainStderr()}");

            return JsonDocument.Parse(line).RootElement.Clone();
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
