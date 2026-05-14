using Microsoft.Data.SqlClient;

namespace GxGenie.Worker;

/// <summary>
/// Encapsula las pocas variaciones de schema SQL que pueden existir entre versiones
/// de GeneXus para la persistencia en LocalDB. La mayoría del schema (Entity,
/// EntityVersion, EntityVersionComposition, MODEL, ATTRIBUTE, TRN_DSD) es estable
/// entre GX17 y GX18 — el adapter es un punto de extensión para los casos que sí
/// difieren (column-name typos corregidos, nuevas columnas, etc.).
///
/// Estado actual: GX17 está validado contra SampleKB y GxGenieTest. El adapter de
/// GX18 hereda el comportamiento de GX17 y debe validarse contra una KB GX18 real
/// (ver FASE4_NOTES.md, sección "Validación pendiente GX18").
/// </summary>
public interface IKbSchemaAdapter
{
    /// <summary>Versión de GeneXus que este adapter soporta (ej: "17", "17U1", "18").</summary>
    string GxVersion { get; }

    /// <summary>
    /// Lee la versión interna de la KB (campo numérico en KnowledgeBaseInformation).
    /// GX17 usa la columna con typo "KnowlegeBaseVersion"; este método encapsula la
    /// query para que cambios de columna en GX18 no rompan el código de lectura.
    /// </summary>
    int ReadKbVersion(SqlConnection conn);
}

/// <summary>Factory de adapters por número de versión de GeneXus.</summary>
public static class SchemaAdapters
{
    public static IKbSchemaAdapter For(string? gxVersion)
    {
        var v = (gxVersion ?? "17").Trim();
        if (v.StartsWith("18", StringComparison.OrdinalIgnoreCase))
            return new Gx18SchemaAdapter();
        return new Gx17SchemaAdapter();
    }
}

/// <summary>Schema adapter para GeneXus 17 (todas las revisiones U1..U11).</summary>
public sealed class Gx17SchemaAdapter : IKbSchemaAdapter
{
    public string GxVersion => "17";

    public int ReadKbVersion(SqlConnection conn)
    {
        using var cmd = new SqlCommand("SELECT KnowlegeBaseVersion FROM KnowledgeBaseInformation", conn);
        var v = cmd.ExecuteScalar();
        return v is int iv ? iv : 0;
    }
}

/// <summary>
/// Schema adapter para GeneXus 18. Por ahora hereda el comportamiento de GX17.
/// Validar contra una KB GX18 real antes de asumir paridad — particularmente
/// el nombre de la columna de versión (¿quizá GeneXus corrigió el typo?).
/// </summary>
public sealed class Gx18SchemaAdapter : IKbSchemaAdapter
{
    public string GxVersion => "18";

    public int ReadKbVersion(SqlConnection conn)
    {
        // Intenta ambos nombres: el typo histórico (GX17) y el nombre corregido
        // que GeneXus podría haber introducido en GX18. Lo que primero responda gana.
        const string sql = @"
SELECT TOP 1 c.name AS col
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.name = 'KnowledgeBaseInformation'
  AND c.name IN ('KnowlegeBaseVersion', 'KnowledgeBaseVersion')";
        string? col;
        using (var probe = new SqlCommand(sql, conn))
        {
            col = probe.ExecuteScalar() as string;
        }
        if (string.IsNullOrEmpty(col)) return 0;
        using var cmd = new SqlCommand($"SELECT [{col}] FROM KnowledgeBaseInformation", conn);
        var v = cmd.ExecuteScalar();
        return v is int iv ? iv : 0;
    }
}
