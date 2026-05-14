using System.Text;

namespace GxGenie.Worker;

/// <summary>
/// Append-only audit log for every write operation. One line per operation, pipe-delimited,
/// human-readable. Format:
/// <c>YYYY-MM-DD HH:MM:SS | LEVEL | tool | target | RESULT | extra</c>.
/// Failures are also recorded so blocked writes (no AllowWrite) stay traceable.
/// </summary>
public sealed class AuditLogger
{
    private readonly string _path;
    private readonly bool _enabled;
    private readonly object _gate = new();

    public AuditLogger(WorkerConfig config)
    {
        _enabled = config.AuditLogEnabled;
        _path = config.AuditLogPath;
        if (_enabled)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    public void Write(string level, string tool, string target, string result, string? extra = null)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {level,-7} | {tool,-22} | {target,-40} | {result,-7} | {extra ?? ""}";
        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
