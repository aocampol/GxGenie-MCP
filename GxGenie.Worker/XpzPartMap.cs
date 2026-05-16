namespace GxGenie.Worker;

/// <summary>
/// Describes a Part of a GeneXus object as it appears in an exported XPZ.
/// <list type="bullet">
///   <item><c>Guid</c> — the stable <c>&lt;Part type="..."&gt;</c> attribute value GeneXus emits in the XPZ XML.</item>
///   <item><c>Editable</c> — whether <see cref="WriteTools.UpdateObjectCode"/> can safely replace this Part's content
///     via <c>&lt;Source&gt;</c>/<c>&lt;InnerHtml&gt;</c> text substitution. <c>false</c> means the Part is structured
///     XML (Variables/Help/Structure/...) and requires a specialised editor that does not exist yet.</item>
///   <item><c>Kind</c> — diagnostic hint: <c>text</c> (plain GeneXus code), <c>xml</c> (KIP/GXML layout serialised as text),
///     <c>html</c> (InnerHtml), <c>structured</c> (XML tree), <c>metadata</c> (Properties only).</item>
/// </list>
/// </summary>
public sealed record PartInfo(string Guid, bool Editable, string Kind);

/// <summary>
/// Catálogo de Parts conocidos por GxGenie, mapeando <c>(objectType, partName)</c> → <see cref="PartInfo"/>.
/// El catálogo fue descubierto exportando un ejemplar de cada tipo de la KB SampleKB (GX17 U1) y leyendo la sección
/// <c>&lt;Dependencies&gt;</c> que el propio Export emite — es decir, los nombres canónicos vienen de GeneXus.
/// Detalle en <c>GxGenie.Worker/probes/discovery/parts-discovery-report.md</c>.
///
/// Los GUIDs son <b>estables</b> entre KBs y (per FASE3_NOTES.md) también entre GeneXus 17 y 18.
/// Si en GX18 se observa un GUID distinto para un mismo Part, sumar la entrada (sin remover la actual).
/// </summary>
public static class XpzPartMap
{
    // Object-type GUIDs (de la sección <Dependencies> Type="Object").
    public const string Type_Procedure      = "84a12160-f59b-4ad7-a683-ea4481ac23e9";
    public const string Type_DataProvider   = "2a9e9aba-d2de-4801-ae7f-5e3819222daf";
    public const string Type_WebPanel       = "c9584656-94b6-4ccd-890f-332d11fc2c25";
    public const string Type_Transaction    = "1db606f2-af09-4cf9-a3b5-b481519d28f6";
    public const string Type_Domain         = "00972a17-9975-449e-aab1-d26165d51393";
    public const string Type_DataSelector   = "ffd44be7-3bb4-4d01-9e7e-d1c1a3c095af";
    public const string Type_DataView       = "19abc6ff-2cd2-0000-0006-6d172bc2333b";
    public const string Type_Table          = "857ca50e-7905-0000-0007-c5d9ff2975ec";
    public const string Type_SDT            = "447527b5-9210-4523-898b-5dccb17be60a";
    public const string Type_Query          = "926a06b9-3417-4ab4-9f8c-09c2f626bb1c";
    public const string Type_Module         = "c88fffcd-b6f8-0000-8fec-00b5497e2117";
    public const string Type_Image          = "9fb193d9-64a4-4d30-b129-ff7c76830f7e";
    public const string Type_WebTheme       = "c804fdbd-7c0b-440d-8527-4316c92649a6";
    public const string Type_ExternalObject = "c163e562-42c6-4158-ad83-5b21a14cf30e";
    public const string Type_SubtypeGroup   = "87313f43-5eb2-41d7-9b8c-e8d9f5bf9588";
    public const string Type_Category       = "00000000-0000-0000-0000-000000000006";

    // Part GUIDs reused across multiple object types.
    private const string Part_Rules         = "9b0a32a3-de6d-4be1-a4dd-1b85d3741534";
    private const string Part_Conditions    = "763f0d8b-d8ac-4db4-8dd4-de8979f2b5b9";
    private const string Part_Variables     = "e4c4ade7-53f0-4a56-bdfd-843735b66f47";
    private const string Part_Help          = "ad3ca970-19d0-44e1-a7b7-db05556e820c";
    private const string Part_Documentation = "babf62c5-0111-49e9-a1c3-cc004d90900a";
    private const string Part_Events        = "c44bd5ff-f918-415b-98e6-aca44fed84fa";
    private const string Part_WebForm       = "d24a58ad-57ba-41b7-9e6e-eaca3543c778";

    /// <summary>
    /// Map <c>objectType → partName → PartInfo</c>. Lookups are case-insensitive on both keys.
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, PartInfo>> ByObjectType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Procedure"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["source"]        = new PartInfo("528d1c06-a9c2-420d-bd35-21dca83f12ff", true,  "text"),
                ["rules"]         = new PartInfo(Part_Rules,                              true,  "text"),
                ["conditions"]    = new PartInfo(Part_Conditions,                         true,  "text"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
                ["variables"]     = new PartInfo(Part_Variables,                          false, "structured"),
                ["help"]          = new PartInfo(Part_Help,                               false, "structured"),
                ["layout"]        = new PartInfo("c414ed00-8cc4-4f44-8820-4baf93547173",  false, "metadata"),
            },

            ["DataProvider"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["source"]        = new PartInfo("1d8aeb5a-6e98-45a7-92d2-d8de7384e432", true,  "text"),
                ["rules"]         = new PartInfo(Part_Rules,                              true,  "text"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
                ["variables"]     = new PartInfo(Part_Variables,                          false, "structured"),
                ["help"]          = new PartInfo(Part_Help,                               false, "structured"),
            },

            ["WebPanel"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["events"]        = new PartInfo(Part_Events,                             true,  "text"),
                ["rules"]         = new PartInfo(Part_Rules,                              true,  "text"),
                ["conditions"]    = new PartInfo(Part_Conditions,                         true,  "text"),
                // WebForm en GX17 KIP es XML/HTML literal; en GX17 U11+/GX18 puede ser GXML.
                // Editable como string pero el caller debe producir XML válido.
                ["webform"]       = new PartInfo(Part_WebForm,                            true,  "xml"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
                ["variables"]     = new PartInfo(Part_Variables,                          false, "structured"),
                ["help"]          = new PartInfo(Part_Help,                               false, "structured"),
            },

            ["Transaction"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["events"]        = new PartInfo(Part_Events,                             true,  "text"),
                ["rules"]         = new PartInfo(Part_Rules,                              true,  "text"),
                ["webform"]       = new PartInfo(Part_WebForm,                            true,  "xml"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
                ["structure"]     = new PartInfo("264be5fb-1b28-4b25-a598-6ca900dd059f", false, "structured"),
                ["winform"]       = new PartInfo("4c28dfb9-f83b-46f0-9cf3-f7e090b525d5", false, "metadata"),
                ["variables"]     = new PartInfo(Part_Variables,                          false, "structured"),
                ["help"]          = new PartInfo(Part_Help,                               false, "structured"),
            },

            ["Domain"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["documentation"] = new PartInfo(Part_Documentation, true,  "html"),
                ["help"]          = new PartInfo(Part_Help,          false, "structured"),
            },

            ["DataSelector"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["structure"]     = new PartInfo("a2bc65a1-999f-4e9b-b837-72285cc9bb16", false, "structured"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["DataView"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["structure"]     = new PartInfo("19abc6ff-2cd2-1000-0006-6d172bc2333b", false, "structured"),
                ["indexes"]       = new PartInfo("7706bd3b-212a-1000-0006-8aaeb59068b9", false, "structured"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["Table"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["structure"]     = new PartInfo("00000000-0000-0000-0002-000000000004", false, "structured"),
                ["indexes"]       = new PartInfo("a5c0e770-560d-0001-0001-7fe71c260de3", false, "structured"),
            },

            ["SDT"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["structure"]     = new PartInfo("5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a", false, "structured"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["Query"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["structure"]     = new PartInfo("447a71af-018d-4f65-9579-3f9216f7c854", false, "structured"),
                ["query-v3"]      = new PartInfo("025b1afc-982f-4bdb-8fa0-4c1712cb94fc", false, "metadata"),
                ["preview"]       = new PartInfo("09f1039f-ce72-48d4-953e-c1035be9111d", false, "metadata"),
                ["sql"]           = new PartInfo("24f60a53-4735-4f95-b03b-b8862c6bd27c", false, "metadata"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["Module"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["documentation"] = new PartInfo(Part_Documentation,                     true,  "html"),
                ["content"]       = new PartInfo("ed1b7b1c-2aaf-46eb-9ec5-db348f6fa3fc", false, "structured"),
            },

            ["Image"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["images"]        = new PartInfo("36f350de-f768-425f-ac20-773749f331bf", false, "structured"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["WebTheme"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["styles"]        = new PartInfo("c31007a6-01d3-4788-95b3-425921d47758", false, "structured"),
                ["font"]          = new PartInfo("43b86e51-163f-44af-ac5a-e101541b1a71", false, "metadata"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },
            // Alias para que el nombre comúnmente usado en gx_list_objects coincida.
            ["Theme"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["styles"]        = new PartInfo("c31007a6-01d3-4788-95b3-425921d47758", false, "structured"),
                ["font"]          = new PartInfo("43b86e51-163f-44af-ac5a-e101541b1a71", false, "metadata"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["ExternalObject"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["members"]       = new PartInfo("00000000-0000-0000-0002-000000000005", false, "structured"),
                ["documentation"] = new PartInfo(Part_Documentation,                      true,  "html"),
            },

            ["Group"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["structure"]     = new PartInfo("74203da2-41b1-402c-0001-d8d564a2c2fa", false, "structured"),
            },

            ["Category"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["documentation"] = new PartInfo(Part_Documentation, true, "html"),
            },
        };

    /// <summary>
    /// Devuelve sólo el GUID de un Part. Compat con código previo a la introducción de <see cref="PartInfo"/>.
    /// </summary>
    public static string? Resolve(string objectType, string partName) =>
        ResolvePart(objectType, partName)?.Guid;

    /// <summary>
    /// Devuelve la <see cref="PartInfo"/> de un Part (GUID + flag de editabilidad + kind).
    /// </summary>
    public static PartInfo? ResolvePart(string objectType, string partName)
    {
        if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(partName)) return null;
        if (!ByObjectType.TryGetValue(objectType, out var map)) return null;
        return map.TryGetValue(partName, out var pi) ? pi : null;
    }

    /// <summary>Lista los Parts registrados para un objectType, con su info. Diccionario vacío si el tipo no está registrado.</summary>
    public static IReadOnlyDictionary<string, PartInfo> KnownPartsFor(string objectType) =>
        ByObjectType.TryGetValue(objectType, out var map)
            ? (IReadOnlyDictionary<string, PartInfo>)map
            : new Dictionary<string, PartInfo>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tipos de objeto con al menos un Part registrado.</summary>
    public static IReadOnlyCollection<string> KnownObjectTypes => ByObjectType.Keys;
}
