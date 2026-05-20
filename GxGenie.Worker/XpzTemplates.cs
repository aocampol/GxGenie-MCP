using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GxGenie.Worker;

/// <summary>
/// Generates minimal XPZ payloads suitable for <c>Import</c>. An XPZ is a single-entry ZIP
/// containing the export XML. The XML schema was reverse-engineered from a real export of
/// SampleKB (see probes/sample-procedure.xpz). Type GUIDs are stable across GeneXus 17/18.
/// </summary>
internal static class XpzTemplates
{
    // Object type GUIDs (from the sample export)
    private const string TypeProcedure   = "84a12160-f59b-4ad7-a683-ea4481ac23e9";
    private const string TypeTransaction = "1db606f2-af09-4cf9-a3b5-b481519d28f6";
    private const string TypeAttribute   = "adbb33c9-0906-4971-833c-998de27e0676";

    // Procedure part type GUIDs
    private const string PartProcedureSource = "528d1c06-a9c2-420d-bd35-21dca83f12ff";
    private const string PartRules           = "9b0a32a3-de6d-4be1-a4dd-1b85d3741534";
    private const string PartConditions      = "763f0d8b-d8ac-4db4-8dd4-de8979f2b5b9";
    private const string PartProperties      = "c414ed00-8cc4-4f44-8820-4baf93547173";

    // Transaction part type GUID + the GenexusBL package GUID used in <Dependencies>.
    private const string PartTransactionStructure = "264be5fb-1b28-4b25-a598-6ca900dd059f";
    private const string GenexusBlPackage         = "3ea7e1c6-b849-4df9-931a-070171a8a2f0";

    /// <summary>
    /// Build a complete .xpz file in memory and write it to <paramref name="xpzPath"/>.
    /// Returns the same path for convenience. The XPZ wraps a single XML document
    /// named after the procedure (matches the convention of real exports).
    /// </summary>
    public static string ProcedureXml(string name, string description, string source)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.0000000Z", CultureInfo.InvariantCulture);
        var guid = Guid.NewGuid().ToString().ToLowerInvariant();

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<ExportFile>\n");
        sb.Append("  <KMW>\n");
        sb.Append("    <MajorVersion>4</MajorVersion>\n");
        sb.Append("    <MinorVersion>0</MinorVersion>\n");
        sb.Append("    <Build>147395</Build>\n");
        sb.Append("  </KMW>\n");
        sb.Append("  <Source kb=\"00000000-0000-0000-0000-000000000000\" username=\"GxGenie\" UNCPath=\"\">\n");
        sb.Append("    <Version guid=\"00000000-0000-0000-0000-000000000000\" name=\"GxGenieGenerated\" />\n");
        sb.Append("  </Source>\n");
        sb.Append("  <Objects>\n");
        sb.Append("    <Object")
            .Append(" guid=\"").Append(guid).Append('"')
            .Append(" name=\"").Append(XmlEscape(name)).Append('"')
            .Append(" type=\"").Append(TypeProcedure).Append('"')
            .Append(" description=\"").Append(XmlEscape(description)).Append('"')
            .Append(" versionDate=\"").Append(now).Append('"')
            .Append(" lastUpdate=\"").Append(now).Append('"')
            .Append(" user=\"\"")
            .Append(" fullyQualifiedName=\"").Append(XmlEscape(name)).Append('"')
            .Append(">\n");

        // Source (procedure body)
        sb.Append("      <Part type=\"").Append(PartProcedureSource).Append("\"><Source><![CDATA[")
            .Append(CdataEscape(source))
            .Append("]]></Source></Part>\n");
        // Rules (empty)
        sb.Append("      <Part type=\"").Append(PartRules).Append("\"><Source><![CDATA[]]></Source></Part>\n");
        // Conditions (empty)
        sb.Append("      <Part type=\"").Append(PartConditions).Append("\"><Source><![CDATA[]]></Source></Part>\n");
        // Properties (empty)
        sb.Append("      <Part type=\"").Append(PartProperties).Append("\"><Properties /></Part>\n");

        sb.Append("    </Object>\n");
        sb.Append("  </Objects>\n");
        sb.Append("  <Attributes />\n");
        sb.Append("  <Dependencies />\n");
        sb.Append("  <ObjectsIdentityMapping />\n");
        sb.Append("</ExportFile>\n");

        return sb.ToString();
    }

    /// <summary>
    /// Build the export XML for a new Transaction: one root <c>&lt;Level&gt;</c> with a single
    /// key <c>&lt;Attribute&gt;</c>, the key Attribute definition in the parallel
    /// <c>&lt;Attributes&gt;</c> section, and the <c>&lt;Dependencies&gt;</c> the Import task expects.
    /// Mirrors the hand-crafted XPZ validated in probes/discovery/b2-roundtrip.ps1.
    /// <paramref name="keyLength"/> is null for the length-less types (Date/DateTime/Boolean).
    /// Numeric keys are emitted as AUTONUMBER — the GeneXus convention for a Transaction key.
    /// </summary>
    public static string TransactionXml(string name, string description, string keyName, string keyDataType, int? keyLength)
    {
        var now  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.0000000Z", CultureInfo.InvariantCulture);
        var trnGuid = Guid.NewGuid().ToString().ToLowerInvariant();
        var lvlGuid = Guid.NewGuid().ToString().ToLowerInvariant();
        var keyGuid = Guid.NewGuid().ToString().ToLowerInvariant();
        var isNumeric = string.Equals(keyDataType, "bas:Numeric", StringComparison.OrdinalIgnoreCase);
        const string zeroGuid = "00000000-0000-0000-0000-000000000000";
        var zeroChecksum = new string('0', 32);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<ExportFile>\n");
        sb.Append("  <KMW><MajorVersion>4</MajorVersion><MinorVersion>0</MinorVersion><Build>147395</Build></KMW>\n");
        sb.Append("  <Source kb=\"").Append(zeroGuid).Append("\" username=\"GxGenie\" UNCPath=\"\">\n");
        sb.Append("    <Version guid=\"").Append(zeroGuid).Append("\" name=\"GxGenieGenerated\" />\n");
        sb.Append("  </Source>\n");
        sb.Append("  <Objects>\n");
        sb.Append("    <Object parentGuid=\"").Append(zeroGuid).Append("\" user=\"\"")
            .Append(" versionDate=\"").Append(now).Append("\" lastUpdate=\"").Append(now).Append('"')
            .Append(" checksum=\"").Append(zeroChecksum).Append('"')
            .Append(" fullyQualifiedName=\"").Append(XmlEscape(name)).Append('"')
            .Append(" moduleGuid=\"").Append(zeroGuid).Append('"')
            .Append(" guid=\"").Append(trnGuid).Append('"')
            .Append(" name=\"").Append(XmlEscape(name)).Append('"')
            .Append(" type=\"").Append(TypeTransaction).Append('"')
            .Append(" description=\"").Append(XmlEscape(description)).Append("\">\n");

        // Structure Part: one root Level holding the key attribute reference.
        sb.Append("      <Part type=\"").Append(PartTransactionStructure).Append("\">\n");
        sb.Append("        <Level Name=\"").Append(XmlEscape(name)).Append('"')
            .Append(" Type=\"").Append(XmlEscape(name)).Append('"')
            .Append(" Description=\"").Append(XmlEscape(description)).Append('"')
            .Append(" Guid=\"").Append(lvlGuid).Append("\">\n");
        sb.Append("          <Properties/>\n");
        sb.Append("          <Attribute key=\"True\" guid=\"").Append(keyGuid).Append("\">")
            .Append(XmlEscape(keyName)).Append("</Attribute>\n");
        sb.Append("        </Level>\n");
        sb.Append("        <Properties><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>\n");
        sb.Append("      </Part>\n");

        sb.Append("      <Properties>\n");
        sb.Append("        <Property><Name>Name</Name><Value>").Append(XmlEscape(name)).Append("</Value></Property>\n");
        sb.Append("        <Property><Name>Description</Name><Value>").Append(XmlEscape(description)).Append("</Value></Property>\n");
        sb.Append("        <Property><Name>IsDefault</Name><Value>False</Value></Property>\n");
        sb.Append("      </Properties>\n");
        sb.Append("    </Object>\n");
        sb.Append("  </Objects>\n");

        // Parallel <Attributes> section: the key Attribute definition. Import OnlyNew
        // reuses an attribute that already exists by name (it is skipped, not overwritten).
        sb.Append("  <Attributes>\n");
        sb.Append("    <Attribute parentGuid=\"").Append(zeroGuid).Append("\" user=\"\"")
            .Append(" versionDate=\"").Append(now).Append("\" lastUpdate=\"").Append(now).Append('"')
            .Append(" checksum=\"").Append(zeroChecksum).Append('"')
            .Append(" fullyQualifiedName=\"").Append(XmlEscape(keyName)).Append('"')
            .Append(" moduleGuid=\"").Append(zeroGuid).Append('"')
            .Append(" guid=\"").Append(keyGuid).Append('"')
            .Append(" name=\"").Append(XmlEscape(keyName)).Append('"')
            .Append(" description=\"").Append(XmlEscape(keyName)).Append("\">\n");
        sb.Append("      <Properties>\n");
        sb.Append("        <Property><Name>Name</Name><Value>").Append(XmlEscape(keyName)).Append("</Value></Property>\n");
        sb.Append("        <Property><Name>Description</Name><Value>").Append(XmlEscape(keyName)).Append("</Value></Property>\n");
        sb.Append("        <Property><Name>ATTCUSTOMTYPE</Name><Value>").Append(keyDataType).Append("</Value></Property>\n");
        if (keyLength.HasValue)
        {
            sb.Append("        <Property><Name>Length</Name><Value>").Append(keyLength.Value).Append("</Value></Property>\n");
            sb.Append("        <Property><Name>AttMaxLen</Name><Value>").Append(keyLength.Value).Append("</Value></Property>\n");
        }
        if (isNumeric)
            sb.Append("        <Property><Name>AUTONUMBER</Name><Value>True</Value></Property>\n");
        sb.Append("        <Property><Name>IsDefault</Name><Value>False</Value></Property>\n");
        sb.Append("      </Properties>\n");
        sb.Append("    </Attribute>\n");
        sb.Append("  </Attributes>\n");

        sb.Append("  <Dependencies>\n");
        AppendReference(sb, "Object", TypeTransaction, "Transaction");
        AppendReference(sb, "Object", TypeAttribute, "Attribute");
        AppendReference(sb, "Part", PartTransactionStructure, "Structure");
        sb.Append("  </Dependencies>\n");
        sb.Append("</ExportFile>\n");

        return sb.ToString();
    }

    private static void AppendReference(StringBuilder sb, string refType, string id, string name)
    {
        sb.Append("    <Reference Package=\"").Append(GenexusBlPackage)
          .Append("\" Type=\"").Append(refType).Append("\" Id=\"").Append(id).Append("\">\n");
        sb.Append("      <Properties Name=\"").Append(name).Append("\" PackageName=\"GenexusBL\" />\n");
        sb.Append("    </Reference>\n");
    }

    /// <summary>Wrap the XML in an XPZ (zip) at the given path.</summary>
    public static void WriteXpz(string xpzPath, string innerFileName, string xmlContent)
    {
        var dir = Path.GetDirectoryName(xpzPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(xpzPath)) File.Delete(xpzPath);

        using var fs = new FileStream(xpzPath, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
        var entry = zip.CreateEntry(innerFileName, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(xmlContent);
    }

    private static string XmlEscape(string s) => string.IsNullOrEmpty(s) ? "" : s
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>CDATA can contain anything except the literal end-marker <c>]]&gt;</c>. Split it if present.</summary>
    private static string CdataEscape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("]]>", "]]]]><![CDATA[>");
}
