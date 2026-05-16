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

    // -------- gx_add_attribute --------

    public sealed record AddAttributeArgs(
        string Name,
        string? DataType,
        int? Length,
        int? Decimals,
        string? Description,
        string? BasedOnDomain,
        string? Transaction,
        string? Level,
        bool IsKey,
        bool Autonumber);

    private static readonly string[] ValidAttCustomTypes =
    {
        "bas:Numeric", "bas:Character", "bas:VarChar", "bas:Date", "bas:DateTime",
        "bas:Boolean", "bas:BLOB", "bas:Image",
    };

    /// <summary>
    /// Crea un atributo nuevo en la KB y opcionalmente lo asocia a una Transaction existente.
    /// Pipeline standalone (sin Transaction): construye un XPZ minimal con la sección
    /// <c>&lt;Attributes&gt;</c> e importa con <c>OnlyNew</c>. Pipeline con Transaction:
    /// exporta la Transaction, le agrega el nuevo <c>&lt;Attribute&gt;</c> a la Structure
    /// (root o sub-level), agrega la sección <c>&lt;Attributes&gt;</c>, e importa con
    /// <c>UpdatedAndNew</c>. Backup SQL automático antes en ambos casos.
    /// </summary>
    public object AddAttribute(AddAttributeArgs args)
    {
        EnsureWriteAllowed("gx_add_attribute");
        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("'name' is required");
        if (!Regex.IsMatch(args.Name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$"))
            throw new ArgumentException("'name' must match ^[A-Za-z][A-Za-z0-9_]{0,63}$");

        var hasType = !string.IsNullOrWhiteSpace(args.DataType);
        var hasDomain = !string.IsNullOrWhiteSpace(args.BasedOnDomain);
        if (hasType == hasDomain)
            throw new ArgumentException("Provide exactly one of 'data_type' or 'based_on_domain'.");

        if (hasType && !ValidAttCustomTypes.Contains(args.DataType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Invalid 'data_type' '{args.DataType}'. Valid: {string.Join(", ", ValidAttCustomTypes)}");

        // Snapshot SQL backup BEFORE any KB mutation.
        var bk = _backup.Snapshot($"add_attr_{SanitizeForFilename(args.Name)}");

        var newAttrGuid = Guid.NewGuid().ToString().ToLowerInvariant();
        var tempDir = Path.Combine(Path.GetTempPath(), "gxmcp", "addattr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var xpzPath = Path.Combine(tempDir, args.Name + ".xpz");

        string? attachedToTransaction = null;
        string? attachedToLevel = null;

        if (string.IsNullOrWhiteSpace(args.Transaction))
        {
            // Standalone: just create the attribute.
            BuildStandaloneAttributeXpz(xpzPath, args, newAttrGuid);
        }
        else
        {
            // Combined: export the Transaction, modify its Structure to reference the
            // new attribute, and add the Attribute object inline. Import as one unit.
            BuildTransactionWithAttributeXpz(xpzPath, args, newAttrGuid, out attachedToTransaction, out attachedToLevel);
        }

        var importType = string.IsNullOrWhiteSpace(args.Transaction) ? "OnlyNew" : "UpdatedAndNew";
        var importTask = new MsBuildRunner.TaskInvocation("Import", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["ImportType"] = importType,
            ["AutomaticBackup"] = "False",
        });

        var result = _msb.RunInsideKb(new[] { importTask });
        if (!result.Success)
        {
            _audit.Write("WRITE", "gx_add_attribute", args.Name, "FAILURE",
                $"exit={result.ExitCode} backup={bk.BackupPath} xpz={xpzPath} transaction={args.Transaction}");
            throw new InvalidOperationException(
                $"Add attribute failed (exit {result.ExitCode}). Backup: {bk.BackupPath}\nXPZ kept at: {xpzPath}\n{result.StdOut}\n{result.StdErr}");
        }

        _audit.Write("WRITE", "gx_add_attribute", args.Name, "SUCCESS",
            $"guid={newAttrGuid} type={args.DataType ?? ("Domain:" + args.BasedOnDomain)} backup={bk.BackupPath} transaction={attachedToTransaction} level={attachedToLevel}");

        return new
        {
            success = true,
            name = args.Name,
            guid = newAttrGuid,
            data_type = args.DataType,
            based_on_domain = args.BasedOnDomain,
            attached_to_transaction = attachedToTransaction,
            attached_to_level = attachedToLevel,
            is_key = args.IsKey,
            backup_path = bk.BackupPath,
            xpz_path = xpzPath,
            log_tail = LastLines(result.StdOut, 12),
        };
    }

    private static void BuildStandaloneAttributeXpz(string xpzPath, AddAttributeArgs args, string newAttrGuid)
    {
        var attrEl = BuildAttributeXElement(args, newAttrGuid);
        var doc = NewExportFileSkeleton();
        var attributesSection = new XElement("Attributes", attrEl);
        doc.Root!.Add(attributesSection);
        EnsureAttributeReference(doc);
        WriteXpz(xpzPath, args.Name, doc);
    }

    private void BuildTransactionWithAttributeXpz(string xpzPath, AddAttributeArgs args, string newAttrGuid,
        out string attachedToTransaction, out string attachedToLevel)
    {
        // 1) Export the target Transaction to a temp xpz so we can pick up its existing Structure.
        var tempExport = Path.ChangeExtension(xpzPath, ".exported.xpz");
        var exportTask = new MsBuildRunner.TaskInvocation("Export", new Dictionary<string, string>
        {
            ["File"] = tempExport,
            ["Objects"] = "Transaction:" + args.Transaction,
            ["IncludeChildren"] = "False",
        });
        var exportResult = _msb.RunInsideKb(new[] { exportTask }, readOnly: true);
        if (!exportResult.Success || !File.Exists(tempExport) || new FileInfo(tempExport).Length == 0)
            throw new InvalidOperationException(
                $"Could not export Transaction '{args.Transaction}' (exit {exportResult.ExitCode}):\n{exportResult.StdOut}");

        // 2) Load the exported xpz, locate the Transaction Object + its Structure Part.
        XDocument doc;
        string innerXmlName;
        using (var fs = File.OpenRead(tempExport))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Exported xpz has no .xml entry");
            innerXmlName = entry.FullName;
            using var stream = entry.Open();
            doc = XDocument.Load(stream);
        }

        var trnObj = doc.Descendants("Object").FirstOrDefault(o =>
            string.Equals(o.Attribute("type")?.Value, XpzPartMap.Type_Transaction, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Attribute("name")?.Value, args.Transaction, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find Transaction '{args.Transaction}' in the exported xpz");

        var structurePart = trnObj.Elements("Part")
            .FirstOrDefault(p => string.Equals(p.Attribute("type")?.Value, "264be5fb-1b28-4b25-a598-6ca900dd059f", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Transaction has no Structure Part — cannot attach attribute");

        // 3) Find target Level. Default to root Level (first one).
        var allLevels = structurePart.Elements("Level").ToList();
        if (allLevels.Count == 0)
            throw new InvalidOperationException("Structure has no Level elements");

        XElement targetLevel;
        if (string.IsNullOrWhiteSpace(args.Level))
        {
            targetLevel = allLevels[0];
        }
        else
        {
            targetLevel = structurePart.Descendants("Level")
                .FirstOrDefault(l => string.Equals(l.Attribute("Name")?.Value, args.Level, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Level '{args.Level}' not found in Transaction '{args.Transaction}'. Available: " +
                    string.Join(", ", structurePart.Descendants("Level").Select(l => l.Attribute("Name")?.Value ?? "(unnamed)")));
        }
        attachedToTransaction = args.Transaction!;
        attachedToLevel = targetLevel.Attribute("Name")?.Value ?? args.Transaction!;

        // 4) Append <Attribute key="..." guid="...">Name</Attribute> to the target Level.
        var newAttrRef = new XElement("Attribute",
            new XAttribute("key", args.IsKey ? "True" : "False"),
            new XAttribute("guid", newAttrGuid),
            args.Name);
        targetLevel.Add(newAttrRef);

        // 5) Add or update the <Attributes> section at the root with the new attribute definition.
        var attrEl = BuildAttributeXElement(args, newAttrGuid);
        var attributesSection = doc.Root!.Element("Attributes");
        if (attributesSection is null)
        {
            attributesSection = new XElement("Attributes");
            // Insert after <Objects> if present, else append.
            var objects = doc.Root.Element("Objects");
            if (objects != null) objects.AddAfterSelf(attributesSection);
            else doc.Root.Add(attributesSection);
        }
        attributesSection.Add(attrEl);

        // 6) Make sure Dependencies references "Attribute" object type.
        EnsureAttributeReference(doc);

        // 7) Re-zip into xpzPath.
        WriteXpzWithDoc(xpzPath, innerXmlName, doc);
    }

    private static XElement BuildAttributeXElement(AddAttributeArgs args, string newAttrGuid)
    {
        var properties = new XElement("Properties");
        AddProperty(properties, "Name", args.Name);
        if (!string.IsNullOrEmpty(args.Description))
            AddProperty(properties, "Description", args.Description!);
        if (!string.IsNullOrEmpty(args.BasedOnDomain))
        {
            AddProperty(properties, "idBasedOn", "Domain:" + args.BasedOnDomain);
        }
        else
        {
            AddProperty(properties, "ATTCUSTOMTYPE", args.DataType!);
            if (args.Length.HasValue) AddProperty(properties, "Length", args.Length.Value.ToString());
            if (args.Decimals.HasValue && args.Decimals.Value > 0) AddProperty(properties, "Decimals", args.Decimals.Value.ToString());
            if (args.Length.HasValue) AddProperty(properties, "AttMaxLen", args.Length.Value.ToString());
        }
        if (args.Autonumber) AddProperty(properties, "AUTONUMBER", "True");
        AddProperty(properties, "IsDefault", "False");

        return new XElement("Attribute",
            new XAttribute("parentGuid", "00000000-0000-0000-0000-000000000000"),
            new XAttribute("user", ""),
            new XAttribute("versionDate", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.0000000Z")),
            new XAttribute("lastUpdate", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.0000000Z")),
            new XAttribute("checksum", new string('0', 32)),
            new XAttribute("fullyQualifiedName", args.Name),
            new XAttribute("moduleGuid", "00000000-0000-0000-0000-000000000000"),
            new XAttribute("guid", newAttrGuid),
            new XAttribute("name", args.Name),
            new XAttribute("description", args.Description ?? args.Name),
            properties);
    }

    private static void AddProperty(XElement properties, string name, string value)
    {
        properties.Add(new XElement("Property",
            new XElement("Name", name),
            new XElement("Value", value)));
    }

    private static XDocument NewExportFileSkeleton()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("ExportFile",
                new XElement("KMW",
                    new XElement("MajorVersion", "4"),
                    new XElement("MinorVersion", "0"),
                    new XElement("Build", "147395")),
                new XElement("Source",
                    new XAttribute("kb", "00000000-0000-0000-0000-000000000000"),
                    new XAttribute("username", "GxGenie"),
                    new XAttribute("UNCPath", ""),
                    new XElement("Version",
                        new XAttribute("guid", "00000000-0000-0000-0000-000000000000"),
                        new XAttribute("name", "GxGenie"))),
                new XElement("Objects"),
                new XElement("Dependencies")));
    }

    private static void EnsureAttributeReference(XDocument doc)
    {
        var deps = doc.Root!.Element("Dependencies");
        if (deps is null)
        {
            deps = new XElement("Dependencies");
            doc.Root.Add(deps);
        }
        var attrTypeGuid = "adbb33c9-0906-4971-833c-998de27e0676";
        var already = deps.Elements("Reference").Any(r =>
            string.Equals(r.Attribute("Type")?.Value, "Object", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Attribute("Id")?.Value, attrTypeGuid, StringComparison.OrdinalIgnoreCase));
        if (!already)
        {
            deps.Add(new XElement("Reference",
                new XAttribute("Package", "3ea7e1c6-b849-4df9-931a-070171a8a2f0"),
                new XAttribute("Type", "Object"),
                new XAttribute("Id", attrTypeGuid),
                new XElement("Properties",
                    new XAttribute("Name", "Attribute"),
                    new XAttribute("PackageName", "GenexusBL"))));
        }
    }

    private static void WriteXpz(string xpzPath, string innerName, XDocument doc)
    {
        WriteXpzWithDoc(xpzPath, innerName + ".xml", doc);
    }

    // -------- gx_remove_attribute --------

    public sealed record RemoveAttributeArgs(string Transaction, string Name, string? Level);

    /// <summary>
    /// Desasocia un atributo de una Transaction. El atributo queda standalone en la KB —
    /// para borrarlo de la KB completa, llamar después a gx_delete_object con Attribute:Name.
    /// Pipeline: export Transaction → quitar <c>&lt;Attribute&gt;</c> reference del Level → import UpdatedAndNew.
    /// </summary>
    public object RemoveAttribute(RemoveAttributeArgs args)
    {
        EnsureWriteAllowed("gx_remove_attribute");
        if (string.IsNullOrWhiteSpace(args.Transaction)) throw new ArgumentException("'transaction' is required");
        if (string.IsNullOrWhiteSpace(args.Name)) throw new ArgumentException("'name' is required");

        var bk = _backup.Snapshot($"remove_attr_{SanitizeForFilename(args.Name)}_from_{SanitizeForFilename(args.Transaction)}");

        var tempDir = Path.Combine(Path.GetTempPath(), "gxmcp", "rmattr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var xpzPath = Path.Combine(tempDir, args.Transaction + ".xpz");

        // 1) Export the transaction
        var exportTask = new MsBuildRunner.TaskInvocation("Export", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["Objects"] = "Transaction:" + args.Transaction,
            ["IncludeChildren"] = "False",
        });
        var exportResult = _msb.RunInsideKb(new[] { exportTask }, readOnly: true);
        if (!exportResult.Success || !File.Exists(xpzPath))
            throw new InvalidOperationException($"Could not export Transaction '{args.Transaction}' (exit {exportResult.ExitCode}):\n{exportResult.StdOut}");

        // 2) Load the XPZ
        XDocument doc;
        string innerXmlName;
        using (var fs = File.OpenRead(xpzPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Exported xpz has no .xml entry");
            innerXmlName = entry.FullName;
            using var stream = entry.Open();
            doc = XDocument.Load(stream);
        }

        // 3) Find the Transaction → Structure Part → target <Attribute> reference
        var trnObj = doc.Descendants("Object").FirstOrDefault(o =>
            string.Equals(o.Attribute("type")?.Value, XpzPartMap.Type_Transaction, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Attribute("name")?.Value, args.Transaction, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Transaction '{args.Transaction}' not found in exported xpz");

        var structurePart = trnObj.Elements("Part")
            .FirstOrDefault(p => string.Equals(p.Attribute("type")?.Value, "264be5fb-1b28-4b25-a598-6ca900dd059f", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Transaction has no Structure Part");

        IEnumerable<XElement> candidateLevels = string.IsNullOrWhiteSpace(args.Level)
            ? structurePart.Descendants("Level")
            : structurePart.Descendants("Level").Where(l => string.Equals(l.Attribute("Name")?.Value, args.Level, StringComparison.OrdinalIgnoreCase));

        XElement? attrRef = null;
        XElement? parentLevel = null;
        foreach (var lvl in candidateLevels)
        {
            attrRef = lvl.Elements("Attribute").FirstOrDefault(a => string.Equals(a.Value?.Trim(), args.Name, StringComparison.OrdinalIgnoreCase));
            if (attrRef != null) { parentLevel = lvl; break; }
        }

        if (attrRef is null)
            throw new InvalidOperationException(
                $"Attribute '{args.Name}' not found in Transaction '{args.Transaction}'" +
                (string.IsNullOrWhiteSpace(args.Level) ? "" : $" Level '{args.Level}'") + ".");

        var wasKey = string.Equals(attrRef.Attribute("key")?.Value, "True", StringComparison.OrdinalIgnoreCase);
        var fromLevel = parentLevel?.Attribute("Name")?.Value;
        attrRef.Remove();

        if (wasKey)
            // Removing a key changes the table's PK — warn loudly. The import will still
            // process it but the BL may reject or cascade. Surface the fact in the audit log
            // and the response so callers can react.
            _audit.Write("WRITE", "gx_remove_attribute", args.Name, "WARN",
                $"removing KEY attribute from {args.Transaction}.{fromLevel} — table PK changes");

        // 4) Re-zip into the same path
        WriteXpzWithDoc(xpzPath, innerXmlName, doc);

        // 5) Import UpdatedAndNew
        var importTask = new MsBuildRunner.TaskInvocation("Import", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["ImportType"] = "UpdatedAndNew",
            ["AutomaticBackup"] = "False",
        });
        var importResult = _msb.RunInsideKb(new[] { importTask });
        if (!importResult.Success)
        {
            _audit.Write("WRITE", "gx_remove_attribute", args.Name, "FAILURE",
                $"exit={importResult.ExitCode} backup={bk.BackupPath} from={args.Transaction}.{fromLevel}");
            throw new InvalidOperationException(
                $"Remove attribute failed (exit {importResult.ExitCode}). Backup: {bk.BackupPath}\nXPZ kept at: {xpzPath}\n{importResult.StdOut}\n{importResult.StdErr}");
        }

        _audit.Write("WRITE", "gx_remove_attribute", args.Name, "SUCCESS",
            $"from={args.Transaction}.{fromLevel} was_key={wasKey} backup={bk.BackupPath}");

        return new
        {
            success = true,
            attribute = args.Name,
            transaction = args.Transaction,
            level = fromLevel,
            was_key = wasKey,
            kb_attribute_kept = true,
            backup_path = bk.BackupPath,
            log_tail = LastLines(importResult.StdOut, 10),
        };
    }

    // -------- gx_set_attribute_property --------

    public sealed record SetAttrPropertyArgs(string Name, string Property, string Value);

    /// <summary>
    /// Modifica una property de un atributo existente (Description, Length, Decimals,
    /// ATTCUSTOMTYPE, idBasedOn, AUTONUMBER, ControlType, etc.). Pipeline: export Attribute →
    /// modificar <c>&lt;Property&gt;</c> en el bag → import UpdatedAndNew.
    /// </summary>
    public object SetAttributeProperty(SetAttrPropertyArgs args)
    {
        EnsureWriteAllowed("gx_set_attribute_property");
        if (string.IsNullOrWhiteSpace(args.Name)) throw new ArgumentException("'name' is required");
        if (string.IsNullOrWhiteSpace(args.Property)) throw new ArgumentException("'property' is required");
        if (args.Value is null) throw new ArgumentException("'value' is required (use empty string to clear)");

        var bk = _backup.Snapshot($"set_attr_prop_{SanitizeForFilename(args.Name)}_{SanitizeForFilename(args.Property)}");

        var tempDir = Path.Combine(Path.GetTempPath(), "gxmcp", "setattrp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var xpzPath = Path.Combine(tempDir, args.Name + ".xpz");

        // 1) Export the Attribute
        var exportTask = new MsBuildRunner.TaskInvocation("Export", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["Objects"] = "Attribute:" + args.Name,
            ["IncludeChildren"] = "False",
        });
        var exportResult = _msb.RunInsideKb(new[] { exportTask }, readOnly: true);
        if (!exportResult.Success || !File.Exists(xpzPath))
            throw new InvalidOperationException($"Could not export Attribute '{args.Name}' (exit {exportResult.ExitCode}):\n{exportResult.StdOut}");

        // 2) Load and find the Attribute element (in <Attributes> section, not in Structure references)
        XDocument doc;
        string innerXmlName;
        using (var fs = File.OpenRead(xpzPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Exported xpz has no .xml entry");
            innerXmlName = entry.FullName;
            using var stream = entry.Open();
            doc = XDocument.Load(stream);
        }

        var attrEl = doc.Root!.Element("Attributes")?.Elements("Attribute")
            .FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, args.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Attribute '{args.Name}' not found in the <Attributes> section of the export. Probably this is a Domain or system attribute that cannot be modified through this tool.");

        // 3) Update or insert the property
        var propertiesEl = attrEl.Element("Properties") ?? throw new InvalidOperationException("Attribute has no <Properties> element");
        var oldValue = (string?)null;
        var existing = propertiesEl.Elements("Property")
            .FirstOrDefault(p => string.Equals(p.Element("Name")?.Value, args.Property, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var valEl = existing.Element("Value");
            oldValue = valEl?.Value;
            if (valEl != null) valEl.Value = args.Value;
            else existing.Add(new XElement("Value", args.Value));
        }
        else
        {
            AddProperty(propertiesEl, args.Property, args.Value);
        }

        // 4) Re-zip and import
        WriteXpzWithDoc(xpzPath, innerXmlName, doc);
        var importTask = new MsBuildRunner.TaskInvocation("Import", new Dictionary<string, string>
        {
            ["File"] = xpzPath,
            ["ImportType"] = "UpdatedAndNew",
            ["AutomaticBackup"] = "False",
        });
        var importResult = _msb.RunInsideKb(new[] { importTask });
        if (!importResult.Success)
        {
            _audit.Write("WRITE", "gx_set_attribute_property", args.Name, "FAILURE",
                $"property={args.Property} value={args.Value} backup={bk.BackupPath}");
            throw new InvalidOperationException(
                $"Set attribute property failed (exit {importResult.ExitCode}). Backup: {bk.BackupPath}\nXPZ kept at: {xpzPath}\n{importResult.StdOut}\n{importResult.StdErr}");
        }

        _audit.Write("WRITE", "gx_set_attribute_property", args.Name, "SUCCESS",
            $"property={args.Property} old={oldValue} new={args.Value} backup={bk.BackupPath}");

        return new
        {
            success = true,
            attribute = args.Name,
            property = args.Property,
            old_value = oldValue,
            new_value = args.Value,
            backup_path = bk.BackupPath,
            log_tail = LastLines(importResult.StdOut, 10),
        };
    }

    private static void WriteXpzWithDoc(string xpzPath, string innerName, XDocument doc)
    {
        if (File.Exists(xpzPath)) File.Delete(xpzPath);
        using var fs = new FileStream(xpzPath, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
        var entry = zip.CreateEntry(innerName, CompressionLevel.Optimal);
        using var ws = entry.Open();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };
        using var xw = XmlWriter.Create(ws, settings);
        doc.Save(xw);
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

        var partName = string.IsNullOrWhiteSpace(args.Part) ? DefaultPartFor(args.Type) : args.Part!.Trim();
        var partInfo = XpzPartMap.ResolvePart(args.Type, partName)
            ?? throw new ArgumentException(BuildPartNotKnownError(args.Type, partName));
        if (!partInfo.Editable)
            throw new ArgumentException(
                $"Part '{partName}' de tipo '{args.Type}' no es editable como texto plano (kind={partInfo.Kind}). " +
                $"Editables para '{args.Type}': {string.Join(", ", EditablePartsFor(args.Type))}.");
        var partGuid = partInfo.Guid;

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
                   $"Disponibles: {string.Join(", ", known.Keys)} " +
                   $"(editables: {string.Join(", ", EditablePartsFor(objectType))}).";
        return $"Tipo '{objectType}' no tiene parts registrados en XpzPartMap. " +
               $"Tipos conocidos: {string.Join(", ", XpzPartMap.KnownObjectTypes)}. " +
               "Para sumar uno, hacé un export real y agregá los GUIDs a XpzPartMap.cs.";
    }

    private static IEnumerable<string> EditablePartsFor(string objectType) =>
        XpzPartMap.KnownPartsFor(objectType).Where(kv => kv.Value.Editable).Select(kv => kv.Key);

    private static string DefaultPartFor(string objectType)
    {
        // Sensible defaults per object type — what a user most likely means when they don't specify a part.
        return objectType switch
        {
            "WebPanel" or "Transaction" => "events",
            _ => "source",
        };
    }

    // ------- gx_list_object_parts -------

    public sealed record ListPartsArgs(string Type);

    /// <summary>
    /// Devuelve los Parts conocidos para un tipo de objeto, con su GUID, editabilidad y kind.
    /// Útil para que el cliente MCP descubra qué Parts puede modificar antes de llamar a UpdateObjectCode.
    /// </summary>
    public object ListObjectParts(ListPartsArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Type))
            throw new ArgumentException("'type' is required");
        var parts = XpzPartMap.KnownPartsFor(args.Type);
        if (parts.Count == 0)
            throw new ArgumentException(
                $"Tipo '{args.Type}' no tiene parts registrados. Tipos conocidos: " +
                string.Join(", ", XpzPartMap.KnownObjectTypes));

        var items = parts.Select(kv => new
        {
            name      = kv.Key,
            guid      = kv.Value.Guid,
            editable  = kv.Value.Editable,
            kind      = kv.Value.Kind,
        }).ToArray();

        return new
        {
            type = args.Type,
            count = items.Length,
            editable_count = items.Count(i => i.editable),
            parts = items,
        };
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
