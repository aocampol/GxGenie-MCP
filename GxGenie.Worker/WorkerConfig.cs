using System.Text.Json;
using System.Xml.Linq;

namespace GxGenie.Worker;

/// <summary>
/// Vista cargada de <c>config.json</c>. Soporta dos formatos:
///
/// 1) <b>Single-KB</b> (formato Fase 1–3, mantenido por compatibilidad):
///    <code>
///    {
///      "GeneXus":       { "Version": "17", "InstallationPath": "...", ... },
///      "KnowledgeBase": { "Path": "...", "ConnectionString": "..." }
///    }
///    </code>
///
/// 2) <b>Multi-KB</b> (formato Fase 4):
///    <code>
///    {
///      "GeneXus":         [ { "Version": "17", ... }, { "Version": "18", ... } ],
///      "KnowledgeBases":  [ { "Name": "SampleKB", "Path": "...", "GeneXusVersion": "17", ... } ],
///      "ActiveKB":        "SampleKB"
///    }
///    </code>
///
/// Internamente todo se normaliza al formato multi-KB y los getters
/// <see cref="ConnectionString"/>, <see cref="KbPath"/>, etc. delegan a la KB
/// activa, lo que permite mutar el estado vía <see cref="SetActiveKb"/> sin
/// reconstruir consumidores que sólo guardan la referencia.
/// </summary>
public sealed class WorkerConfig
{
    public List<GxInstall> GxInstalls { get; set; } = new();
    public List<KbDef> KnowledgeBases { get; set; } = new();
    public string ActiveKbName { get; set; } = "";

    public bool AllowWrite { get; set; }
    public bool AllowBuild { get; set; }
    public bool AuditLogEnabled { get; set; }
    public string AuditLogPath { get; set; } = "";
    public string BackupRoot { get; set; } = "";

    public string ResolvedConfigPath { get; set; } = "";

    // ----- Delegadores a la KB / GX install activos (mantienen API previa) -----

    public KbDef ActiveKb =>
        KnowledgeBases.FirstOrDefault(k => string.Equals(k.Name, ActiveKbName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"Active KB '{ActiveKbName}' not found. Available: " +
            string.Join(", ", KnowledgeBases.Select(k => k.Name)));

    public GxInstall ActiveGx
    {
        get
        {
            var kb = ActiveKb;
            var match = GxInstalls.FirstOrDefault(g =>
                string.Equals(g.Version, kb.GeneXusVersion, StringComparison.OrdinalIgnoreCase))
                ?? GxInstalls.FirstOrDefault(g =>
                    !string.IsNullOrEmpty(g.Version) &&
                    !string.IsNullOrEmpty(kb.GeneXusVersion) &&
                    g.Version.StartsWith(kb.GeneXusVersion[..Math.Min(2, kb.GeneXusVersion.Length)],
                        StringComparison.OrdinalIgnoreCase));
            return match
                ?? GxInstalls.FirstOrDefault()
                ?? throw new InvalidOperationException("No GeneXus installs configured.");
        }
    }

    public string ConnectionString => ActiveKb.ConnectionString;
    public string KbPath => ActiveKb.Path;
    public string KbDirectory => string.IsNullOrEmpty(KbPath) ? "" : Path.GetDirectoryName(KbPath)!;
    public string GxInstallationPath => ActiveGx.InstallationPath;
    public string MsBuildPath => ActiveGx.MsBuildPath;
    public string GxVersion => ActiveKb.GeneXusVersion;
    public string SdkPath => ActiveGx.SdkPath;

    /// <summary>Cambia la KB activa por nombre (case-insensitive). Lanza si no existe.</summary>
    public void SetActiveKb(string kbName)
    {
        var match = KnowledgeBases.FirstOrDefault(k =>
            string.Equals(k.Name, kbName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"KB '{kbName}' not defined in config. Available: " +
                string.Join(", ", KnowledgeBases.Select(k => k.Name)));
        ActiveKbName = match.Name;
    }

    // ----- Loader -----

    /// <summary>
    /// Resuelve la configuración en este orden de prioridad:
    /// <list type="number">
    ///   <item>Path explícito (arg <c>--config</c>) — el usuario manda.</item>
    ///   <item>Variable <c>GXGENIE_CONFIG</c> — override explícito.</item>
    ///   <item><b>Auto-detección por cwd (modo per-KB)</b>: si <c>GXGENIE_CWD</c> (o el cwd del proceso)
    ///         contiene un <c>.gxw</c>, se arma una config al vuelo. Es el modo recomendado:
    ///         abrís Claude Code en la carpeta de la KB y el MCP se bindea automáticamente.</item>
    ///   <item><c>config.json</c> junto al exe (legacy single-folder install).</item>
    ///   <item><c>config.json</c> en la raíz del repo (cuando se corre desde <c>bin/Debug/net8.0/</c>).</item>
    /// </list>
    /// </summary>
    public static WorkerConfig Load(string? path)
    {
        // 1) y 2): overrides explícitos del usuario
        var explicitCandidates = new List<string?>
        {
            path,
            Environment.GetEnvironmentVariable("GXGENIE_CONFIG"),
        };
        foreach (var c in explicitCandidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
                return LoadFromFile(c);
        }

        // 3): auto-detect por cwd. Si la carpeta donde se lanzó Claude Code contiene un .gxw,
        // gana sobre cualquier config.json del repo. Esto hace que el modo per-KB sea "default" sin sorpresas.
        var cwd = Environment.GetEnvironmentVariable("GXGENIE_CWD");
        if (string.IsNullOrEmpty(cwd)) cwd = Environment.CurrentDirectory;
        var auto = AutoDetectFromDirectory(cwd);
        if (auto is not null) return auto;

        // 4) y 5): fallback al config.json instalado
        var fileCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config.json")),
        };
        foreach (var c in fileCandidates)
        {
            if (File.Exists(c)) return LoadFromFile(c);
        }

        throw new FileNotFoundException(
            "No se encontró config.json y la auto-detección falló: " +
            $"el cwd '{cwd}' no contiene un archivo .gxw. " +
            "Andá a la carpeta de una KB o seteá GXGENIE_CONFIG con la ruta a un config.json válido.");
    }

    private static WorkerConfig LoadFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cfg = new WorkerConfig { ResolvedConfigPath = filePath };

        ParseGeneXusSection(root, cfg);
        ParseKnowledgeBaseSection(root, cfg);
        ParseSecuritySection(root, cfg, filePath);

        ApplyDefaults(cfg);
        Validate(cfg);
        return cfg;
    }

    /// <summary>
    /// Construye una <see cref="WorkerConfig"/> autodetectando una KB en la carpeta dada:
    /// busca un único <c>*.gxw</c>, lee <c>knowledgebase.connection</c> para el DBName,
    /// y resuelve la instalación de GeneXus (env <c>GX_PROGRAM_DIR</c> o paths estándar).
    /// Devuelve <c>null</c> si no hay <c>.gxw</c> o no encuentra una instalación de GeneXus.
    /// </summary>
    public static WorkerConfig? AutoDetectFromDirectory(string cwd)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return null;

        var gxwFiles = Directory.GetFiles(cwd, "*.gxw");
        if (gxwFiles.Length == 0) return null;
        var kbPath = gxwFiles[0];
        var kbName = Path.GetFileNameWithoutExtension(kbPath);

        // 1) DBName + version del knowledgebase.connection
        string dbName = "";
        var connFile = Path.Combine(cwd, "knowledgebase.connection");
        if (File.Exists(connFile))
        {
            try
            {
                var doc = XDocument.Load(connFile);
                dbName = doc.Root?.Element("DBName")?.Value ?? "";
            }
            catch { /* fallback abajo */ }
        }
        if (string.IsNullOrEmpty(dbName)) dbName = "GX_KB_" + kbName;

        // 2) Versión de GX — heurística sobre el path; default 17.
        var gxVersion = "17";
        if (System.Text.RegularExpressions.Regex.IsMatch(cwd, @"Gx?18", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            gxVersion = "18";

        // 3) Localizar instalación de GeneXus
        var gxInstall = AutoDetectGxInstall(gxVersion);
        if (gxInstall is null) return null;

        // 4) Estado por-KB en .gxmcp/ dentro de la carpeta de la KB
        var gxmcpDir = Path.Combine(cwd, ".gxmcp");

        var cfg = new WorkerConfig
        {
            ResolvedConfigPath = "<auto-detected:" + cwd + ">",
            ActiveKbName = kbName,
            AllowWrite = false,
            AllowBuild = false,
            AuditLogEnabled = true,
            AuditLogPath = Path.Combine(gxmcpDir, "audit.log"),
            BackupRoot = Path.Combine(gxmcpDir, "backups"),
        };
        cfg.GxInstalls.Add(gxInstall);
        cfg.KnowledgeBases.Add(new KbDef
        {
            Name = kbName,
            Path = kbPath,
            ConnectionString = $"Server=(LocalDB)\\MSSQLLocalDB;Database={dbName};Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15",
            GeneXusVersion = gxVersion,
        });

        // Permitir overrides via env vars sin necesidad de config.json
        if (string.Equals(Environment.GetEnvironmentVariable("GXGENIE_ALLOW_WRITE"), "true", StringComparison.OrdinalIgnoreCase))
            cfg.AllowWrite = true;
        if (string.Equals(Environment.GetEnvironmentVariable("GXGENIE_ALLOW_BUILD"), "true", StringComparison.OrdinalIgnoreCase))
            cfg.AllowBuild = true;

        return cfg;
    }

    private static GxInstall? AutoDetectGxInstall(string preferredVersion)
    {
        var envDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
        var sdkDir = Environment.GetEnvironmentVariable("GX_SDK_DIR") ?? "";
        var msbuild = FindMsBuildExe();

        var standardPaths = new List<string>();
        if (!string.IsNullOrEmpty(envDir)) standardPaths.Add(envDir);
        // Versión preferida primero
        standardPaths.Add($@"C:\Program Files (x86)\GeneXus\GeneXus{preferredVersion}U1");
        standardPaths.Add($@"C:\Program Files (x86)\GeneXus\GeneXus{preferredVersion}U11");
        standardPaths.Add($@"C:\Program Files (x86)\GeneXus\GeneXus{preferredVersion}");
        // Fallbacks generales
        foreach (var v in new[] { "17U1", "17U11", "17", "18", "18U1" })
        {
            var p = $@"C:\Program Files (x86)\GeneXus\GeneXus{v}";
            if (!standardPaths.Contains(p)) standardPaths.Add(p);
        }

        var hit = standardPaths.FirstOrDefault(Directory.Exists);
        if (hit is null) return null;

        return new GxInstall
        {
            Version = preferredVersion,
            InstallationPath = hit,
            SdkPath = sdkDir,
            MsBuildPath = msbuild ?? @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe",
        };
    }

    private static string? FindMsBuildExe()
    {
        var paths = new[]
        {
            @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe",
            @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
        };
        return paths.FirstOrDefault(File.Exists);
    }

    private static void ParseGeneXusSection(JsonElement root, WorkerConfig cfg)
    {
        if (!root.TryGetProperty("GeneXus", out var gx)) return;

        if (gx.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in gx.EnumerateArray())
                cfg.GxInstalls.Add(ReadGxInstall(item));
        }
        else if (gx.ValueKind == JsonValueKind.Object)
        {
            cfg.GxInstalls.Add(ReadGxInstall(gx));
        }
    }

    private static GxInstall ReadGxInstall(JsonElement el) => new()
    {
        Version = el.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "",
        InstallationPath = el.TryGetProperty("InstallationPath", out var ip) ? ip.GetString() ?? "" : "",
        SdkPath = el.TryGetProperty("SdkPath", out var sp) ? sp.GetString() ?? "" : "",
        MsBuildPath = el.TryGetProperty("MSBuildPath", out var mp) ? mp.GetString() ?? "" : "",
    };

    private static void ParseKnowledgeBaseSection(JsonElement root, WorkerConfig cfg)
    {
        // Nuevo formato: "KnowledgeBases": [ { Name, Path, ConnectionString, GeneXusVersion }, ... ]
        if (root.TryGetProperty("KnowledgeBases", out var kbs) && kbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in kbs.EnumerateArray())
                cfg.KnowledgeBases.Add(ReadKbDef(item));

            if (root.TryGetProperty("ActiveKB", out var ak))
                cfg.ActiveKbName = ak.GetString() ?? "";

            if (string.IsNullOrEmpty(cfg.ActiveKbName) && cfg.KnowledgeBases.Count > 0)
                cfg.ActiveKbName = cfg.KnowledgeBases[0].Name;
            return;
        }

        // Formato legacy: "KnowledgeBase": { Path, ConnectionString }
        if (!root.TryGetProperty("KnowledgeBase", out var kb)) return;

        var legacy = new KbDef
        {
            Path = kb.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : "",
            ConnectionString = kb.TryGetProperty("ConnectionString", out var cs) ? cs.GetString() ?? "" : "",
            // Versión heredada de la sección GeneXus single-object si está disponible
            GeneXusVersion = cfg.GxInstalls.FirstOrDefault()?.Version ?? "17",
        };
        // Derivamos un nombre del archivo .gxw
        legacy.Name = string.IsNullOrEmpty(legacy.Path)
            ? "default"
            : Path.GetFileNameWithoutExtension(legacy.Path);
        cfg.KnowledgeBases.Add(legacy);
        cfg.ActiveKbName = legacy.Name;
    }

    private static KbDef ReadKbDef(JsonElement el) => new()
    {
        Name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
        Path = el.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : "",
        ConnectionString = el.TryGetProperty("ConnectionString", out var cs) ? cs.GetString() ?? "" : "",
        GeneXusVersion = el.TryGetProperty("GeneXusVersion", out var gv) ? gv.GetString() ?? "17" : "17",
    };

    private static void ParseSecuritySection(JsonElement root, WorkerConfig cfg, string configPath)
    {
        if (!root.TryGetProperty("Security", out var sec)) return;
        if (sec.TryGetProperty("AllowWrite", out var aw)) cfg.AllowWrite = aw.ValueKind == JsonValueKind.True;
        if (sec.TryGetProperty("AllowBuild", out var ab)) cfg.AllowBuild = ab.ValueKind == JsonValueKind.True;
        if (sec.TryGetProperty("AuditLog", out var al)) cfg.AuditLogEnabled = al.ValueKind == JsonValueKind.True;
        if (sec.TryGetProperty("AuditLogPath", out var alp)) cfg.AuditLogPath = alp.GetString() ?? "";
        if (sec.TryGetProperty("BackupRoot", out var br)) cfg.BackupRoot = br.GetString() ?? "";
    }

    private static void ApplyDefaults(WorkerConfig cfg)
    {
        var configDir = Path.GetDirectoryName(cfg.ResolvedConfigPath) ?? AppContext.BaseDirectory;

        // Defaults para Security paths
        if (string.IsNullOrWhiteSpace(cfg.BackupRoot))
            cfg.BackupRoot = Path.Combine(configDir, "backups");
        if (string.IsNullOrWhiteSpace(cfg.AuditLogPath))
            cfg.AuditLogPath = Path.Combine(configDir, "audit.log");

        // Default MSBuild path por GxInstall (si falta)
        foreach (var gx in cfg.GxInstalls)
        {
            if (string.IsNullOrWhiteSpace(gx.MsBuildPath))
                gx.MsBuildPath = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe";
        }
    }

    private static void Validate(WorkerConfig cfg)
    {
        if (cfg.KnowledgeBases.Count == 0)
            throw new InvalidOperationException("config.json: no Knowledge Bases configured (use 'KnowledgeBase' or 'KnowledgeBases').");
        if (cfg.GxInstalls.Count == 0)
            throw new InvalidOperationException("config.json: no GeneXus installs configured (use 'GeneXus').");
        if (string.IsNullOrEmpty(cfg.ActiveKbName))
            cfg.ActiveKbName = cfg.KnowledgeBases[0].Name;

        // Sanity: la KB activa debe existir y tener ConnectionString
        var active = cfg.KnowledgeBases.FirstOrDefault(k =>
            string.Equals(k.Name, cfg.ActiveKbName, StringComparison.OrdinalIgnoreCase));
        if (active is null)
            throw new InvalidOperationException(
                $"config.json: ActiveKB '{cfg.ActiveKbName}' not in KnowledgeBases. " +
                $"Available: {string.Join(", ", cfg.KnowledgeBases.Select(k => k.Name))}");
        if (string.IsNullOrWhiteSpace(active.ConnectionString))
            throw new InvalidOperationException($"KB '{active.Name}': ConnectionString is required.");
    }
}
