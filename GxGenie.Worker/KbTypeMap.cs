namespace GxGenie.Worker;

/// <summary>
/// Maps between human-readable GeneXus object/attribute type names and the
/// numeric codes used in the KB SQL schema.
///
/// IMPORTANT: <c>EntityTypeId</c> values are **assigned per KB**, not global.
/// SampleKB has Procedure=33 but a fresh KB might have Procedure=31. The
/// canonical name list below is the contract exposed to MCP clients, but the
/// actual ID resolution happens at runtime via the <c>EntityType</c> table —
/// see <see cref="KbRepository"/>'s type-map lazy load.
/// </summary>
public static class KbTypeMap
{
    /// <summary>Top-level type names the MCP exposes. Maps to EntityType.EntityTypeName.</summary>
    public static readonly string[] TopLevelNames =
    {
        "Query", "BPDiagram", "DataProvider", "DataSelector", "DataView",
        "Domain", "ExternalObject", "Group", "Procedure", "Report",
        "SDT", "Table", "Theme", "Transaction", "Menu", "WebPanel",
        "WorkPanel", "Image",
    };

    /// <summary>
    /// Procedure-body / sub-part type names. These are children inside
    /// EntityVersionComposition and resolve to readable keys in the JSON response.
    /// Multiple part names can map to the same logical key (e.g. SDTStructure +
    /// Transaction Structure both become "structure").
    /// </summary>
    public static readonly Dictionary<string, string> PartNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Conditions", "conditions" },
        { "Documentation", "documentation" },
        { "Events", "events" },
        { "Help", "help" },
        { "DataProviderSource", "source" },
        { "Procedure", "source" },            // Procedure body
        { "Rules", "rules" },
        { "SDTStructure", "structure" },
        { "Structure", "structure" },         // Transaction structure
        { "Variables", "variables" },
        { "WinForm", "winform" },
        { "WebForm", "webform" },
        { "Xml", "xml" },
        { "GroupStructure", "structure" },
        { "IndexStructure", "structure" },
        { "LNGStructure", "structure" },
    };

    /// <summary>
    /// GeneXus attribute type codes (ATTRIBUTE.attri_type). Confirmed by
    /// scanning samples in the SampleKB KB (counts/lengths consistent with
    /// the GeneXus documentation for data types).
    /// </summary>
    public static string AttributeTypeName(short code) => code switch
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
        _ => $"Unknown({code})",
    };
}
