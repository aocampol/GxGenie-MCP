using System.Text.Json;
using System.Text.Json.Nodes;

namespace GxGenie.Gateway;

/// <summary>
/// MCP tool catalog. Each entry maps the public MCP tool name to the underlying Worker
/// tool plus the JSON Schema for its input. The MCP spec requires <c>inputSchema</c>;
/// <c>outputSchema</c> is optional and omitted (the Worker returns free-form data).
/// </summary>
public static class ToolSchemas
{
    public sealed class ToolDef
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public JsonNode InputSchema { get; init; } = new JsonObject();
        public string WorkerTool { get; init; } = "";
    }

    private static JsonObject Obj(params (string key, JsonNode? val)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v;
        return o;
    }

    private static JsonObject Prop(string type, string description, JsonNode? extra = null)
    {
        var o = new JsonObject { ["type"] = type, ["description"] = description };
        if (extra is JsonObject jo)
        {
            foreach (var kv in jo.ToArray())
            {
                jo.Remove(kv.Key);
                o[kv.Key] = kv.Value;
            }
        }
        return o;
    }

    private static JsonArray EnumOf(params string[] values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(v);
        return a;
    }

    // Object types that the Worker recognises (see KbTypeMap.TopLevel).
    private static readonly string[] ObjectTypes =
    {
        "All", "Transaction", "Procedure", "WebPanel", "DataProvider", "DataSelector",
        "DataView", "BusinessComponent", "SDT", "Domain", "Table", "Theme",
        "Query", "Report", "Menu", "WorkPanel", "Image", "ExternalObject", "Group", "BPDiagram"
    };

    public static readonly IReadOnlyList<ToolDef> All = new List<ToolDef>
    {
        new()
        {
            Name = "gx_kb_info",
            WorkerTool = "gx_kb_info",
            Description = "Información general de la Knowledge Base de GeneXus: nombre, versión, modelos y conteo de objetos por tipo.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", new JsonObject()),
                ("additionalProperties", false)
            ),
        },
        new()
        {
            Name = "gx_list_objects",
            WorkerTool = "gx_list_objects",
            Description = "Lista objetos de la KB filtrando por tipo y/o substring del nombre. Use type='All' para listar todos los tipos top-level.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("type", Prop("string", "Tipo de objeto a listar. 'All' devuelve todos los tipos top-level.", new JsonObject
                    {
                        ["enum"] = new JsonArray(ObjectTypes.Select(t => (JsonNode)t).ToArray()),
                        ["default"] = "All",
                    })),
                    ("filter", Prop("string", "Substring case-insensitive a buscar en el nombre del objeto (opcional).")),
                    ("limit", Prop("integer", "Máximo de filas a devolver (default 1000).", new JsonObject
                    {
                        ["minimum"] = 1,
                        ["maximum"] = 100000,
                        ["default"] = 1000,
                    }))
                )),
                ("additionalProperties", false)
            ),
        },
        new()
        {
            Name = "gx_read_object",
            WorkerTool = "gx_read_object",
            Description = "Devuelve la metadata y el código fuente decodificado de un objeto (todas sus parts: Events, Rules, Procedure body, Structure, Variables, etc.).",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre exacto del objeto (case-insensitive).")),
                    ("type", Prop("string", "Tipo del objeto. Opcional: si se omite, se busca en cualquier tipo.", new JsonObject
                    {
                        ["enum"] = new JsonArray(ObjectTypes.Where(t => t != "All").Select(t => (JsonNode)t).ToArray()),
                    }))
                )),
                ("required", new JsonArray("name")),
                ("additionalProperties", false)
            ),
        },
        new()
        {
            Name = "gx_search",
            WorkerTool = "gx_search",
            Description = "Busca objetos por nombre (rápido) o dentro del código fuente decodificado (lento en KBs grandes).",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("query", Prop("string", "Texto a buscar (substring case-insensitive).")),
                    ("search_in", Prop("string", "Dónde buscar: 'name' (default, rápido) o 'code' (escanea blobs source/rules/events).", new JsonObject
                    {
                        ["enum"] = EnumOf("name", "code"),
                        ["default"] = "name",
                    })),
                    ("limit", Prop("integer", "Máximo de hits a devolver.", new JsonObject
                    {
                        ["minimum"] = 1,
                        ["maximum"] = 5000,
                        ["default"] = 200,
                    }))
                )),
                ("required", new JsonArray("query")),
                ("additionalProperties", false)
            ),
        },
        new()
        {
            Name = "gx_list_attributes",
            WorkerTool = "gx_list_attributes",
            Description = "Lista los atributos de una Transaction con su tipo, longitud, decimales y flag de clave primaria.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("transaction", Prop("string", "Nombre de la Transaction."))
                )),
                ("required", new JsonArray("transaction")),
                ("additionalProperties", false)
            ),
        },

        // ----- Phase 3 write tools (require AllowWrite=true / AllowBuild=true in config) -----

        new()
        {
            Name = "gx_export_xpz",
            WorkerTool = "gx_export_xpz",
            Description = "Exporta uno o más objetos de la KB a un archivo XPZ. Usa MSBuild Export task. Read-only sobre la KB pero genera artefacto en disco.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("objects", Obj(
                        ("type", "array"),
                        ("description", "Objetos a exportar en formato 'Type:Name' (ej: 'Procedure:MiProc', 'Transaction:Cliente')."),
                        ("items", new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z]+:.+$" }),
                        ("minItems", 1)
                    )),
                    ("output_path", Prop("string", "Ruta absoluta del archivo .xpz a generar (se sobreescribe si existe).")),
                    ("include_children", Prop("boolean", "Incluir objetos dependientes hijos. Default: true.", new JsonObject { ["default"] = true }))
                )),
                ("required", new JsonArray("objects", "output_path")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_import_xpz",
            WorkerTool = "gx_import_xpz",
            Description = "Importa un archivo XPZ a la KB. Hace backup automático del LocalDB antes salvo en modo preview.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("xpz_path", Prop("string", "Ruta absoluta al archivo .xpz a importar.")),
                    ("import_type", Prop("string", "Estrategia de conflicto: 'OnlyNew', 'UpdatedAndNew', 'AllObjects' o 'OnlyImported'. Default OnlyNew.", new JsonObject
                    {
                        ["enum"] = EnumOf("OnlyNew", "UpdatedAndNew", "AllObjects", "OnlyImported"),
                        ["default"] = "OnlyNew",
                    })),
                    ("preview_mode", Prop("boolean", "Solo simular sin tocar la KB. Default: false.", new JsonObject { ["default"] = false })),
                    ("backup", Prop("boolean", "Snapshot SQL antes de importar. Default: true.", new JsonObject { ["default"] = true }))
                )),
                ("required", new JsonArray("xpz_path")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_build_object",
            WorkerTool = "gx_build_object",
            Description = "Especifica + genera (compila) un objeto de la KB usando MSBuild BuildOne. Requiere AllowBuild=true.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("object_name", Prop("string", "Nombre exacto del objeto a buildear.")),
                    ("force_rebuild", Prop("boolean", "Forzar reconstrucción completa. Default: false.", new JsonObject { ["default"] = false }))
                )),
                ("required", new JsonArray("object_name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_delete_object",
            WorkerTool = "gx_delete_object",
            Description = "Borra objetos de la KB. Hace backup automático antes. CUIDADO: irreversible salvo restore del .bak.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("objects", Obj(
                        ("type", "array"),
                        ("description", "Objetos a borrar en formato 'Type:Name'."),
                        ("items", new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z]+:.+$" }),
                        ("minItems", 1)
                    )),
                    ("include_children", Prop("boolean", "Borrar también hijos dependientes. Default: true.", new JsonObject { ["default"] = true }))
                )),
                ("required", new JsonArray("objects")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_create_procedure",
            WorkerTool = "gx_create_procedure",
            Description = "Crea un nuevo Procedure en la KB. Genera un XPZ mínimo internamente e invoca MSBuild Import. Hace backup automático antes.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre del procedure (regex: ^[A-Za-z][A-Za-z0-9_]{0,63}$).", new JsonObject
                    {
                        ["pattern"] = "^[A-Za-z][A-Za-z0-9_]{0,63}$",
                    })),
                    ("description", Prop("string", "Descripción del procedure (opcional, default = name).")),
                    ("module", Prop("string", "Module/Folder donde ubicar el procedure. Opcional — actualmente ignorado (queda en raíz).")),
                    ("source", Prop("string", "Código fuente del procedure (GeneXus). Si se omite se crea con un comentario placeholder."))
                )),
                ("required", new JsonArray("name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_update_object_code",
            WorkerTool = "gx_update_object_code",
            Description = "Actualiza el código/contenido de un Part de un objeto existente. Tipos editables: Procedure (source/rules/conditions/documentation), DataProvider (source/rules/documentation), WebPanel (events/rules/conditions/webform/documentation), Transaction (events/rules/webform/documentation), Domain (documentation), DataSelector/SDT/Query/DataView/Module/Image/Theme/ExternalObject/Category (documentation). Parts estructurados (Variables, Help, Structure, etc) NO son editables — usa gx_list_object_parts para ver el catálogo. Pipeline: backup SQL → Export → modificar XPZ → Import UpdatedAndNew. Restaurable desde el .bak si algo falla.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("type", Prop("string", "Tipo de objeto.", new JsonObject
                    {
                        ["enum"] = EnumOf("Procedure", "DataProvider", "WebPanel", "Transaction", "Domain",
                                          "DataSelector", "DataView", "SDT", "Query", "Module", "Image",
                                          "Theme", "WebTheme", "ExternalObject", "Category"),
                    })),
                    ("name", Prop("string", "Nombre exacto del objeto a actualizar (case-insensitive).")),
                    ("new_source", Prop("string", "Nuevo código/contenido completo del Part. Reemplaza el contenido existente — no es un patch incremental.")),
                    ("part", Prop("string", "Qué Part actualizar. Si se omite: 'events' para WebPanel/Transaction, 'source' para los demás. Llamá a gx_list_object_parts para ver los disponibles por tipo.", new JsonObject
                    {
                        ["enum"] = EnumOf("source", "rules", "events", "conditions", "webform", "documentation"),
                    }))
                )),
                ("required", new JsonArray("type", "name", "new_source")),
                ("additionalProperties", false)
            ),
        },

        // ----- Phase B1: structured read tools -----

        new()
        {
            Name = "gx_get_structure",
            WorkerTool = "gx_get_structure",
            Description = "Devuelve la estructura de una Transaction/SDT/DataSelector como JSON: niveles anidados con sus atributos (nombre, tipo de dato, longitud, decimales, isKey, header). Cada nivel puede contener sub_levels recursivos. Más amigable que leer el XML crudo de gx_read_object.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre exacto del objeto.")),
                    ("type", Prop("string", "Tipo del objeto. Opcional.", new JsonObject
                    {
                        ["enum"] = EnumOf("Transaction", "SDT", "DataSelector", "DataView", "Table"),
                    }))
                )),
                ("required", new JsonArray("name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_get_layout",
            WorkerTool = "gx_get_layout",
            Description = "Devuelve el Web Form (layout) de un WebPanel o Transaction como árbol JSON. Detecta el formato: 'GXML' (moderno, GX17 U11+ / GX18) o 'KIP' (legacy HTML-like). Incluye lista de control_names y el tree completo con cada elemento como {kind, attributes, text, children}.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre exacto del objeto.")),
                    ("type", Prop("string", "Tipo del objeto. Opcional.", new JsonObject
                    {
                        ["enum"] = EnumOf("WebPanel", "Transaction"),
                    }))
                )),
                ("required", new JsonArray("name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_get_variables",
            WorkerTool = "gx_get_variables",
            Description = "Devuelve las variables (Variables Part) de un objeto como lista JSON. Cada variable trae id, name, description, data_type decodificado (de AttCustomType), length, decimals, based_on, is_standard, y all_properties con el bag de propiedades crudo.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre exacto del objeto.")),
                    ("type", Prop("string", "Tipo del objeto. Opcional.", new JsonObject
                    {
                        ["enum"] = EnumOf("Procedure", "DataProvider", "WebPanel", "Transaction"),
                    }))
                )),
                ("required", new JsonArray("name")),
                ("additionalProperties", false)
            ),
        },

        // ----- Phase B2: granular writes on Structure -----

        new()
        {
            Name = "gx_add_attribute",
            WorkerTool = "gx_add_attribute",
            Description = "Crea un atributo nuevo en la KB y opcionalmente lo asocia a una Transaction existente (root level o sub-level). Especificar exactamente uno: 'data_type' (con length/decimals propios) o 'based_on_domain' (toma el tipo del Domain). Pipeline: export Transaction + modificar Structure XML + agregar Attribute → import UpdatedAndNew. Backup SQL automático antes — restaurable si falla.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre del atributo (regex ^[A-Za-z][A-Za-z0-9_]{0,63}$).", new JsonObject
                    {
                        ["pattern"] = "^[A-Za-z][A-Za-z0-9_]{0,63}$",
                    })),
                    ("data_type", Prop("string", "Tipo GeneXus. Mutuamente exclusivo con based_on_domain.", new JsonObject
                    {
                        ["enum"] = EnumOf("bas:Numeric", "bas:Character", "bas:VarChar", "bas:Date", "bas:DateTime", "bas:Boolean", "bas:BLOB", "bas:Image"),
                    })),
                    ("length", Prop("integer", "Longitud (requerido para Numeric/Character/VarChar). Ignorado para Date/DateTime/Boolean.", new JsonObject
                    {
                        ["minimum"] = 1,
                        ["maximum"] = 4000,
                    })),
                    ("decimals", Prop("integer", "Decimales (para Numeric). Default 0.", new JsonObject
                    {
                        ["minimum"] = 0,
                        ["maximum"] = 20,
                    })),
                    ("description", Prop("string", "Descripción del atributo. Default = name.")),
                    ("based_on_domain", Prop("string", "Nombre del Domain del que hereda el tipo. Mutuamente exclusivo con data_type.")),
                    ("transaction", Prop("string", "Si se especifica, el atributo se agrega a la Structure de esa Transaction (UpdatedAndNew). Si se omite, el atributo se crea standalone (OnlyNew).")),
                    ("level", Prop("string", "Nombre del Level dentro de la Transaction donde insertar el atributo. Default = root level (la propia Transaction). Solo aplica si 'transaction' está presente.")),
                    ("is_key", Prop("boolean", "Si el atributo es parte de la clave del Level. Default: false.", new JsonObject { ["default"] = false })),
                    ("autonumber", Prop("boolean", "Si el atributo es autonumérico. Solo aplica a Numeric. Default: false.", new JsonObject { ["default"] = false }))
                )),
                ("required", new JsonArray("name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_remove_attribute",
            WorkerTool = "gx_remove_attribute",
            Description = "Desasocia un atributo de una Transaction (quita la referencia en el Level). El atributo permanece en la KB y puede usarse en otras Transactions; para borrarlo completamente llamar después a gx_delete_object con 'Attribute:Name'. Pipeline: export Transaction → quitar la <Attribute> reference → import UpdatedAndNew. ATENCIÓN: si el atributo es key del nivel se reporta en el audit log — el cambio de PK puede tener consecuencias en la BD.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("transaction", Prop("string", "Nombre de la Transaction.")),
                    ("name", Prop("string", "Nombre del atributo a desasociar.")),
                    ("level", Prop("string", "Nombre del Level específico donde quitar el atributo. Opcional: si se omite se busca en cualquier Level."))
                )),
                ("required", new JsonArray("transaction", "name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_set_attribute_property",
            WorkerTool = "gx_set_attribute_property",
            Description = "Modifica una property de un atributo existente. Properties típicamente editables: 'Description', 'Length', 'Decimals', 'ATTCUSTOMTYPE' (cambia tipo: bas:Numeric/bas:VarChar/bas:Date/etc.), 'idBasedOn' (cambia domain: 'Domain:Id'), 'AUTONUMBER', 'ControlType', 'ATT_PICTURE'. Pipeline: export Attribute → patch <Property> → import UpdatedAndNew. La BL de GeneXus valida la consistencia al importar.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("name", Prop("string", "Nombre del atributo a modificar.")),
                    ("property", Prop("string", "Nombre de la property a modificar (ej: 'Description', 'Length', 'Decimals', 'ATTCUSTOMTYPE', 'idBasedOn', 'AUTONUMBER').")),
                    ("value", Prop("string", "Nuevo valor de la property. Usar '' (string vacío) para limpiar."))
                )),
                ("required", new JsonArray("name", "property", "value")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_list_object_parts",
            WorkerTool = "gx_list_object_parts",
            Description = "Lista los Parts conocidos de un tipo de objeto GeneXus (Events, Rules, WebForm, Structure, Variables, etc.), indicando cuáles son editables via gx_update_object_code. Útil para descubrir qué se puede modificar antes de intentar el update.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("type", Prop("string", "Tipo de objeto GeneXus.", new JsonObject
                    {
                        ["enum"] = EnumOf("Procedure", "DataProvider", "WebPanel", "Transaction", "Domain",
                                          "DataSelector", "DataView", "SDT", "Query", "Table", "Module",
                                          "Image", "Theme", "WebTheme", "ExternalObject", "Group", "Category"),
                    }))
                )),
                ("required", new JsonArray("type")),
                ("additionalProperties", false)
            ),
        },

        // ----- Phase 4 multi-KB tools -----

        new()
        {
            Name = "gx_switch_kb",
            WorkerTool = "gx_switch_kb",
            Description = "Cambia la KB activa de la sesión MCP. El 'kb_name' debe coincidir con un Name definido en config.json (KnowledgeBases[]). Útil para trabajar con varias KBs sin reiniciar Claude Code.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", Obj(
                    ("kb_name", Prop("string", "Nombre lógico de la KB destino (definido en config.json KnowledgeBases[].Name)."))
                )),
                ("required", new JsonArray("kb_name")),
                ("additionalProperties", false)
            ),
        },

        new()
        {
            Name = "gx_list_kbs",
            WorkerTool = "gx_list_kbs",
            Description = "Lista las Knowledge Bases definidas en config.json e indica cuál está activa actualmente.",
            InputSchema = Obj(
                ("type", "object"),
                ("properties", new JsonObject()),
                ("additionalProperties", false)
            ),
        },
    };

    public static ToolDef? FindByName(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
