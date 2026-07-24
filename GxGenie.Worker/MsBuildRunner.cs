using System.Diagnostics;
using System.Text;

namespace GxGenie.Worker;

/// <summary>
/// Generates an ad-hoc <c>.msbuild</c> file that opens the KB, executes one or more
/// GeneXus MSBuild tasks (Export, Import, BuildOne, DeleteObject, …) and closes the KB,
/// then invokes MSBuild.exe to run it. The MSBuild tasks DLL is x86, so the .NET Framework
/// x86 MSBuild is the natural runner (configurable via <see cref="WorkerConfig.MsBuildPath"/>).
/// </summary>
public sealed class MsBuildRunner
{
    private readonly WorkerConfig _config;

    public MsBuildRunner(WorkerConfig config)
    {
        _config = config;
    }

    public sealed record TaskInvocation(string TaskName, IReadOnlyDictionary<string, string> Attributes);

    public sealed record RunResult(bool Success, int ExitCode, string StdOut, string StdErr, string ScriptPath);

    /// <summary>
    /// Run a sequence of tasks inside a single Open/Close. Each task is rendered as
    /// <c>&lt;TaskName Attr1="v1" .../&gt;</c>. Tasks run in order; if any fails MSBuild stops.
    /// </summary>
    public RunResult RunInsideKb(IReadOnlyList<TaskInvocation> tasks, bool readOnly = false)
    {
        EnsureMsBuildExists();
        EnsureGxInstall();
        if (string.IsNullOrEmpty(_config.KbDirectory))
            throw new InvalidOperationException("KnowledgeBase.Path is not configured.");

        var script = BuildScript(tasks, readOnly);
        var tempDir = Path.Combine(Path.GetTempPath(), "gxmcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "run.msbuild");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        return InvokeMsBuild(scriptPath);
    }

    /// <summary>Run a script that's already on disk (used for create-KB and other meta ops).</summary>
    public RunResult RunScript(string scriptPath)
    {
        EnsureMsBuildExists();
        return InvokeMsBuild(scriptPath);
    }

    private string BuildScript(IReadOnlyList<TaskInvocation> tasks, bool readOnly)
    {
        var targetsPath = FindGenexusTargets(_config.GxInstallationPath);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
        sb.AppendLine("<Project DefaultTargets=\"Run\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        sb.AppendLine($"  <PropertyGroup>");
        sb.AppendLine($"    <GXInstall>{XmlAttr(_config.GxInstallationPath)}</GXInstall>");
        sb.AppendLine($"    <KBPath>{XmlAttr(_config.KbDirectory)}</KBPath>");
        sb.AppendLine($"  </PropertyGroup>");
        sb.AppendLine($"  <Import Project=\"{XmlAttr(targetsPath)}\"/>");
        sb.AppendLine("  <Target Name=\"Run\">");
        // GX17U11's OpenKnowledgeBase task (Genexus.MsBuild.Tasks.dll bajo MERGEMOD\) no expone
        // el parámetro ReadOnly (MSB4064 si se lo pasamos, sea cual sea el valor) — a diferencia
        // de GX17U1. Lo omitimos para esa versión; el resto sigue como antes.
        if (SupportsOpenKbReadOnlyParam(_config.GxVersion))
        {
            sb.AppendLine($"    <OpenKnowledgeBase Directory=\"$(KBPath)\" ReadOnly=\"{(readOnly ? "True" : "False")}\"/>");
        }
        else
        {
            sb.AppendLine("    <OpenKnowledgeBase Directory=\"$(KBPath)\"/>");
        }
        foreach (var t in tasks)
        {
            sb.Append($"    <{t.TaskName}");
            foreach (var kv in t.Attributes)
            {
                if (kv.Value is null) continue;
                sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlAttr(kv.Value)).Append('"');
            }
            sb.AppendLine("/>");
        }
        sb.AppendLine("    <CloseKnowledgeBase Directory=\"$(KBPath)\"/>");
        sb.AppendLine("  </Target>");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private RunResult InvokeMsBuild(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.MsBuildPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("/nologo");
        psi.ArgumentList.Add("/verbosity:minimal");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        if (!p.Start())
            throw new InvalidOperationException($"Failed to start MSBuild: {_config.MsBuildPath}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();

        return new RunResult(p.ExitCode == 0, p.ExitCode, stdout.ToString(), stderr.ToString(), scriptPath);
    }

    private void EnsureMsBuildExists()
    {
        if (!File.Exists(_config.MsBuildPath))
            throw new FileNotFoundException($"MSBuild not found at {_config.MsBuildPath}. Configure GeneXus.MSBuildPath in config.json.");
    }

    private void EnsureGxInstall()
    {
        if (string.IsNullOrEmpty(_config.GxInstallationPath) || !Directory.Exists(_config.GxInstallationPath))
            throw new DirectoryNotFoundException($"GeneXus install dir not found: {_config.GxInstallationPath}");
        // Lanza si no encuentra el .targets en ninguna ubicación conocida.
        _ = FindGenexusTargets(_config.GxInstallationPath);
    }

    /// <summary>
    /// Indica si la tarea <c>OpenKnowledgeBase</c> de esta versión de GeneXus expone el
    /// parámetro <c>ReadOnly</c>. Confirmado ausente en GX17U11 (MSB4064); presente en GX17U1.
    /// Versiones desconocidas se tratan como compatibles (comportamiento previo a este fix).
    /// </summary>
    private static bool SupportsOpenKbReadOnlyParam(string? gxVersion) =>
        !string.Equals(gxVersion, "17U11", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Localiza <c>Genexus.Tasks.targets</c> dentro del install de GeneXus.
    /// En GX17U1 está en la raíz del install; en GX17U11 vive bajo <c>MERGEMOD\</c>;
    /// en GX18 puede haberse movido a otros subdirectorios (<c>MSBuild\</c>, <c>Tasks\</c>)
    /// — chequeamos varios candidatos.
    /// </summary>
    public static string FindGenexusTargets(string gxInstallPath)
    {
        var candidates = new[]
        {
            Path.Combine(gxInstallPath, "Genexus.Tasks.targets"),
            Path.Combine(gxInstallPath, "MSBuild", "Genexus.Tasks.targets"),
            Path.Combine(gxInstallPath, "Tasks", "Genexus.Tasks.targets"),
            Path.Combine(gxInstallPath, "Build", "Genexus.Tasks.targets"),
            Path.Combine(gxInstallPath, "MERGEMOD", "Genexus.Tasks.targets"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        throw new FileNotFoundException(
            $"Genexus.Tasks.targets not found in {gxInstallPath}. " +
            $"Looked in: {string.Join(", ", candidates)}");
    }

    /// <summary>
    /// Localiza <c>genexus.msbuild.tasks.dll</c> dentro del install de GeneXus.
    /// Útil para diagnósticos y para validar que el install está completo.
    /// </summary>
    public static string FindGenexusTasksDll(string gxInstallPath)
    {
        var candidates = new[]
        {
            Path.Combine(gxInstallPath, "genexus.msbuild.tasks.dll"),
            Path.Combine(gxInstallPath, "MSBuild", "genexus.msbuild.tasks.dll"),
            Path.Combine(gxInstallPath, "Tasks", "genexus.msbuild.tasks.dll"),
            Path.Combine(gxInstallPath, "MERGEMOD", "genexus.msbuild.tasks.dll"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        throw new FileNotFoundException(
            $"genexus.msbuild.tasks.dll not found in {gxInstallPath}. " +
            $"Looked in: {string.Join(", ", candidates)}");
    }

    private static string XmlAttr(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
