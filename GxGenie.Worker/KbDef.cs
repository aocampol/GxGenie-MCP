namespace GxGenie.Worker;

/// <summary>
/// Definición de una Knowledge Base en config.json. En el formato multi-KB es uno
/// de varios; en el formato single-KB se construye una sola entrada implícita.
/// </summary>
public sealed class KbDef
{
    /// <summary>Identificador lógico que el usuario usa con gx_switch_kb.</summary>
    public string Name { get; set; } = "";

    /// <summary>Ruta al archivo .gxw de la KB.</summary>
    public string Path { get; set; } = "";

    /// <summary>Cadena de conexión a la LocalDB de la KB.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Versión de GeneXus que persiste esta KB ("17", "17U1", "18", ...).</summary>
    public string GeneXusVersion { get; set; } = "17";
}

/// <summary>Una instalación de GeneXus localizada en el sistema.</summary>
public sealed class GxInstall
{
    public string Version { get; set; } = "";
    public string InstallationPath { get; set; } = "";
    public string SdkPath { get; set; } = "";
    public string MsBuildPath { get; set; } = "";
}
