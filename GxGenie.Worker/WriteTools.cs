using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GxGenie.Worker;

/// <summary>
/// Implementation of the Phase-3 write tools. Each entry point performs three things in this order:
/// 1) authorisation check (config.Security.AllowWrite / AllowBuild), 2) backup snapshot via
/// <see cref="BackupHelper"/>, 3) MSBuild task execution via <see cref="MsBuildRunner"/>. Every
/// call — success or failure — is recorded by <see cref="AuditLogger"/>.
/// </summary>
public sealed class WriteTools
{
    private readonly WorkerConfig _config;
    private readonly MsBuildRunner _msb;
    private readonly BackupHelper _backup;
    private readonly AuditLogger _audit;
    private readonly KbRepository _repo;

    public WriteTools(WorkerConfig config, KbRepository repo)
    {
        _config = config;
        _repo = repo;
        _msb = new MsBuildRunner(config);
        _backup = new BackupHelper(config);
        _audit = new AuditLogger(config);
    }

    // -------- gx_export_xpz --------

    public sealed record ExportArgs(string[] Objects, string OutputPath, bool IncludeChildren = true);

    public object ExportXpz(ExportArgs args)
    {
        EnsureWriteAllowed("gx_export_xpz");
        if (args.Objects is null || args.Objects.Length == 0)
            throw new ArgumentException("'objects' is required and must contain at least one item");
        if (string.IsNullOrWhiteSpace(args.OutputPath))
            throw new ArgumentException("'output_path' is required");

        EnsureParentDir(args.OutputPath);
        var objectsAttr = string.Join(",", args.Objects);

        var task = new MsBuildRunner.TaskInvocation("Export", new Dictionary<string, string>
        {
            ["File"] = args.OutputPath,
            ["Objects"] = objectsAttr,
            ["IncludeChildren"] = args.IncludeChildren ? "True" : "False",
        });

        var result = _msb.RunInsideKb(new[] { task }, readOnly: true);
        if (!result.Success)
        {
            _audit.Write("WRITE", "gx_export_xpz", objectsAttr, "FAILURE", $"exit={result.ExitCode} stdout={Truncate(result.StdOut, 500)}");
            throw new InvalidOperationException($"Export failed (exit {result.ExitCode}):\n{result.StdOut}\n{result.StdErr}");
        }

        var size = File.Exists(args.OutputPath) ? new FileInfo(args.OutputPath).Length : 0L;
        _audit.Write("WRITE", "gx_export_xpz", objectsAttr, "SUCCESS", $"file={args.OutputPath} bytes={size}");
        return new
        {
            success = true,
            output_path = args.OutputPath,
            objects = args.Objects,
            bytes = size,
            log_tail = LastLines(result.StdOut, 8),
        };
    }

    // -------- gx_import_xpz --------

    public sealed record ImportArgs(string XpzPath, string ImportType = "OnlyNew", bool PreviewMode = false, bool Backup = true);

    public object ImportXpz(ImportArgs args)
    {
        EnsureWriteAllowed("gx_import_xpz");
        if (string.IsNullOrWhiteSpace(args.XpzPath))
            throw new ArgumentException("'xpz_path' is required");
        if (!File.Exists(args.XpzPath))
            throw new FileNotFoundException($"XPZ not found: {args.XpzPath}");

        string? backupPath = null;
        if (args.Backup && !args.PreviewMode)
        {
            var bk = _backup.Snapshot($"import_{Path.GetFileNameWithoutExtension(args.XpzPath)}");
            backupPath = bk.BackupPath;
        }

        var task = new MsBuildRunner.TaskInvocation("Import", new Dictionary<string, string>
        {
            ["File"] = args.XpzPath,
            ["ImportType"] = args.ImportType,
            ["AutomaticBackup"] = "False", // we do our own backup at the SQL level above
            ["PreviewMode"] = args.PreviewMode ? "True" : "False",
        });

        var result = _msb.RunInsideKb(new[] { task });
        if (!result.Success)
        {
            _audit.Write("WRITE", "gx_import_xpz", args.XpzPath, "FAILURE", $"exit={result.ExitCode} backup={backupPath} stdout={Truncate(result.StdOut, 500)}");
            throw new InvalidOperationException($"Import failed (exit {result.ExitCode}). Backup: {backupPath ?? "(none)"}\n{result.StdOut}\n{result.StdErr}");
        }

        _audit.Write("WRITE", "gx_import_xpz", args.XpzPath, "SUCCESS", $"backup={backupPath} preview={args.PreviewMode}");
        return new
        {
            success = true,
            xpz_path = args.XpzPath,
            backup_path = backupPath,
            preview = args.PreviewMode,
            log_tail = LastLines(result.StdOut, 12),
        };
    }

    // -------- gx_build_object --------

    public sealed record BuildArgs(string ObjectName, bool ForceRebuild = false);

    public object BuildObject(BuildArgs args)
    {
        if (!_config.AllowBuild)
            throw new UnauthorizedAccessException("Builds are disabled in config (Security.AllowBuild = false).");
        if (string.IsNullOrWhiteSpace(args.ObjectName))
            throw new ArgumentException("'object_name' is required");

        var task = new MsBuildRunner.TaskInvocation("BuildOne", new Dictionary<string, string>
        {
            ["ObjectName"] = args.ObjectName,
            ["ForceRebuild"] = args.ForceRebuild ? "True" : "False",
        });

        var result = _msb.RunInsideKb(new[] { task });
        var ok = result.Success;
        _audit.Write("BUILD", "gx_build_object", args.ObjectName, ok ? "SUCCESS" : "FAILURE", $"exit={result.ExitCode}");
        if (!ok)
            throw new InvalidOperationException($"Build failed (exit {result.ExitCode}):\n{result.StdOut}\n{result.StdErr}");

        return new { success = true, @object = args.ObjectName, log_tail = LastLines(result.StdOut, 20) };
    }

    // -------- gx_delete_object --------

    public sealed record DeleteArgs(string[] Objects, bool IncludeChildren = true, bool FailWhenNone = true);

    public object DeleteObject(DeleteArgs args)
    {
        EnsureWriteAllowed("gx_delete_object");
        if (args.Objects is null || args.Objects.Length == 0)
            throw new ArgumentException("'objects' is required and must contain at least one item");

        var objectsAttr = string.Join(",", args.Objects);
        var bk = _backup.Snapshot($"delete_{SanitizeForFilename(objectsAttr)}");

        var task = new MsBuildRunner.TaskInvocation("DeleteObject", new Dictionary<string, string>
        {
            ["Objects"] = objectsAttr,
            ["IncludeChildren"] = args.IncludeChildren ? "True" : "False",
            ["FailWhenNone"] = args.FailWhenNone ? "True" : "False",
        });

        var result = _msb.RunInsideKb(new[] { task });
        if (!result.Success)
        {
            _audit.Write("WRITE", "gx_delete_object", objectsAttr, "FAILURE", $"exit={result.ExitCode} backup={bk.BackupPath}");
            throw new InvalidOperationException($"Delete failed (exit {result.ExitCode}). Backup: {bk.BackupPath}\n{result.StdOut}\n{result.StdErr}");
        }

        _audit.Write("WRITE", "gx_delete_object", objectsAttr, "SUCCESS", $"backup={bk.BackupPath}");
        return new
        {
            success = true,
            objects = args.Objects,
            backup_path = bk.BackupPath,
            log_tail = LastLines(result.StdOut, 12),
        };
    }

    // -------- gx_create_procedure --------

    public sealed record CreateProcArgs(string Name, string? Description, string? Module, string? Source, string[]? Variables);

    /// <summary>
    /// Creates a new Procedure via XPZ import: builds a minimal XPZ skeleton with the requested
    /// name and an optional Procedure body source, then drives <see cref="ImportXpz"/>. This is
    /// safer than INSERTing into the UDM tables because the Import task handles all coherence
    /// (Entity/EntityVersion/EntityVersionComposition/ModelEntityVersion/ModelEntityProperty).
    /// </summary>
    public object CreateProcedure(CreateProcArgs args)
    {
        EnsureWriteAllowed("gx_create_procedure");
        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("'name' is required");
        if (!Regex.IsMatch(args.Name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$"))
            throw new ArgumentException("'name' must match ^[A-Za-z][A-Za-z0-9_]{0,63}$");

        // Snapshot first so a corrupted import is recoverable.
        var bk = _backup.Snapshot($"create_proc_{args.Name}");

        // Materialise a tiny XPZ in temp and import it.
        var tempDir = Path.Combine(Path.GetTempPath(), "gxmcp", "xpz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var xpzPath = Path.Combine(tempDir, args.Name + ".xpz");
        var xml = XpzTemplates.ProcedureXml(
            name: args.Name,
            description: args.Description ?? args.Name,
            source: args.Source ?? "// GxGenie-generated empty procedure"
        );
        XpzTemplates.WriteXpz(xpzPath, args.Name + ".xml", xml);

        var task = new MsBuildRunner.TaskInvocation("Import", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["ImportType"] = "OnlyNew",
            ["AutomaticBackup"] = "False",
        });

        var result = _msb.RunInsideKb(new[] { task });
        if (!result.Success)
        {
            _audit.Write("WRITE", "gx_create_procedure", args.Name, "FAILURE", $"exit={result.ExitCode} backup={bk.BackupPath} xpz={xpzPath} stdout={Truncate(result.StdOut, 500)}");
            throw new InvalidOperationException($"Create procedure failed (exit {result.ExitCode}). Backup: {bk.BackupPath}\nXPZ kept at: {xpzPath}\n{result.StdOut}\n{result.StdErr}");
        }

        _audit.Write("WRITE", "gx_create_procedure", args.Name, "SUCCESS", $"backup={bk.BackupPath} xpz={xpzPath}");
        return new
        {
            success = true,
            name = args.Name,
            backup_path = bk.BackupPath,
            xpz_path = xpzPath,
            log_tail = LastLines(result.StdOut, 12),
        };
    }

    // -------- gx_update_object_code --------

    public sealed record UpdateCodeArgs(string Type, string Name, string NewSource, string? Part = null);

    /// <summary>
    /// Actualiza el código fuente de un Part de un objeto existente.
    /// Pipeline: backup SQL → <c>Export</c> task (genera .xpz con el objeto) → unzip →
    /// reemplazo del <c>&lt;Source&gt;</c> del Part target → re-zip → <c>Import</c>
    /// con <c>ImportType=UpdatedAndNew</c>. Es el camino canónico — GeneXus valida
    /// el cambio contra su BL al importar, así que no podemos corromper la KB
    /// con un source malformado.
    /// </summary>
    public object UpdateObjectCode(UpdateCodeArgs args)
    {
        EnsureWriteAllowed("gx_update_object_code");
        if (string.IsNullOrWhiteSpace(args.Type)) throw new ArgumentException("'type' is required (ej: 'Procedure')");
        if (string.IsNullOrWhiteSpace(args.Name)) throw new ArgumentException("'name' is required");
        if (args.NewSource is null) throw new ArgumentException("'new_source' is required (puede ser vacío pero no null)");

        var partName = string.IsNullOrWhiteSpace(args.Part) ? "source" : args.Part!.Trim();
        var partGuid = XpzPartMap.Resolve(args.Type, partName)
            ?? throw new ArgumentException(BuildPartNotKnownError(args.Type, partName));

        // 1) Backup ANTES de cualquier cosa — el import puede fallar y queremos rollback.
        var tag = SanitizeForFilename($"update_{args.Type}_{args.Name}_{partName}");
        var bk = _backup.Snapshot(tag);

        // 2) Export del objeto a un .xpz temporal.
        var tempDir = Path.Combine(Path.GetTempPath(), "gxmcp", "update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var xpzPath = Path.Combine(tempDir, args.Name + ".xpz");

        var exportTask = new MsBuildRunner.TaskInvocation("Export", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["Objects"] = $"{args.Type}:{args.Name}",
            ["IncludeChildren"] = "False",
        });
        var exportResult = _msb.RunInsideKb(new[] { exportTask }, readOnly: true);
        if (!exportResult.Success || !File.Exists(xpzPath) || new FileInfo(xpzPath).Length == 0)
        {
            _audit.Write("WRITE", "gx_update_object_code", $"{args.Type}:{args.Name}", "FAILURE",
                $"export failed: exit={exportResult.ExitCode} backup={bk.BackupPath}");
            throw new InvalidOperationException(
                $"Export del objeto falló (exit {exportResult.ExitCode}). ¿Existe {args.Type}:{args.Name}? Backup: {bk.BackupPath}\n" +
                $"{exportResult.StdOut}\n{exportResult.StdErr}");
        }

        // 3) Modificar el XPZ in-place.
        try
        {
            ReplacePartSourceInXpz(xpzPath, partGuid, args.NewSource);
        }
        catch (Exception ex)
        {
            _audit.Write("WRITE", "gx_update_object_code", $"{args.Type}:{args.Name}", "FAILURE",
                $"xpz modify failed: {ex.GetType().Name}: {ex.Message} backup={bk.BackupPath}");
            throw;
        }

        // 4) Import con UpdatedAndNew (sin backup adicional — ya hicimos el nuestro).
        var importTask = new MsBuildRunner.TaskInvocation("Import", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["ImportType"] = "UpdatedAndNew",
            ["AutomaticBackup"] = "False",
        });
        var importResult = _msb.RunInsideKb(new[] { importTask });
        if (!importResult.Success)
        {
            _audit.Write("WRITE", "gx_update_object_code", $"{args.Type}:{args.Name}", "FAILURE",
                $"import failed: exit={importResult.ExitCode} backup={bk.BackupPath} xpz={xpzPath}");
            throw new InvalidOperationException(
                $"Import del XPZ modificado falló (exit {importResult.ExitCode}). " +
                $"Restaurá con: RESTORE DATABASE ... FROM DISK='{bk.BackupPath}' WITH REPLACE.\n" +
                $"XPZ preservado en: {xpzPath}\n{importResult.StdOut}\n{importResult.StdErr}");
        }

        _audit.Write("WRITE", "gx_update_object_code", $"{args.Type}:{args.Name}", "SUCCESS",
            $"part={partName} bytes={args.NewSource.Length} backup={bk.BackupPath}");
        return new
        {
            success = true,
            @object = $"{args.Type}:{args.Name}",
            part = partName,
            bytes = args.NewSource.Length,
            backup_path = bk.BackupPath,
            xpz_path = xpzPath,
            log_tail = LastLines(importResult.StdOut, 8),
        };
    }

    /// <summary>
    /// Abre el .xpz como ZIP, encuentra el único archivo XML, ubica el <c>&lt;Part type="guid"&gt;</c>
    /// target y reemplaza el contenido de su <c>&lt;Source&gt;</c> por un nuevo CDATA. Re-empaqueta
    /// preservando el nombre del entry. Si el part no existe o no tiene <c>&lt;Source&gt;</c>, lanza.
    /// </summary>
    private static void ReplacePartSourceInXpz(string xpzPath, string targetPartGuid, string newSource)
    {
        string innerName;
        XDocument doc;
        using (var fs = File.OpenRead(xpzPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("El .xpz exportado no contiene un archivo .xml.");
            innerName = entry.FullName;
            using var stream = entry.Open();
            doc = XDocument.Load(stream);
        }

        var candidates = doc.Descendants("Part")
            .Where(p => string.Equals(p.Attribute("type")?.Value, targetPartGuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            var found = doc.Descendants("Part")
                .Select(p => p.Attribute("type")?.Value)
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct()
                .ToList();
            throw new InvalidOperationException(
                $"No se encontró ningún <Part type=\"{targetPartGuid}\"> en el XPZ exportado. " +
                $"Parts presentes: {string.Join(", ", found)}");
        }
        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"Se encontraron {candidates.Count} <Part type=\"{targetPartGuid}\"> — esperaba exactamente 1.");

        var sourceEl = candidates[0].Element("Source")
            ?? throw new InvalidOperationException(
                $"El <Part type=\"{targetPartGuid}\"> no tiene un sub-elemento <Source> editable. " +
                "Este Part probablemente guarda Properties u otro formato no-textual.");
        sourceEl.RemoveNodes();
        sourceEl.Add(new XCData(newSource));

        if (doc.Declaration is null) doc.Declaration = new XDeclaration("1.0", "utf-8", null);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };

        // Re-empaquetar
        File.Delete(xpzPath);
        using var outFs = new FileStream(xpzPath, FileMode.CreateNew);
        using var outZip = new ZipArchive(outFs, ZipArchiveMode.Create, leaveOpen: false);
        var newEntry = outZip.CreateEntry(innerName, CompressionLevel.Optimal);
        using var ws = newEntry.Open();
        using var xw = XmlWriter.Create(ws, settings);
        doc.Save(xw);
    }

    private static string BuildPartNotKnownError(string objectType, string partName)
    {
        var known = XpzPartMap.KnownPartsFor(objectType);
        if (known.Count > 0)
            return $"Part '{partName}' no registrado para tipo '{objectType}'. " +
                   $"Disponibles: {string.Join(", ", known)}.";
        return $"Tipo '{objectType}' no tiene parts registrados en XpzPartMap. " +
               $"Tipos conocidos: {string.Join(", ", XpzPartMap.KnownObjectTypes)}. " +
               "Para sumar uno, hacé un export real y agregá los GUIDs a XpzPartMap.cs.";
    }

    // -------- helpers --------

    private void EnsureWriteAllowed(string tool)
    {
        if (!_config.AllowWrite)
        {
            _audit.Write("WRITE", tool, "-", "BLOCKED", "AllowWrite=false in config");
            throw new UnauthorizedAccessException($"Write operations are disabled (Security.AllowWrite = false). Refusing: {tool}");
        }
    }

    private static void EnsureParentDir(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string LastLines(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.Split('\n');
        var start = Math.Max(0, lines.Length - n);
        return string.Join("\n", lines.Skip(start)).TrimEnd();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    private static string SanitizeForFilename(string s)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ',', ' ' }).ToHashSet();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim('_');
        if (safe.Length > 40) safe = safe.Substring(0, 40);
        return string.IsNullOrEmpty(safe) ? "objects" : safe;
    }
}
