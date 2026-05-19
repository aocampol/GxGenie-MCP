using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace GxGenie.Worker;

/// <summary>
/// Phase B1 read tools. Each method reads the decoded XML of a single Part of an
/// object via <see cref="KbRepository.ReadObject"/> and returns a JSON-friendly tree
/// that hides the raw XML quirks (case-insensitivity, GeneXus-specific escapes,
/// KIP vs GXML detection, AttCustomType decoding).
///
/// These are pure read operations — they hit SQL (via the repository) and CPU only.
/// No MSBuild, no backups, no audit log entries.
/// </summary>
public sealed class KbInspector
{
    private readonly KbRepository _repo;

    public KbInspector(KbRepository repo) { _repo = repo; }

    // ---------------- gx_get_structure ----------------

    /// <summary>
    /// Returns the Transaction/SDT/DataSelector structure as a JSON-friendly object:
    /// nested levels each with attributes and sub-levels. Attribute type/length data
    /// is enriched from the ATTRIBUTE table when available.
    /// </summary>
    public object GetStructure(string objectName, string? typeFilter)
    {
        var detail = _repo.ReadObject(objectName, typeFilter)
            ?? throw new ArgumentException($"Object not found: {objectName}");

        if (!detail.Parts.TryGetValue("structure", out var xml) || string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException(
                $"Object '{detail.Type}:{objectName}' has no 'structure' Part (or it is empty). " +
                $"Available parts: {string.Join(", ", detail.Parts.Keys)}");

        // The Structure Part as persisted in SQL has <Attribute Id="…" IsKey="True"> nodes
        // — note that this differs from the XPZ-exported variant which uses lower-case
        // 'key' and inlines the attribute name as text content. We parse the SQL flavour
        // (the XPZ flavour is not needed here — that's only relevant inside WriteTools).
        XElement root;
        try { root = XElement.Parse(StripNullBytes(xml!)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not parse structure XML for {objectName}: {ex.Message}");
        }

        // Index attribute metadata by attri_num (the value of <Attribute Id="...">) AND by name
        // as a fallback. SQL-persisted Structure references attributes by attri_num.
        var attrList = detail.Attributes ?? new List<AttributeInfo>();
        var attrByNum  = attrList.GroupBy(a => a.AttriNum).ToDictionary(g => g.Key, g => g.First());
        var attrByName = attrList.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var levels = new List<object>();
        // The XML root is the top-level Level itself (not a wrapper).
        if (string.Equals(root.Name.LocalName, "Level", StringComparison.OrdinalIgnoreCase))
            levels.Add(LevelToJson(root, attrByNum, attrByName, fallbackName: detail.Name));
        else
            foreach (var l in root.Elements().Where(e => string.Equals(e.Name.LocalName, "Level", StringComparison.OrdinalIgnoreCase)))
                levels.Add(LevelToJson(l, attrByNum, attrByName, fallbackName: detail.Name));

        return new
        {
            @object = $"{detail.Type}:{detail.Name}",
            type = detail.Type,
            levels = levels,
            attribute_count = attrList.Count,
        };
    }

    private static object LevelToJson(XElement level, Dictionary<int, AttributeInfo> attrByNum, Dictionary<string, AttributeInfo> attrByName, string? fallbackName)
    {
        var attrs = level.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "Attribute", StringComparison.OrdinalIgnoreCase))
            .Select(a =>
            {
                AttributeInfo? meta = null;
                var idStr = a.Attribute("Id")?.Value;
                if (int.TryParse(idStr, out var idNum) && attrByNum.TryGetValue(idNum, out var m))
                    meta = m;

                // Fallback: <Attribute>Name</Attribute> form (XPZ flavour).
                var inlineName = (a.Value ?? "").Trim();
                if (meta is null && !string.IsNullOrEmpty(inlineName))
                    attrByName.TryGetValue(inlineName, out meta);

                // Key: SQL persists as IsKey, XPZ uses 'key'. Accept both.
                var keyAttr = a.Attribute("IsKey") ?? a.Attribute("key");
                var key = string.Equals(keyAttr?.Value, "True", StringComparison.OrdinalIgnoreCase);

                return new
                {
                    name = meta?.Name ?? (string.IsNullOrEmpty(inlineName) ? null : inlineName),
                    key,
                    attri_num = idNum > 0 ? idNum : (int?)null,
                    guid = a.Attribute("guid")?.Value,
                    data_type = meta?.Type,
                    length = meta?.Length,
                    decimals = meta?.Decimals,
                    header = meta?.Header,
                    position = meta?.Position,
                };
            }).ToList();

        var subLevels = level.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "Level", StringComparison.OrdinalIgnoreCase))
            .Select(l => LevelToJson(l, attrByNum, attrByName, fallbackName: null))
            .ToList();

        return new
        {
            name        = level.Attribute("Name")?.Value ?? fallbackName,
            type_name   = level.Attribute("Type")?.Value,
            description = level.Attribute("Description")?.Value,
            guid        = level.Attribute("Guid")?.Value,
            level_id    = level.Attribute("Id")?.Value,
            attributes  = attrs,
            sub_levels  = subLevels,
        };
    }

    /// <summary>
    /// Some Parts persisted in SQL contain stray NULL bytes (0x00) when the underlying blob
    /// was padded — these are not valid in XML 1.0. Strip them before parsing.
    /// </summary>
    internal static string StripNullBytes(string s) =>
        s.IndexOf('\0') < 0 ? s : s.Replace("\0", "");

    // ---------------- gx_get_layout ----------------

    /// <summary>
    /// Returns the Web Form / layout of a WebPanel or Transaction as a JSON tree.
    /// Detects whether the underlying format is KIP (legacy HTML-like) or GXML
    /// (modern abstract layout) by inspecting the root element name.
    /// </summary>
    public object GetLayout(string objectName, string? typeFilter)
    {
        var detail = _repo.ReadObject(objectName, typeFilter)
            ?? throw new ArgumentException($"Object not found: {objectName}");

        if (!detail.Parts.TryGetValue("webform", out var xml) || string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException(
                $"Object '{detail.Type}:{objectName}' has no 'webform' Part (or it is empty). " +
                $"Available parts: {string.Join(", ", detail.Parts.Keys)}");

        XElement root;
        try { root = XElement.Parse(StripNullBytes(xml!)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not parse webform XML for {objectName}: {ex.Message}");
        }

        var format = DetectLayoutFormat(root);
        var version = root.Attribute("version")?.Value;

        var tree = NodeToJson(root);
        var controlNames = root.Descendants()
            .Select(e => (string?)(e.Attribute("controlName")?.Value ?? e.Attribute("ControlName")?.Value))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();

        return new
        {
            @object = $"{detail.Type}:{detail.Name}",
            format,
            version,
            control_count = controlNames.Count,
            control_names = controlNames,
            tree,
        };
    }

    internal static string DetectLayoutFormat(XElement root)
    {
        var name = root.Name.LocalName;
        if (string.Equals(name, "GxMultiForm", StringComparison.OrdinalIgnoreCase)) return "GXML";
        if (string.Equals(name, "body", StringComparison.OrdinalIgnoreCase)) return "KIP";
        if (string.Equals(name, "BODY", StringComparison.Ordinal)) return "KIP";
        return "unknown";
    }

    private static object NodeToJson(XElement el)
    {
        var attrs = new Dictionary<string, string>();
        foreach (var a in el.Attributes()) attrs[a.Name.LocalName] = a.Value;

        var childElements = el.Elements()
            .Select(NodeToJson)
            .Cast<object>()
            .ToList();

        string? text = null;
        if (childElements.Count == 0)
        {
            var t = el.Value;
            if (!string.IsNullOrWhiteSpace(t)) text = t;
        }

        return new
        {
            kind = el.Name.LocalName,
            attributes = attrs,
            text,
            children = childElements,
        };
    }

    // ---------------- gx_get_variables ----------------

    /// <summary>
    /// Returns the Variables Part of an object as a JSON list. Each variable carries
    /// id, name, description, data type (decoded from AttCustomType), length, decimals,
    /// based-on attribute reference, and the raw property bag for power users.
    /// </summary>
    public object GetVariables(string objectName, string? typeFilter)
    {
        var detail = _repo.ReadObject(objectName, typeFilter)
            ?? throw new ArgumentException($"Object not found: {objectName}");

        if (!detail.Parts.TryGetValue("variables", out var xml) || string.IsNullOrWhiteSpace(xml))
            return new
            {
                @object = $"{detail.Type}:{detail.Name}",
                count = 0,
                variables = Array.Empty<object>(),
            };

        XElement root;
        try { root = XElement.Parse(StripNullBytes(xml!)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not parse variables XML for {objectName}: {ex.Message}");
        }

        var items = root.Elements()
            .Where(e =>
                string.Equals(e.Name.LocalName, "Variable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name.LocalName, "StandardVariable", StringComparison.OrdinalIgnoreCase))
            .Select(VariableToJson)
            .ToList();

        return new
        {
            @object = $"{detail.Type}:{detail.Name}",
            count = items.Count,
            variables = items,
        };
    }

    private static object VariableToJson(XElement varEl)
    {
        var props = ReadProperties(varEl);
        var isStandard = string.Equals(varEl.Name.LocalName, "StandardVariable", StringComparison.OrdinalIgnoreCase);

        // Description is sometimes an attribute on the element, sometimes a Property.
        var description = varEl.Attribute("Description")?.Value
                          ?? props.GetValueOrDefault("Description");

        // AttCustomType is the source of truth for data type — it's an escaped XML blob.
        var attCustomXml = props.GetValueOrDefault("ATTCUSTOMTYPE");
        var (dataTypeCode, basedOnAttr, basedOnObjectType) = DecodeAttCustomType(attCustomXml);

        // idBasedOn is the legacy way to declare "Like" attributes.
        var idBasedOn = props.GetValueOrDefault("idBasedOn");

        int? lengthInt = TryInt(props.GetValueOrDefault("Length"));
        int? decimalsInt = TryInt(props.GetValueOrDefault("Decimals"));

        return new
        {
            id = TryInt(varEl.Attribute("Id")?.Value),
            name = varEl.Attribute("Name")?.Value ?? props.GetValueOrDefault("Name"),
            description = string.IsNullOrEmpty(description) ? null : description,
            is_standard = isStandard,
            data_type = dataTypeCode is null ? null : DataTypeName(dataTypeCode.Value),
            data_type_code = dataTypeCode,
            length = lengthInt,
            decimals = decimalsInt,
            based_on_attribute = basedOnAttr,
            based_on_object_type = basedOnObjectType,
            id_based_on = idBasedOn,
            all_properties = props,
        };
    }

    private static Dictionary<string, string> ReadProperties(XElement el)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var propsEl = el.Element("Properties");
        if (propsEl is null) return dict;
        foreach (var p in propsEl.Elements("Property"))
        {
            var n = p.Element("Name")?.Value;
            var v = p.Element("Value")?.Value ?? "";
            if (!string.IsNullOrEmpty(n)) dict[n] = v;
        }
        return dict;
    }

    private static readonly Regex AttCustomDataTypeRegex = new(@"<DataType>\s*(\d+)\s*</DataType>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttCustomBasedOnRegex  = new(@"<BasedOn[^>]*>\s*([^<]+)\s*</BasedOn>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Decodes the embedded ATTCUSTOMTYPE XML (which arrives doubly-escaped inside a
    /// Property/Value). Returns the data-type code and optional based-on info.
    /// </summary>
    private static (int? dataType, string? basedOnAttr, string? basedOnObjectType) DecodeAttCustomType(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, null, null);
        // The Property/Value stores XML that has already been entity-decoded once by XElement reader.
        // No further decoding needed — we can regex it directly.
        int? dt = null;
        var m = AttCustomDataTypeRegex.Match(raw);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var code)) dt = code;

        string? basedOn = null;
        var m2 = AttCustomBasedOnRegex.Match(raw);
        if (m2.Success) basedOn = m2.Groups[1].Value;

        return (dt, basedOn, null);
    }

    /// <summary>
    /// Maps GeneXus DataType codes (as they appear in ATTCUSTOMTYPE) to human names.
    /// Extends <see cref="KbTypeMap.AttributeTypeName"/> with codes seen in Variables
    /// but not Attributes (254 = SDT reference, 255 = External Object reference).
    /// </summary>
    private static string DataTypeName(int code) => code switch
    {
        4 => "Numeric",
        5 => "Character",
        6 => "Date",
        7 => "Bitmap",
        8 => "BLOB",
        12 => "DateTime",
        13 => "VarChar",
        14 => "Image",
        15 => "Boolean",
        254 => "SDT",
        255 => "ExternalObject",
        _ => $"Unknown({code})",
    };

    private static int? TryInt(string? s) => int.TryParse(s, out var n) ? n : null;

    // ---------------- gx_get_unused_variables ----------------

    /// <summary>
    /// Result of scanning a single variable inside an object. Shared between
    /// <see cref="GetUnusedVariables"/> (read tool) and <see cref="WriteTools"/>
    /// (pre-check before <c>gx_remove_variable</c>).
    /// </summary>
    public sealed class VariableScan
    {
        public string Name { get; set; } = "";
        public bool IsStandard { get; set; }
        public string? DataType { get; set; }
        public int? Length { get; set; }
        public int ReferenceCount { get; set; }
        public Dictionary<string, int> ReferencesByPart { get; set; } = new();
    }

    // Parts whose text we scan for &<Name> references. Each entry that exists on
    // the object contributes to the reference count.
    private static readonly string[] ScannableParts = { "events", "rules", "conditions", "source" };

    /// <summary>
    /// Reads the object's Variables Part and counts case-insensitive matches of
    /// <c>&amp;Name</c> in each of the scannable text Parts that the object has.
    /// Returns the loaded <see cref="ObjectDetail"/> alongside one
    /// <see cref="VariableScan"/> per <c>&lt;Variable&gt;</c>/<c>&lt;StandardVariable&gt;</c>.
    /// </summary>
    public (ObjectDetail Detail, List<VariableScan> Scans, List<string> ScannedParts) ScanVariableReferences(string objectName, string? typeFilter)
    {
        var detail = _repo.ReadObject(objectName, typeFilter)
            ?? throw new ArgumentException($"Object not found: {objectName}");

        var scans = new List<VariableScan>();
        var present = ScannableParts
            .Where(p => detail.Parts.TryGetValue(p, out var v) && !string.IsNullOrEmpty(v))
            .ToList();

        if (!detail.Parts.TryGetValue("variables", out var xml) || string.IsNullOrWhiteSpace(xml))
            return (detail, scans, present);

        XElement root;
        try { root = XElement.Parse(StripNullBytes(xml!)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not parse variables XML for {objectName}: {ex.Message}");
        }

        foreach (var el in root.Elements())
        {
            var isStd = string.Equals(el.Name.LocalName, "StandardVariable", StringComparison.OrdinalIgnoreCase);
            var isVar = string.Equals(el.Name.LocalName, "Variable", StringComparison.OrdinalIgnoreCase);
            if (!isStd && !isVar) continue;
            var name = el.Attribute("Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;

            var props = ReadProperties(el);
            var (dtCode, _, _) = DecodeAttCustomType(props.GetValueOrDefault("ATTCUSTOMTYPE"));
            int? len = TryInt(props.GetValueOrDefault("Length"));

            // Pattern: literal `&` + variable name + negative lookahead for word char.
            // Case-insensitive — GeneXus is case-insensitive for identifiers.
            // Caveat (documented in the tool description): this also counts occurrences
            // inside string literals and comments. False-positives on 'referenced' are
            // safer than false-positives on 'unused'.
            var rx = new Regex(@"&" + Regex.Escape(name!) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
            var byPart = new Dictionary<string, int>();
            int total = 0;
            foreach (var p in present)
            {
                var text = detail.Parts[p];
                if (string.IsNullOrEmpty(text)) continue;
                var c = rx.Matches(text!).Count;
                if (c > 0) byPart[p] = c;
                total += c;
            }

            scans.Add(new VariableScan
            {
                Name = name!,
                IsStandard = isStd,
                DataType = dtCode is null ? null : DataTypeName(dtCode.Value),
                Length = len,
                ReferenceCount = total,
                ReferencesByPart = byPart,
            });
        }

        return (detail, scans, present);
    }

    /// <summary>
    /// Detects variables declared in the Variables Part that are not referenced in any
    /// other Part of the same object (events / rules / conditions / source). Useful
    /// for cleanup. The reference scan is a pragmatic regex (<c>&amp;Name</c> + word
    /// boundary, case-insensitive) — it will count occurrences inside string literals
    /// and comments, so prefer it as a hint rather than a hard answer. Standard
    /// variables (Today, Time, Pgmname, …) are reported but never listed as
    /// candidates for deletion: they are part of the GeneXus runtime.
    /// </summary>
    public object GetUnusedVariables(string objectName, string? typeFilter)
    {
        var (detail, scans, scannedParts) = ScanVariableReferences(objectName, typeFilter);

        var candidates = scans
            .Where(s => !s.IsStandard && s.ReferenceCount == 0)
            .Select(s => new { name = s.Name, data_type = s.DataType, length = s.Length })
            .ToList();

        var standardUnused = scans
            .Where(s => s.IsStandard && s.ReferenceCount == 0)
            .Select(s => s.Name)
            .ToList();

        return new
        {
            @object = $"{detail.Type}:{detail.Name}",
            scanned_parts = scannedParts,
            total = scans.Count,
            candidates_count = candidates.Count,
            candidates,
            standard_unused = standardUnused,
            variables = scans.Select(s => new
            {
                name = s.Name,
                data_type = s.DataType,
                length = s.Length,
                referenced = s.ReferenceCount > 0,
                reference_count = s.ReferenceCount,
                is_standard = s.IsStandard,
                references_by_part = s.ReferencesByPart,
            }).ToList(),
        };
    }
}
