using Microsoft.Data.SqlClient;

namespace GxGenie.Worker;

/// <summary>
/// Helper that re-attaches a KB's LocalDB database when MSBuild's
/// <c>CloseKnowledgeBase</c> task has detached it. GeneXus's Close task removes
/// the DB from the LocalDB instance on every close; if subsequent code uses a
/// raw SQL connection it fails with "Cannot open database … login failed".
/// </summary>
public static class LocalDbAttacher
{
    public static void EnsureAttached(string connectionString, string kbDirectory)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var dbName = b.InitialCatalog;
        if (string.IsNullOrEmpty(dbName)) return;

        var masterCs = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ToString();
        using var conn = new SqlConnection(masterCs);
        conn.Open();

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @n";
            check.Parameters.AddWithValue("@n", dbName);
            if ((int)check.ExecuteScalar() > 0) return;
        }

        var (mdf, ldf) = FindDataFiles(kbDirectory);
        if (mdf is null)
            throw new InvalidOperationException($"DB '{dbName}' not attached and could not locate .mdf in {kbDirectory}");

        using var attach = conn.CreateCommand();
        attach.CommandText = ldf is null
            ? $"CREATE DATABASE [{dbName}] ON (FILENAME = N'{mdf}') FOR ATTACH"
            : $"CREATE DATABASE [{dbName}] ON (FILENAME = N'{mdf}'), (FILENAME = N'{ldf}') FOR ATTACH";
        attach.ExecuteNonQuery();
    }

    private static (string? mdf, string? ldf) FindDataFiles(string kbDirectory)
    {
        var connFile = Path.Combine(kbDirectory, "knowledgebase.connection");
        if (File.Exists(connFile))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(connFile);
                var data = doc.Root?.Element("DataFile")?.Value;
                var log  = doc.Root?.Element("LogFile")?.Value;
                var dir  = doc.Root?.Element("Directory")?.Value ?? kbDirectory;
                if (!string.IsNullOrEmpty(data))
                {
                    var mdfPath = Path.IsPathRooted(data) ? data : Path.Combine(dir, data);
                    var ldfPath = string.IsNullOrEmpty(log) ? null : (Path.IsPathRooted(log!) ? log : Path.Combine(dir, log!));
                    return (mdfPath, ldfPath);
                }
            }
            catch { /* fall through to filesystem scan */ }
        }

        var mdfFiles = Directory.GetFiles(kbDirectory, "*.mdf");
        if (mdfFiles.Length == 0) return (null, null);
        var mdf = mdfFiles[0];
        var alt = Directory.GetFiles(kbDirectory, "*.ldf");
        var ldf = alt.Length > 0 ? alt[0] : null;
        return (mdf, ldf);
    }
}
