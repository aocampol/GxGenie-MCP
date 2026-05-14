namespace GxGenie.Worker;

/// <summary>
/// Mapa estático <c>(objectType → partName → partTypeGuid)</c> para resolver, al
/// modificar un XPZ exportado, qué <c>&lt;Part type="..."&gt;</c> tocar.
///
/// Los GUIDs son <b>estables</b> a través de KBs y (según FASE3_NOTES.md) también
/// entre GeneXus 17 y 18 — pero sólo conocemos empíricamente los de Procedure
/// (extraídos del export de SampleKB en <c>probes/sample-procedure.xpz</c>).
///
/// Para extender a más tipos: hacer un export de un objeto de prueba en el IDE,
/// abrir el XPZ, listar los <c>type</c> de cada <c>&lt;Part&gt;</c> y agregarlos aquí
/// con el alias lógico que corresponda.
/// </summary>
public static class XpzPartMap
{
    public static readonly Dictionary<string, Dictionary<string, string>> ByObjectType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Procedure"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["source"]     = "528d1c06-a9c2-420d-bd35-21dca83f12ff",
                ["rules"]      = "9b0a32a3-de6d-4be1-a4dd-1b85d3741534",
                ["conditions"] = "763f0d8b-d8ac-4db4-8dd4-de8979f2b5b9",
                // Variables / Help / Documentation están listados en FASE3_NOTES.md
                // pero no se exportan por default en un Procedure y no los validé.
            },
            // TODO Fase 3.5+: WebPanel (events, rules, conditions, variables, webform),
            //                 Transaction (rules, events, structure, web form, win form),
            //                 DataProvider (source), etc.
        };

    /// <summary>
    /// Resuelve el GUID de un Part dado <paramref name="objectType"/> y
    /// <paramref name="partName"/>. Devuelve <c>null</c> si la combinación no
    /// está registrada (caller debe lanzar con un mensaje útil).
    /// </summary>
    public static string? Resolve(string objectType, string partName)
    {
        if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(partName)) return null;
        if (!ByObjectType.TryGetValue(objectType, out var map)) return null;
        return map.TryGetValue(partName, out var guid) ? guid : null;
    }

    /// <summary>Lista los partNames registrados para un objectType. Vacío si el tipo no está en el mapa.</summary>
    public static IReadOnlyCollection<string> KnownPartsFor(string objectType) =>
        ByObjectType.TryGetValue(objectType, out var map)
            ? (IReadOnlyCollection<string>)map.Keys
            : Array.Empty<string>();

    /// <summary>Tipos de objeto con al menos un part registrado.</summary>
    public static IReadOnlyCollection<string> KnownObjectTypes => ByObjectType.Keys;
}
