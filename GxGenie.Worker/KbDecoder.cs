using System.IO.Compression;
using System.Text;
using System.Xml;

namespace GxGenie.Worker;

/// <summary>
/// Decodes the binary EntityVersionData blobs that GeneXus stores for object
/// parts (Events, Rules, Procedure, Conditions, Variables, Structure, etc.).
///
/// Wire format (discovered empirically):
///   bytes  0..3   magic 0x01 0x02 0x03 0x04
///   bytes  4..6   flags (compression algo / version)
///   bytes  7..10  little-endian uint32, decompressed length hint
///   bytes 11..N   gzip stream
///
/// Decompressed payload is XML, usually a TokenDataList — each TokenData has
/// a Token (type code), a Word (literal text), and an Id (entity reference).
/// Source code is reconstructed by concatenating the Word values in order.
/// </summary>
public static class KbDecoder
{
    private static readonly byte[] GzipMagic = { 0x1F, 0x8B, 0x08 };

    public static string? DecompressToText(byte[]? blob)
    {
        if (blob is null || blob.Length < 16) return null;

        int gzStart = FindGzipStart(blob);
        if (gzStart < 0) return null;

        using var ms = new MemoryStream(blob, gzStart, blob.Length - gzStart);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int FindGzipStart(byte[] blob)
    {
        int limit = Math.Min(blob.Length - 3, 32);
        for (int i = 0; i < limit; i++)
        {
            if (blob[i] == GzipMagic[0] && blob[i + 1] == GzipMagic[1] && blob[i + 2] == GzipMagic[2])
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Reconstructs the textual source code from a TokenDataList XML payload.
    /// If the XML is not a TokenDataList, returns null and the raw XML should
    /// be reported instead.
    /// </summary>
    public static string? ReconstructSource(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        if (!xml.Contains("<TokenDataList", StringComparison.Ordinal)) return null;

        var sb = new StringBuilder();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = false,
        };

        try
        {
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.Name == "Word")
                {
                    if (xr.IsEmptyElement) continue;
                    if (xr.Read() && (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.SignificantWhitespace || xr.NodeType == XmlNodeType.Whitespace))
                    {
                        sb.Append(xr.Value);
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return sb.ToString();
    }

    /// <summary>
    /// One-shot helper: decompresses an EntityVersionData blob and reconstructs
    /// source code if the payload is a TokenDataList; otherwise returns the
    /// raw decompressed payload (typically XML).
    /// </summary>
    public static string? DecodePart(byte[]? blob)
    {
        var raw = DecompressToText(blob);
        if (raw is null) return null;
        return ReconstructSource(raw) ?? raw;
    }
}
