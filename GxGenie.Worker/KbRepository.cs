using Microsoft.Data.SqlClient;

namespace GxGenie.Worker;

/// <summary>
/// SQL-backed read access to a GeneXus 17 KB (the LocalDB instance the KB
/// uses for persistence). Pure data layer — no business logic, no decoding
/// of source-code blobs; that lives in <see cref="KbDecoder"/>.
/// </summary>
/// <remarks>
/// The <c>EntityType</c> table assigns IDs **per KB**, not globally — SampleKB has
/// Procedure=33 while a freshly-created KB has Procedure=31. The type map is therefore
/// loaded lazily from the DB on first use and cached for the lifetime of the instance.
///
/// Version resolution: every catalog query resolves an object's *current* version
/// through <see cref="CurrentVersionJoin"/>, which reads <c>ModelEntityVersion</c>
/// (the design-model pointer) instead of trusting <c>Entity.EntityLastVersionId</c>.
/// The latter is stale (off by one) for any object nested inside a module, folder or
/// WorkWithPlus instance — which silently hid ~1100 objects from the catalog.
/// See <c>docs/MCP-BUG-REPORT-modulos.md</c>.
/// </remarks>
public sealed class KbRepository
{
    private readonly string _connectionString;
    private readonly string _kbDirectory;
    private readonly IKbSchemaAdapter _schema;
    private const int DesignModelId = 1;

    /// <summary>
    /// SQL JOIN fragment for queries anchored on <c>Entity e</c>. Resolves the current
    /// <c>EntityVersion</c> (alias <c>ev</c>) via the design-model pointer in
    /// <c>ModelEntityVersion</c> (alias <c>mev</c>), falling back to
    /// <c>Entity.EntityLastVersionId</c> for the few part entities with no
    /// <c>ModelEntityVersion</c> row. <c>ModelId = 1</c> is the Design model.
    /// </summary>
    private const string CurrentVersionJoin = @"
LEFT JOIN ModelEntityVersion mev
  ON mev.ModelId = 1
 AND mev.EntityTypeId = e.EntityTypeId
 AND mev.EntityId = e.EntityId
JOIN EntityVersion ev
  ON ev.EntityTypeId = e.EntityTypeId
 AND ev.EntityId = e.EntityId
 AND ev.EntityVersionId = COALESCE(mev.EntityVersionId, e.EntityLastVersionId)";

    /// <summary>Container EntityType names — traversed to compute an object's module path.</summary>
    private static readonly string[] ContainerTypeNames = { "Module", "Udm.Types.Folder", "WorkWithPlus" };

    private readonly object _typeMapGate = new();
    private Dictionary<string, int>? _topLevelByName;
    private Dictionary<int, string>? _topLevelById;
    private Dictionary<int, string>? _partTypeKeyById;
    private int _transactionTypeId = -1;
    private List<int>? _containerTypeIds;
    private int _moduleTypeId = -1;

    private readonly object _containerGate = new();
    private Dictionary<(int Type, int Id), ContainerNode>? _containerTree;

    /// <summary>A node in the module/folder/WorkWithPlus container tree.</summary>
    private sealed class ContainerNode
    {
        public int TypeId;
        public string Name = "";
        public int ParentTypeId;
        public int ParentId;
    }

    public KbRepository(string connectionString, string kbDirectory = "", IKbSchemaAdapter? schema = null)
    {
        _connectionString = connectionString;
        _kbDirectory = kbDirectory;
        _schema = schema ?? new Gx17SchemaAdapter();
    }

    public IKbSchemaAdapter Schema => _schema;

    private SqlConnection Open()
    {
        if (!string.IsNullOrEmpty(_kbDirectory))
            LocalDbAttacher.EnsureAttached(_connectionString, _kbDirectory);
        var c = new SqlConnection(_connectionString);
        c.Open();
        return c;
    }

    /// <summary>
    /// Build (or return cached) maps of TypeName↔TypeId for top-level objects and
    /// the readable JSON key for each part type. Uses MIN(EntityTypeId) per name
    /// to pick the top-level entry when a name is reused (e.g. "Procedure" appears
    /// twice: top-level object and Procedure-body part — the top-level one always
    /// has the lower ID).
    /// </summary>
    private void EnsureTypeMap(SqlConnection conn)
    {
        if (_topLevelByName is not null) return;
        lock (_typeMapGate)
        {
            if (_topLevelByName is not null) return;

            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<int, string>();
            var partKeyById = new Dictionary<int, string>();
            var containerIds = new List<int>();
            int trnId = -1;
            int moduleId = -1;

            using var cmd = new SqlCommand(
                "SELECT EntityTypeId, EntityTypeName FROM EntityType ORDER BY EntityTypeId", conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                string name = r.IsDBNull(1) ? "" : r.GetString(1);
                if (string.IsNullOrEmpty(name)) continue;

                if (KbTypeMap.TopLevelNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    // First occurrence (lowest id) wins → that's the top-level entry.
                    if (!byName.ContainsKey(name))
                    {
                        byName[name] = id;
                        byId[id] = name;
                        if (string.Equals(name, "Transaction", StringComparison.OrdinalIgnoreCase))
                            trnId = id;
                    }
                }

                if (KbTypeMap.PartNameAliases.TryGetValue(name, out var key))
                    partKeyById[id] = key;

                if (ContainerTypeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    containerIds.Add(id);
                    if (string.Equals(name, "Module", StringComparison.OrdinalIgnoreCase) && moduleId < 0)
                        moduleId = id;
                }
            }

            _topLevelByName = byName;
            _topLevelById = byId;
            _partTypeKeyById = partKeyById;
            _transactionTypeId = trnId;
            _containerTypeIds = containerIds;
            _moduleTypeId = moduleId;
        }
    }

    /// <summary>
    /// Builds (or returns cached) the module/folder/WorkWithPlus container tree from
    /// <c>ModelEntityVersion</c>. Each node records its name and its own parent so
    /// <see cref="ResolveModulePath"/> can walk an object's chain up to the root.
    /// </summary>
    private void EnsureContainerTree(SqlConnection conn)
    {
        if (_containerTree is not null) return;
        lock (_containerGate)
        {
            if (_containerTree is not null) return;

            var tree = new Dictionary<(int, int), ContainerNode>();
            if (_containerTypeIds is { Count: > 0 })
            {
                var csv = string.Join(",", _containerTypeIds);
                using var cmd = new SqlCommand($@"
SELECT EntityTypeId, EntityId, ModelEntityVersionName, ModelParentEntityTypeId, ModelParentEntityId
FROM ModelEntityVersion
WHERE ModelId = {DesignModelId} AND EntityTypeId IN ({csv})", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var node = new ContainerNode
                    {
                        TypeId = r.GetInt32(0),
                        Name = r.IsDBNull(2) ? "" : r.GetString(2),
                        ParentTypeId = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                        ParentId = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    };
                    tree[(node.TypeId, r.GetInt32(1))] = node;
                }
            }
            _containerTree = tree;
        }
    }

    /// <summary>
    /// Walks the container chain of an object upward and returns its dotted module
    /// path (e.g. <c>"LISAPI.V1"</c>), or null when the object lives directly in the
    /// root module. Folders and WorkWithPlus instances are traversed but not emitted —
    /// only GeneXus modules form the namespace.
    /// </summary>
    private string? ResolveModulePath(int parentTypeId, int parentId)
    {
        if (_containerTree is null || parentTypeId == 0 || _moduleTypeId < 0) return null;

        var modules = new List<string>();
        var cur = (parentTypeId, parentId);
        for (int guard = 0; guard < 32 && _containerTree.TryGetValue(cur, out var node); guard++)
        {
            // A module whose own parent is 0 is the root module — skip it (noise).
            if (node.TypeId == _moduleTypeId && node.ParentTypeId != 0)
                modules.Add(node.Name);
            cur = (node.ParentTypeId, node.ParentId);
        }
        if (modules.Count == 0) return null;
        modules.Reverse();
        return string.Join(".", modules);
    }

    private string TopLevelTypesCsv() => string.Join(",", _topLevelByName!.Values);

    private List<int> ResolveTypes(string? typeFilter)
    {
        if (_topLevelByName is null) throw new InvalidOperationException("Type map not loaded");
        if (string.IsNullOrWhiteSpace(typeFilter) || typeFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            return _topLevelByName.Values.ToList();

        var names = typeFilter.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<int>();
        foreach (var n in names)
        {
            if (_topLevelByName.TryGetValue(n, out var id)) ids.Add(id);
        }
        return ids;
    }

    // ---------- KB info ----------

    public KbInfo GetKbInfo(string? kbPath)
    {
        using var conn = Open();
        EnsureTypeMap(conn);

        var info = new KbInfo
        {
            KbName = conn.Database,
            KbPath = kbPath ?? "",
        };

        info.KbVersion = _schema.ReadKbVersion(conn);

        using (var cmd = new SqlCommand("SELECT model_id, model_name, BaseModel FROM MODEL ORDER BY model_id", conn))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                info.Models.Add(new ModelInfo
                {
                    Id = r.GetInt16(0),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    Type = r.IsDBNull(2) ? 0 : r.GetInt16(2),
                });
            }
        }

        const string countSql = @"
SELECT e.EntityTypeId, COUNT(*) AS cnt
FROM Entity e
WHERE e.EntityTypeId IN (SELECT value FROM STRING_SPLIT(@types, ','))
GROUP BY e.EntityTypeId";

        using (var cmd = new SqlCommand(countSql, conn))
        {
            cmd.Parameters.AddWithValue("@types", TopLevelTypesCsv());
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int typeId = r.GetInt32(0);
                int cnt = r.GetInt32(1);
                if (_topLevelById!.TryGetValue(typeId, out var name))
                    info.ObjectCounts[name] = cnt;
            }
        }

        using (var cmd = new SqlCommand("SELECT COUNT(*) FROM Entity", conn))
        {
            info.TotalEntities = (int)cmd.ExecuteScalar();
        }

        return info;
    }

    // ---------- List ----------

    public List<ObjectListItem> ListObjects(string? typeFilter, string? nameFilter, int limit)
    {
        using var conn = Open();
        EnsureTypeMap(conn);
        EnsureContainerTree(conn);
        var result = new List<ObjectListItem>();

        var wantedTypes = ResolveTypes(typeFilter);
        if (wantedTypes.Count == 0) return result;

        var typesCsv = string.Join(",", wantedTypes);
        var sql = @"
SELECT e.EntityTypeId, e.EntityId, ev.EntityVersionName, ev.EntityVersionDescription,
       mev.ModelParentEntityTypeId, mev.ModelParentEntityId
FROM Entity e" + CurrentVersionJoin + @"
WHERE e.EntityTypeId IN (SELECT CAST(value AS int) FROM STRING_SPLIT(@types, ','))
" + (string.IsNullOrEmpty(nameFilter)
       ? ""
       : " AND ev.EntityVersionName LIKE @name") + @"
ORDER BY ev.EntityVersionName";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@types", typesCsv);
        if (!string.IsNullOrEmpty(nameFilter))
            cmd.Parameters.AddWithValue("@name", ToLikePattern(nameFilter));

        using var r = cmd.ExecuteReader();
        int n = 0;
        while (r.Read() && n < limit)
        {
            int typeId = r.GetInt32(0);
            int parentType = r.IsDBNull(4) ? 0 : r.GetInt32(4);
            int parentId = r.IsDBNull(5) ? 0 : r.GetInt32(5);
            result.Add(new ObjectListItem
            {
                Id = r.GetInt32(1),
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                Type = _topLevelById!.GetValueOrDefault(typeId, $"Type{typeId}"),
                Description = r.IsDBNull(3) ? null : r.GetString(3),
                Module = ResolveModulePath(parentType, parentId),
            });
            n++;
        }
        return result;
    }

    // ---------- Read ----------

    public ObjectDetail? ReadObject(string name, string? type, string? module = null)
    {
        using var conn = Open();
        EnsureTypeMap(conn);
        EnsureContainerTree(conn);

        var (typeId, entityId, versionId, versionName, versionDesc, parentType, parentId) = FindEntity(conn, name, type, module);
        if (entityId < 0) return null;

        var detail = new ObjectDetail
        {
            Name = versionName,
            Type = _topLevelById!.GetValueOrDefault(typeId, $"Type{typeId}"),
            Description = versionDesc,
            Module = ResolveModulePath(parentType, parentId),
        };

        // Each part is itself an entity; resolve its current version through
        // ModelEntityVersion (see CurrentVersionJoin) so parts of objects nested
        // in modules/folders are not dropped by a stale EntityLastVersionId.
        const string partsSql = @"
SELECT c.ComponentEntityTypeId, c.ComponentEntityId, ev.EntityVersionData
FROM EntityVersionComposition c
LEFT JOIN ModelEntityVersion mev
  ON mev.ModelId = 1
 AND mev.EntityTypeId = c.ComponentEntityTypeId
 AND mev.EntityId = c.ComponentEntityId
LEFT JOIN Entity e
  ON e.EntityTypeId = c.ComponentEntityTypeId
 AND e.EntityId    = c.ComponentEntityId
JOIN EntityVersion ev
  ON ev.EntityTypeId = c.ComponentEntityTypeId
 AND ev.EntityId     = c.ComponentEntityId
 AND ev.EntityVersionId = COALESCE(mev.EntityVersionId, e.EntityLastVersionId)
WHERE c.CompoundEntityTypeId = @t
  AND c.CompoundEntityId     = @i
  AND c.CompoundEntityVersionId = @v";

        using (var cmd = new SqlCommand(partsSql, conn))
        {
            cmd.Parameters.AddWithValue("@t", typeId);
            cmd.Parameters.AddWithValue("@i", entityId);
            cmd.Parameters.AddWithValue("@v", versionId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int childType = r.GetInt32(0);
                if (!_partTypeKeyById!.TryGetValue(childType, out var partKey)) continue;
                if (r.IsDBNull(2)) continue;
                var blob = (byte[])r[2];
                if (blob.Length == 0) continue;
                detail.Parts[partKey] = KbDecoder.DecodePart(blob);
            }
        }

        if (typeId == _transactionTypeId)
        {
            detail.Attributes = ListAttributesForTransaction(conn, entityId);
        }

        return detail;
    }

    // ---------- Search ----------

    public List<SearchHit> Search(string query, string searchIn, int limit, string? typeFilter = null, string? module = null)
    {
        using var conn = Open();
        EnsureTypeMap(conn);
        EnsureContainerTree(conn);
        var hits = new List<SearchHit>();

        var wantedTypes = ResolveTypes(typeFilter);
        if (wantedTypes.Count == 0) return hits;
        var typesCsv = string.Join(",", wantedTypes);

        if (searchIn == "name" || searchIn == "both")
        {
            const string sql = @"
SELECT e.EntityTypeId, e.EntityId, ev.EntityVersionName,
       mev.ModelParentEntityTypeId, mev.ModelParentEntityId
FROM Entity e" + CurrentVersionJoin + @"
WHERE e.EntityTypeId IN (SELECT CAST(value AS int) FROM STRING_SPLIT(@types, ','))
  AND ev.EntityVersionName LIKE @q
ORDER BY ev.EntityVersionName";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@types", typesCsv);
            cmd.Parameters.AddWithValue("@q", ToLikePattern(query));
            using var r = cmd.ExecuteReader();
            while (r.Read() && hits.Count < limit)
            {
                int parentType = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                int parentId = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                var modulePath = ResolveModulePath(parentType, parentId);
                if (module is not null && !ModuleMatches(modulePath, module)) continue;
                hits.Add(new SearchHit
                {
                    Id = r.GetInt32(1),
                    Name = r.IsDBNull(2) ? "" : r.GetString(2),
                    Type = _topLevelById!.GetValueOrDefault(r.GetInt32(0), "?"),
                    Module = modulePath,
                });
            }
        }

        if ((searchIn == "code" || searchIn == "both") && hits.Count < limit)
        {
            // Resolve part type IDs for source-bearing parts. Names come from
            // PartNameAliases above; we filter to the ones that hold text source.
            var sourceTypeIds = _partTypeKeyById!
                .Where(kv => kv.Value is "source" or "rules" or "events" or "conditions")
                .Select(kv => kv.Key)
                .ToList();
            if (sourceTypeIds.Count == 0) return hits;
            var sourceIdsCsv = string.Join(",", sourceTypeIds);

            // No TOP cap here: the scan must visit every source-bearing part or whole
            // object types silently drop out of the results (SQL returns rows in
            // physical order, grouped by part type — a fixed cap starved Procedures
            // in large KBs; see docs/MCP_GeneXus_Bug_Report_gx_search.md). The loop
            // still stops early once 'limit' hits are collected.
            var sql = $@"
SELECT c.CompoundEntityTypeId, c.CompoundEntityId, ev.EntityVersionData,
       mevc.ModelParentEntityTypeId, mevc.ModelParentEntityId
FROM EntityVersionComposition c
LEFT JOIN ModelEntityVersion mev
  ON mev.ModelId=1 AND mev.EntityTypeId=c.ComponentEntityTypeId AND mev.EntityId=c.ComponentEntityId
LEFT JOIN Entity e
  ON e.EntityTypeId=c.ComponentEntityTypeId AND e.EntityId=c.ComponentEntityId
JOIN EntityVersion ev
  ON ev.EntityTypeId=c.ComponentEntityTypeId AND ev.EntityId=c.ComponentEntityId
 AND ev.EntityVersionId=COALESCE(mev.EntityVersionId, e.EntityLastVersionId)
LEFT JOIN ModelEntityVersion mevc
  ON mevc.ModelId=1 AND mevc.EntityTypeId=c.CompoundEntityTypeId AND mevc.EntityId=c.CompoundEntityId
WHERE c.ComponentEntityTypeId IN ({sourceIdsCsv})
  AND c.CompoundEntityTypeId IN (SELECT CAST(value AS int) FROM STRING_SPLIT(@types, ','))
  AND DATALENGTH(ev.EntityVersionData) > 0";

            var pending = new List<(int type, int id, string snippet)>();
            var seen = new HashSet<(int, int)>(hits.Select(h => (TypeIdOf(h.Type), h.Id)));

            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.CommandTimeout = 300;
                cmd.Parameters.AddWithValue("@types", typesCsv);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (pending.Count + hits.Count >= limit) break;
                    int parentType = r.GetInt32(0);
                    int parentId = r.GetInt32(1);
                    if (module is not null)
                    {
                        int objParentType = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                        int objParentId = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                        if (!ModuleMatches(ResolveModulePath(objParentType, objParentId), module)) continue;
                    }
                    var blob = (byte[])r[2];
                    var text = KbDecoder.DecodePart(blob);
                    if (text is null) continue;
                    int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    if (!seen.Add((parentType, parentId))) continue;

                    int start = Math.Max(0, idx - 40);
                    int len = Math.Min(text.Length - start, 160);
                    pending.Add((parentType, parentId, text.Substring(start, len)));
                }
            }

            foreach (var (parentType, parentId, snippet) in pending)
            {
                var (objName, objParentType, objParentId) = ResolveNameAndParent(conn, parentType, parentId);
                hits.Add(new SearchHit
                {
                    Id = parentId,
                    Type = _topLevelById!.GetValueOrDefault(parentType, "?"),
                    Name = objName,
                    Module = ResolveModulePath(objParentType, objParentId),
                    Snippet = snippet,
                });
            }
        }

        return hits;
    }

    // ---------- Attributes ----------

    public List<AttributeInfo>? ListAttributes(string transactionName, string? module = null)
    {
        using var conn = Open();
        EnsureTypeMap(conn);
        var (typeId, entityId, _, _, _, _, _) = FindEntity(conn, transactionName, "Transaction", module);
        if (typeId != _transactionTypeId || entityId < 0) return null;
        return ListAttributesForTransaction(conn, entityId);
    }

    private List<AttributeInfo> ListAttributesForTransaction(SqlConnection conn, int trnEntityId)
    {
        // In GeneXus 17 the Entity.EntityId for a Transaction matches trn_id in
        // the legacy TRN_DSD table (model_id=1). Confirmed on SampleKB.
        const string sql = @"
SELECT td.trn_pos, td.attri_num, a.attri_name, a.attri_type, a.length, a.decimals, a.header,
       CASE WHEN td.key_flag = 1 THEN 1 ELSE 0 END AS is_key
FROM TRN_DSD td
JOIN ATTRIBUTE a ON a.model_id=td.model_id AND a.attri_num=td.attri_num
WHERE td.model_id=@m AND td.trn_id=@id
ORDER BY td.trn_pos";

        var rows = new List<AttributeInfo>();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@m", DesignModelId);
        cmd.Parameters.AddWithValue("@id", trnEntityId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new AttributeInfo
            {
                Position = r.GetInt16(0),
                AttriNum = r.GetInt32(1),
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                Type = KbTypeMap.AttributeTypeName(r.GetInt16(3)),
                Length = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                Decimals = r.IsDBNull(5) ? 0 : r.GetInt16(5),
                Header = r.IsDBNull(6) ? null : r.GetString(6),
                IsKey = r.GetInt32(7) == 1,
            });
        }
        return rows;
    }

    // ---------- Helpers ----------

    private sealed record ObjectCandidate(
        int TypeId, int EntityId, int VersionId, string Name, string? Desc,
        int ParentType, int ParentId, string? ModulePath, string TypeName);

    /// <summary>
    /// Resolves an object by name, optionally scoped to a type and a module path.
    /// <paramref name="name"/> may be module-qualified ("Cotizaciones.BuscaConvenioPromo");
    /// an explicit <paramref name="module"/> takes precedence over qualification, and
    /// <c>module = ""</c> pins the root module. When the name matches objects in more
    /// than one module and no module was given, throws <see cref="AmbiguousObjectException"/>
    /// instead of silently picking one. Same-module homonyms of different types keep the
    /// historical lowest-EntityTypeId pick (e.g. Transaction over its same-named Table).
    /// </summary>
    private (int typeId, int entityId, int versionId, string name, string? desc, int parentType, int parentId) FindEntity(
        SqlConnection conn, string name, string? type, string? module = null)
    {
        EnsureContainerTree(conn);

        var candidates = QueryCandidates(conn, name, type);

        // Module-qualified name: only tried when nothing matches the raw name as-is
        // (GeneXus object names cannot contain dots) and no explicit module was given.
        if (candidates.Count == 0 && module is null && name.Contains('.'))
        {
            int cut = name.LastIndexOf('.');
            module = name[..cut];
            candidates = QueryCandidates(conn, name[(cut + 1)..], type);
        }

        if (module is not null)
            candidates = candidates.Where(c => ModuleMatches(c.ModulePath, module)).ToList();

        if (candidates.Count == 0) return (-1, -1, -1, "", null, 0, 0);

        var distinctModules = candidates
            .Select(c => c.ModulePath ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctModules > 1)
        {
            var list = string.Join("; ", candidates.Select(c =>
                $"{c.TypeName}:{c.Name} (id={c.EntityId}, module={(c.ModulePath is null ? "<root>" : $"'{c.ModulePath}'")})"));
            throw new AmbiguousObjectException(
                $"Ambiguous object name '{name}': matches {candidates.Count} objects — {list}. " +
                "Disambiguate with the 'module' parameter (\"\" = root module) or a qualified name like 'Module.Object'.");
        }

        var c0 = candidates[0];
        return (c0.TypeId, c0.EntityId, c0.VersionId, c0.Name, c0.Desc, c0.ParentType, c0.ParentId);
    }

    private List<ObjectCandidate> QueryCandidates(SqlConnection conn, string name, string? type)
    {
        var typeFilter = "";
        var types = ResolveTypes(type);
        if (types.Count > 0)
            typeFilter = " AND e.EntityTypeId IN (" + string.Join(",", types) + ")";

        var sql = @"
SELECT e.EntityTypeId, e.EntityId,
       COALESCE(mev.EntityVersionId, e.EntityLastVersionId) AS CurrentVersionId,
       ev.EntityVersionName, ev.EntityVersionDescription,
       mev.ModelParentEntityTypeId, mev.ModelParentEntityId
FROM Entity e" + CurrentVersionJoin + @"
WHERE ev.EntityVersionName = @n " + typeFilter + @"
ORDER BY e.EntityTypeId, e.EntityId";

        var list = new List<ObjectCandidate>();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@n", name);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int typeId = r.GetInt32(0);
            int parentType = r.IsDBNull(5) ? 0 : r.GetInt32(5);
            int parentId = r.IsDBNull(6) ? 0 : r.GetInt32(6);
            list.Add(new ObjectCandidate(
                typeId,
                r.GetInt32(1),
                r.GetInt32(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                parentType, parentId,
                ResolveModulePath(parentType, parentId),
                _topLevelById!.GetValueOrDefault(typeId, $"Type{typeId}")));
        }
        return list;
    }

    /// <summary>Compares an object's dotted module path against a filter; "" means root.</summary>
    private static bool ModuleMatches(string? modulePath, string filter) =>
        string.Equals(modulePath ?? "", filter, StringComparison.OrdinalIgnoreCase);

    private static string ToLikePattern(string raw)
    {
        if (raw.Contains('%') || raw.Contains('_')) return raw;
        return "%" + raw + "%";
    }

    private int TypeIdOf(string typeName) =>
        _topLevelByName!.TryGetValue(typeName, out var id) ? id : -1;

    private static (string Name, int ParentType, int ParentId) ResolveNameAndParent(
        SqlConnection conn, int typeId, int entityId)
    {
        using var cmd = new SqlCommand(@"
SELECT TOP 1 ev.EntityVersionName, mev.ModelParentEntityTypeId, mev.ModelParentEntityId
FROM Entity e" + CurrentVersionJoin + @"
WHERE e.EntityTypeId=@t AND e.EntityId=@i", conn);
        cmd.Parameters.AddWithValue("@t", typeId);
        cmd.Parameters.AddWithValue("@i", entityId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ("", 0, 0);
        return (
            r.IsDBNull(0) ? "" : r.GetString(0),
            r.IsDBNull(1) ? 0 : r.GetInt32(1),
            r.IsDBNull(2) ? 0 : r.GetInt32(2)
        );
    }
}
