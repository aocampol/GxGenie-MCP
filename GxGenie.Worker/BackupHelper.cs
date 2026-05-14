using Microsoft.Data.SqlClient;

namespace GxGenie.Worker;

/// <summary>
/// Snapshots the KB's SQL Server LocalDB to a .bak file via <c>BACKUP DATABASE</c>.
/// A .bak captures the entire database state at a point in time, can be restored with
/// <c>RESTORE DATABASE … WITH REPLACE</c>, and doesn't require detaching the live DB
/// (so the IDE can keep the KB open if needed).
/// </summary>
public sealed class BackupHelper
{
    private readonly WorkerConfig _config;

    public BackupHelper(WorkerConfig config)
    {
        _config = config;
    }

    public sealed record Result(string BackupPath, string DatabaseName, long SizeBytes);

    /// <summary>
    /// Creates a timestamped .bak file under <c>{BackupRoot}/{kb-name}/{yyyyMMdd_HHmmss}/</c>.
    /// Returns the path so the caller can record it in the audit log and reference it on rollback.
    /// </summary>
    public Result Snapshot(string tag)
    {
        var dbName = ExtractDbName(_config.ConnectionString)
            ?? throw new InvalidOperationException("Could not parse Database= from connection string.");

        var kbFolderName = Path.GetFileName(_config.KbDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(kbFolderName)) kbFolderName = dbName;

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTag = SanitizeFileName(tag);
        var backupDir = Path.Combine(_config.BackupRoot, kbFolderName, stamp);
        Directory.CreateDirectory(backupDir);
        var bakPath = Path.Combine(backupDir, $"{dbName}__{safeTag}.bak");

        // The MSBuild CloseKnowledgeBase task detaches the DB from LocalDB on exit,
        // so a subsequent BACKUP DATABASE call would otherwise fail with "Cannot open
        // database … login failed". Reattach if necessary before any SQL operation.
        LocalDbAttacher.EnsureAttached(_config.ConnectionString, _config.KbDirectory);

        using var conn = new SqlConnection(_config.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"BACKUP DATABASE [{dbName}] TO DISK = @path WITH FORMAT, INIT, COMPRESSION, NAME = @name";
        cmd.Parameters.AddWithValue("@path", bakPath);
        cmd.Parameters.AddWithValue("@name", $"GxGenie backup {stamp} {safeTag}");

        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqlException ex) when (ex.Number == 1844 || ex.Message.Contains("COMPRESSION", StringComparison.OrdinalIgnoreCase))
        {
            // LocalDB Express edition doesn't support COMPRESSION. Retry without it.
            cmd.CommandText = $"BACKUP DATABASE [{dbName}] TO DISK = @path WITH FORMAT, INIT, NAME = @name";
            cmd.ExecuteNonQuery();
        }

        var size = new FileInfo(bakPath).Length;
        return new Result(bakPath, dbName, size);
    }

    private static string? ExtractDbName(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var v = b.InitialCatalog;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "snapshot";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrEmpty(safe) ? "snapshot" : safe;
    }
}
