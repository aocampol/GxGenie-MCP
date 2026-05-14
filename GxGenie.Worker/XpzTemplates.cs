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
    private const string TypeProcedure = "84a12160-f59b-4ad7-a683-ea4481ac23e9";

    // Procedure part type GUIDs
    private const string PartProcedureSource = "528d1c06-a9c2-420d-bd35-21dca83f12ff";
    private const string PartRules           = "9b0a32a3-de6d-4be1-a4dd-1b85d3741534";
    private const string PartConditions      = "763f0d8b-d8ac-4db4-8dd4-de8979f2b5b9";
    private const string PartProperties      = "c414ed00-8cc4-4f44-8820-4baf93547173";

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
