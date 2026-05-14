using System.Text.Json;

namespace GxGenie.Gateway;

/// <summary>
/// Slice mínima de configuración que el Gateway necesita para spawnar al Worker.
///
/// Soporta dos modos:
/// <list type="number">
///   <item><b>Con config.json</b>: lee <c>Worker.ExecutablePath</c> y <c>Worker.TimeoutSeconds</c>.</item>
///   <item><b>Per-KB / auto-detect</b>: si no hay <c>config.json</c>, intenta localizar el Worker.exe
///         por env <c>GXGENIE_WORKER_EXE</c> o en el layout estándar del repo (sibling de Gateway/bin/...).
///         En este caso, el cwd actual debería contener un <c>.gxw</c> y el Worker hará auto-detección
///         de la KB cuando lo invoquemos.</item>
/// </list>
/// </summary>
public sealed class GatewayConfig
{
    public string WorkerExecutablePath { get; set; } = "";
    public int WorkerTimeoutSeconds { get; set; } = 120;
    public string ResolvedConfigPath { get; set; } = "";

    /// <summary>
    /// True si el config se resolvió desde un override explícito del usuario
    /// (arg <c>--config</c> o env <c>GXGENIE_CONFIG</c>). False si vino de un fallback
    /// automático (al lado del exe o en raíz del repo). Sólo en el primer caso
    /// se propaga al Worker como <c>GXGENIE_CONFIG</c> — en el segundo dejamos que
    /// el Worker haga su propia resolución (que prioriza auto-detect por cwd).
    /// </summary>
    public bool IsExplicitConfig { get; set; }

    public static GatewayConfig Load(string? explicitPath)
    {
        var envConfig = Environment.GetEnvironmentVariable("GXGENIE_CONFIG");
        var explicitCandidates = new List<string?> { explicitPath, envConfig };
        var autoCandidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "config.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config.json")),
        };

        string? found = null;
        bool isExplicit = false;
        foreach (var c in explicitCandidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) { found = c; isExplicit = true; break; }
        }
        if (found is null)
        {
            foreach (var c in autoCandidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { found = c; break; }
            }
        }

        var cfg = new GatewayConfig { IsExplicitConfig = isExplicit };
        if (found is not null)
        {
            cfg.ResolvedConfigPath = found;
            var json = File.ReadAllText(found);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("Worker", out var w))
            {
                if (w.TryGetProperty("ExecutablePath", out var ep)) cfg.WorkerExecutablePath = ep.GetString() ?? "";
                if (w.TryGetProperty("TimeoutSeconds", out var ts) && ts.TryGetInt32(out var n)) cfg.WorkerTimeoutSeconds = n;
            }
        }

        // Si la ruta del Worker no salió del config (o el config no existe), intentamos auto-detectar.
        if (string.IsNullOrWhiteSpace(cfg.WorkerExecutablePath) || !File.Exists(cfg.WorkerExecutablePath))
        {
            var auto = AutoDetectWorkerExe();
            if (auto is not null) cfg.WorkerExecutablePath = auto;
        }

        if (string.IsNullOrWhiteSpace(cfg.WorkerExecutablePath))
            throw new InvalidOperationException(
                "No se pudo localizar GxGenie.Worker.exe. Probá una de estas:\n" +
                "  • Setear GXGENIE_WORKER_EXE con la ruta completa.\n" +
                "  • Configurar Worker.ExecutablePath en un config.json.\n" +
                "  • Asegurarte de que el Gateway está en el layout estándar del repo.");
        if (!File.Exists(cfg.WorkerExecutablePath))
            throw new FileNotFoundException($"Worker executable not found at: {cfg.WorkerExecutablePath}");

        return cfg;
    }

    /// <summary>
    /// Intenta localizar <c>GxGenie.Worker.exe</c> en:
    /// <list type="number">
    ///   <item>Env <c>GXGENIE_WORKER_EXE</c>.</item>
    ///   <item>Al lado del Gateway (single-folder publish).</item>
    ///   <item>Layout estándar del repo: <c>../../../../GxGenie.Worker/bin/{Release,Debug}/net8.0/</c>.</item>
    /// </list>
    /// </summary>
    private static string? AutoDetectWorkerExe()
    {
        var env = Environment.GetEnvironmentVariable("GXGENIE_WORKER_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var gatewayDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(gatewayDir, "GxGenie.Worker.exe"),
            Path.GetFullPath(Path.Combine(gatewayDir, "..", "..", "..", "..", "GxGenie.Worker", "bin", "Release", "net8.0", "GxGenie.Worker.exe")),
            Path.GetFullPath(Path.Combine(gatewayDir, "..", "..", "..", "..", "GxGenie.Worker", "bin", "Debug", "net8.0", "GxGenie.Worker.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
