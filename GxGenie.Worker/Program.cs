using System.Text;
using System.Text.Json;
using GxGenie.Worker;

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

Console.OutputEncoding = Encoding.UTF8;

string? configPath = ResolveConfigPath(args);
WorkerConfig config;
try
{
    config = WorkerConfig.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load config: {ex.Message}");
    return 2;
}

var session = new WorkerSession(config);

if (args.Any(a => a == "--help" || a == "-h"))
{
    PrintUsage();
    return 0;
}

// --once '{"tool":"gx_kb_info"}'
int onceIdx = Array.IndexOf(args, "--once");
if (onceIdx >= 0 && onceIdx + 1 < args.Length)
{
    var resp = Execute(args[onceIdx + 1]);
    Console.WriteLine(JsonSerializer.Serialize(resp, jsonOpts));
    return resp.Success ? 0 : 1;
}

// --tool gx_list_objects --params '{"type":"Transaction"}'
int toolIdx = Array.IndexOf(args, "--tool");
if (toolIdx >= 0 && toolIdx + 1 < args.Length)
{
    var tool = args[toolIdx + 1];
    string paramsJson = "{}";
    int pIdx = Array.IndexOf(args, "--params");
    if (pIdx >= 0 && pIdx + 1 < args.Length) paramsJson = args[pIdx + 1];
    var resp = Execute("{\"tool\":\"" + tool + "\",\"params\":" + paramsJson + "}");
    Console.WriteLine(JsonSerializer.Serialize(resp, jsonOpts));
    return resp.Success ? 0 : 1;
}

// Default: line-based stdio loop. One JSON request per line, one JSON
// response per line. Used by the Gateway via IPC.
string? line;
while ((line = Console.In.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var resp = Execute(line);
    Console.WriteLine(JsonSerializer.Serialize(resp, jsonOpts));
    Console.Out.Flush();
}
return 0;

WorkerResponse Execute(string requestJson)
{
    WorkerRequest? req;
    try
    {
        req = JsonSerializer.Deserialize<WorkerRequest>(requestJson, jsonOpts);
    }
    catch (Exception ex)
    {
        return WorkerResponse.Fail($"Invalid JSON request: {ex.Message}");
    }
    if (req is null || string.IsNullOrWhiteSpace(req.Tool))
        return WorkerResponse.Fail("Missing 'tool' in request");

    try
    {
        return Dispatch(req);
    }
    catch (Exception ex)
    {
        return WorkerResponse.Fail($"{ex.GetType().Name}: {ex.Message}", req.Id);
    }
}

WorkerResponse Dispatch(WorkerRequest req)
{
    var p = req.Params ?? new Dictionary<string, object?>();

    switch (req.Tool)
    {
        case "gx_kb_info":
        {
            var info = session.Repo.GetKbInfo(session.Config.KbPath);
            return WorkerResponse.Ok(new
            {
                kb_name = session.Config.ActiveKbName,
                gx_version = session.Schema.GxVersion,
                kb_path = info.KbPath,
                kb_version = info.KbVersion,
                models = info.Models,
                object_counts = info.ObjectCounts,
                total_entities = info.TotalEntities,
            }, req.Id);
        }
        case "gx_list_objects":
        {
            var typeFilter = GetString(p, "type");
            var nameFilter = GetString(p, "filter");
            var limit = GetInt(p, "limit", 1000);
            var data = session.Repo.ListObjects(typeFilter, nameFilter, limit);
            return WorkerResponse.Ok(new { count = data.Count, items = data }, req.Id);
        }
        case "gx_read_object":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var type = GetString(p, "type");
            var detail = session.Repo.ReadObject(name!, type);
            if (detail is null) return WorkerResponse.Fail($"Object not found: {name}");
            return WorkerResponse.Ok(detail, req.Id);
        }
        case "gx_search":
        {
            var query = GetString(p, "query");
            if (string.IsNullOrEmpty(query)) return WorkerResponse.Fail("Missing 'query'");
            var where = GetString(p, "search_in") ?? "name";
            var limit = GetInt(p, "limit", 200);
            var hits = session.Repo.Search(query!, where, limit);
            return WorkerResponse.Ok(new { count = hits.Count, hits }, req.Id);
        }
        case "gx_list_attributes":
        {
            var trn = GetString(p, "transaction");
            if (string.IsNullOrEmpty(trn)) return WorkerResponse.Fail("Missing 'transaction'");
            var attrs = session.Repo.ListAttributes(trn!);
            if (attrs is null) return WorkerResponse.Fail($"Transaction not found: {trn}");
            return WorkerResponse.Ok(new { count = attrs.Count, attributes = attrs }, req.Id);
        }
        case "gx_export_xpz":
        {
            var objs = GetStringArray(p, "objects");
            if (objs is null || objs.Length == 0) return WorkerResponse.Fail("Missing 'objects' (array of 'Type:Name' strings)");
            var output = GetString(p, "output_path");
            if (string.IsNullOrEmpty(output)) return WorkerResponse.Fail("Missing 'output_path'");
            var includeChildren = GetBool(p, "include_children", true);
            var data = session.Writes.ExportXpz(new WriteTools.ExportArgs(objs, output!, includeChildren));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_import_xpz":
        {
            var xpz = GetString(p, "xpz_path");
            if (string.IsNullOrEmpty(xpz)) return WorkerResponse.Fail("Missing 'xpz_path'");
            var importType = GetString(p, "import_type") ?? "OnlyNew";
            var preview = GetBool(p, "preview_mode", false);
            var backup = GetBool(p, "backup", true);
            var data = session.Writes.ImportXpz(new WriteTools.ImportArgs(xpz!, importType, preview, backup));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_build_object":
        {
            var name = GetString(p, "object_name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'object_name'");
            var force = GetBool(p, "force_rebuild", false);
            var data = session.Writes.BuildObject(new WriteTools.BuildArgs(name!, force));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_delete_object":
        {
            var objs = GetStringArray(p, "objects");
            if (objs is null || objs.Length == 0) return WorkerResponse.Fail("Missing 'objects' (array of 'Type:Name' strings)");
            var includeChildren = GetBool(p, "include_children", true);
            var data = session.Writes.DeleteObject(new WriteTools.DeleteArgs(objs, includeChildren));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_create_procedure":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var description = GetString(p, "description");
            var module = GetString(p, "module");
            var source = GetString(p, "source");
            var data = session.Writes.CreateProcedure(new WriteTools.CreateProcArgs(name!, description, module, source, null));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_create_transaction":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var args = new WriteTools.CreateTrnArgs(
                Name: name!,
                Description: GetString(p, "description"),
                KeyAttribute: GetString(p, "key_attribute"),
                KeyDataType: GetString(p, "key_data_type"),
                KeyLength: GetInt(p, "key_length", -1) >= 0 ? GetInt(p, "key_length", 0) : (int?)null,
                Module: GetString(p, "module"),
                Levels: ParseTrnLevels(p));
            var data = session.Writes.CreateTransaction(args);
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_update_object_code":
        {
            var type = GetString(p, "type");
            var name = GetString(p, "name");
            var newSource = GetString(p, "new_source");
            var part = GetString(p, "part");
            if (string.IsNullOrEmpty(type)) return WorkerResponse.Fail("Missing 'type' (ej: 'Procedure')");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            if (newSource is null) return WorkerResponse.Fail("Missing 'new_source'");
            var data = session.Writes.UpdateObjectCode(new WriteTools.UpdateCodeArgs(type!, name!, newSource, part));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_switch_kb":
        {
            var kbName = GetString(p, "kb_name");
            if (string.IsNullOrEmpty(kbName)) return WorkerResponse.Fail("Missing 'kb_name'");
            session.SwitchKb(kbName!);
            // Cargar KbInfo nuevo para confirmar al cliente que el switch funcionó.
            var info = session.Repo.GetKbInfo(session.Config.KbPath);
            return WorkerResponse.Ok(new
            {
                success = true,
                kb_name = session.Config.ActiveKbName,
                gx_version = session.Schema.GxVersion,
                kb_path = info.KbPath,
                objects_count = info.TotalEntities,
                available_kbs = session.Config.KnowledgeBases.Select(k => k.Name).ToArray(),
            }, req.Id);
        }
        case "gx_list_kbs":
        {
            var items = session.Config.KnowledgeBases.Select(k => new
            {
                name = k.Name,
                path = k.Path,
                gx_version = k.GeneXusVersion,
                active = string.Equals(k.Name, session.Config.ActiveKbName, StringComparison.OrdinalIgnoreCase),
            }).ToList();
            return WorkerResponse.Ok(new { count = items.Count, items, active = session.Config.ActiveKbName }, req.Id);
        }
        case "gx_list_object_parts":
        {
            var type = GetString(p, "type");
            if (string.IsNullOrEmpty(type)) return WorkerResponse.Fail("Missing 'type'");
            var data = session.Writes.ListObjectParts(new WriteTools.ListPartsArgs(type!));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_get_structure":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var type = GetString(p, "type");
            var data = session.Inspector.GetStructure(name!, type);
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_get_layout":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var type = GetString(p, "type");
            var data = session.Inspector.GetLayout(name!, type);
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_get_variables":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var type = GetString(p, "type");
            var data = session.Inspector.GetVariables(name!, type);
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_get_unused_variables":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var type = GetString(p, "type");
            var data = session.Inspector.GetUnusedVariables(name!, type);
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_remove_variable":
        {
            var obj = GetString(p, "object");
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(obj)) return WorkerResponse.Fail("Missing 'object' (Type:Name)");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var data = session.Writes.RemoveVariable(new WriteTools.RemoveVariableArgs(obj!, name!));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_add_attribute":
        {
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var args = new WriteTools.AddAttributeArgs(
                Name: name!,
                DataType: GetString(p, "data_type"),
                Length: GetInt(p, "length", -1) >= 0 ? GetInt(p, "length", 0) : (int?)null,
                Decimals: GetInt(p, "decimals", -1) >= 0 ? GetInt(p, "decimals", 0) : (int?)null,
                Description: GetString(p, "description"),
                BasedOnDomain: GetString(p, "based_on_domain"),
                Transaction: GetString(p, "transaction"),
                Level: GetString(p, "level"),
                IsKey: GetBool(p, "is_key", false),
                Autonumber: GetBool(p, "autonumber", false));
            var data = session.Writes.AddAttribute(args);
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_remove_attribute":
        {
            var trn = GetString(p, "transaction");
            var name = GetString(p, "name");
            if (string.IsNullOrEmpty(trn)) return WorkerResponse.Fail("Missing 'transaction'");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            var data = session.Writes.RemoveAttribute(new WriteTools.RemoveAttributeArgs(trn!, name!, GetString(p, "level")));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_set_attribute_property":
        {
            var name = GetString(p, "name");
            var prop = GetString(p, "property");
            var value = GetString(p, "value");
            if (string.IsNullOrEmpty(name)) return WorkerResponse.Fail("Missing 'name'");
            if (string.IsNullOrEmpty(prop)) return WorkerResponse.Fail("Missing 'property'");
            if (value is null) return WorkerResponse.Fail("Missing 'value'");
            var data = session.Writes.SetAttributeProperty(new WriteTools.SetAttrPropertyArgs(name!, prop!, value));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_set_control_property":
        {
            var obj = GetString(p, "object");
            var ctrl = GetString(p, "control_name");
            var prop = GetString(p, "property");
            var value = GetString(p, "value");
            if (string.IsNullOrEmpty(obj)) return WorkerResponse.Fail("Missing 'object' (Type:Name)");
            if (string.IsNullOrEmpty(ctrl)) return WorkerResponse.Fail("Missing 'control_name'");
            if (string.IsNullOrEmpty(prop)) return WorkerResponse.Fail("Missing 'property'");
            if (value is null) return WorkerResponse.Fail("Missing 'value'");
            var data = session.Writes.SetControlProperty(new WriteTools.SetControlPropertyArgs(obj!, ctrl!, prop!, value));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_remove_control":
        {
            var obj = GetString(p, "object");
            var ctrl = GetString(p, "control_name");
            if (string.IsNullOrEmpty(obj)) return WorkerResponse.Fail("Missing 'object' (Type:Name)");
            if (string.IsNullOrEmpty(ctrl)) return WorkerResponse.Fail("Missing 'control_name'");
            var data = session.Writes.RemoveControl(new WriteTools.RemoveControlArgs(obj!, ctrl!));
            return WorkerResponse.Ok(data, req.Id);
        }
        case "gx_add_control":
        {
            var obj = GetString(p, "object");
            var kind = GetString(p, "control_kind");
            if (string.IsNullOrEmpty(obj)) return WorkerResponse.Fail("Missing 'object' (Type:Name)");
            if (string.IsNullOrEmpty(kind)) return WorkerResponse.Fail("Missing 'control_kind'");
            // properties is an optional object/dictionary
            Dictionary<string, string>? properties = null;
            if (p.TryGetValue("properties", out var propsRaw) && propsRaw is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in je.EnumerateObject())
                    properties[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : prop.Value.ToString();
            }
            var args = new WriteTools.AddControlArgs(
                Object: obj!,
                ParentControlName: GetString(p, "parent_control_name"),
                ParentId: GetString(p, "parent_id"),
                ControlKind: kind!,
                Properties: properties);
            var data = session.Writes.AddControl(args);
            return WorkerResponse.Ok(data, req.Id);
        }
        default:
            return WorkerResponse.Fail($"Unknown tool: {req.Tool}", req.Id);
    }
}

static string? GetString(Dictionary<string, object?> p, string key)
{
    if (!p.TryGetValue(key, out var v) || v is null) return null;
    if (v is JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Null => null,
            _ => je.ToString(),
        };
    }
    return v.ToString();
}

static int GetInt(Dictionary<string, object?> p, string key, int fallback)
{
    if (!p.TryGetValue(key, out var v) || v is null) return fallback;
    if (v is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
    if (int.TryParse(v.ToString(), out var parsed)) return parsed;
    return fallback;
}

static bool GetBool(Dictionary<string, object?> p, string key, bool fallback)
{
    if (!p.TryGetValue(key, out var v) || v is null) return fallback;
    if (v is JsonElement je)
    {
        if (je.ValueKind == JsonValueKind.True) return true;
        if (je.ValueKind == JsonValueKind.False) return false;
        if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var b)) return b;
    }
    if (bool.TryParse(v.ToString(), out var parsed2)) return parsed2;
    return fallback;
}

static string[]? GetStringArray(Dictionary<string, object?> p, string key)
{
    if (!p.TryGetValue(key, out var v) || v is null) return null;
    if (v is JsonElement je)
    {
        if (je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray();
        if (je.ValueKind == JsonValueKind.String)
            return new[] { je.GetString() ?? "" };
    }
    return null;
}

// Parse the optional 'levels' tree for gx_create_transaction. Returns null when absent.
// Lenient by design — structural parsing only; WriteTools.NormalizeLevel does the validation.
static List<TrnLevelDef>? ParseTrnLevels(Dictionary<string, object?> p)
{
    if (!p.TryGetValue("levels", out var raw) || raw is null) return null;
    if (raw is not JsonElement je || je.ValueKind != JsonValueKind.Array) return null;
    return ParseTrnLevelArray(je);
}

static List<TrnLevelDef> ParseTrnLevelArray(JsonElement arr)
{
    var levels = new List<TrnLevelDef>();
    foreach (var el in arr.EnumerateArray())
    {
        if (el.ValueKind != JsonValueKind.Object) continue;
        var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? (n.GetString() ?? "") : "";

        var attrs = new List<TrnAttrDef>();
        if (el.TryGetProperty("attributes", out var ja) && ja.ValueKind == JsonValueKind.Array)
        {
            foreach (var ae in ja.EnumerateArray())
            {
                if (ae.ValueKind != JsonValueKind.Object) continue;
                var an = ae.TryGetProperty("name", out var x) && x.ValueKind == JsonValueKind.String
                    ? (x.GetString() ?? "") : "";
                string? adt = ae.TryGetProperty("data_type", out var dt) && dt.ValueKind == JsonValueKind.String
                    ? dt.GetString() : null;
                int? alen = ae.TryGetProperty("length", out var ln) && ln.ValueKind == JsonValueKind.Number
                            && ln.TryGetInt32(out var lv) ? lv : (int?)null;
                int? adec = ae.TryGetProperty("decimals", out var dc) && dc.ValueKind == JsonValueKind.Number
                            && dc.TryGetInt32(out var dv) ? dv : (int?)null;
                var akey = ae.TryGetProperty("is_key", out var k) &&
                    (k.ValueKind == JsonValueKind.True ||
                     (k.ValueKind == JsonValueKind.String && bool.TryParse(k.GetString(), out var kb) && kb));
                attrs.Add(new TrnAttrDef(an, adt, alen, adec, akey));
            }
        }

        var subs = el.TryGetProperty("levels", out var sl) && sl.ValueKind == JsonValueKind.Array
            ? ParseTrnLevelArray(sl)
            : new List<TrnLevelDef>();

        levels.Add(new TrnLevelDef(name, attrs, subs));
    }
    return levels;
}

static void PrintUsage()
{
    Console.WriteLine("GxGenie.Worker — GeneXus KB tools over JSON IPC");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  GxGenie.Worker.exe                                  Line-based stdio (one JSON request per line)");
    Console.WriteLine("  GxGenie.Worker.exe --once '<request-json>'          Execute a single request and exit");
    Console.WriteLine("  GxGenie.Worker.exe --tool <name> --params '<json>'  Convenience form for --once");
    Console.WriteLine("  GxGenie.Worker.exe --config <path>                  Override config.json location");
    Console.WriteLine();
    Console.WriteLine("Tools: gx_kb_info, gx_list_objects, gx_read_object, gx_search, gx_list_attributes,");
    Console.WriteLine("       gx_export_xpz, gx_import_xpz, gx_build_object, gx_delete_object, gx_create_procedure,");
    Console.WriteLine("       gx_update_object_code, gx_switch_kb, gx_list_kbs");
}

static string? ResolveConfigPath(string[] args)
{
    int idx = Array.IndexOf(args, "--config");
    if (idx >= 0 && idx + 1 < args.Length) return args[idx + 1];
    return null;
}
