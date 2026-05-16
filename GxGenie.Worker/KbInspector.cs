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
    private static string StripNullBytes(string s) =>
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

    private static string DetectLayoutFormat(XElement root)
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
}
