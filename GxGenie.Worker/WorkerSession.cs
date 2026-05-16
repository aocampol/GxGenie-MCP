namespace GxGenie.Worker;

/// <summary>
/// Encapsula el estado por-KB del Worker (repositorio SQL + tools de escritura).
/// Vive durante toda la sesión MCP y permite cambiar de KB en caliente vía
/// <see cref="SwitchKb"/> sin reiniciar el proceso (lo que perdería la conexión
/// MCP del Gateway y el LocalDB attach).
/// </summary>
public sealed class WorkerSession
{
    public WorkerConfig Config { get; }
    public KbRepository Repo { get; private set; } = default!;
    public WriteTools Writes { get; private set; } = default!;
    public KbInspector Inspector { get; private set; } = default!;
    public IKbSchemaAdapter Schema { get; private set; } = default!;

    public WorkerSession(WorkerConfig config)
    {
        Config = config;
        Reload();
    }

    /// <summary>
    /// Cambia la KB activa por nombre. Reconstruye <see cref="Repo"/> y
    /// <see cref="Writes"/> con el nuevo connection string y adapter.
    /// </summary>
    public void SwitchKb(string kbName)
    {
        Config.SetActiveKb(kbName);
        Reload();
    }

    private void Reload()
    {
        Schema = SchemaAdapters.For(Config.GxVersion);
        Repo = new KbRepository(Config.ConnectionString, Config.KbDirectory, Schema);
        Writes = new WriteTools(Config, Repo);
        Inspector = new KbInspector(Repo);
    }
}
