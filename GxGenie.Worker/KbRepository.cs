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
/// </remarks>
public sealed class KbRepository
{
    private readonly string _connectionString;
    private readonly string _kbDirectory;
    private readonly IKbSchemaAdapter _schema;
    private const int DesignModelId = 1;

    private readonly object _typeMapGate = new();
    private Dictionary<string, int>? _topLevelByName;
    private Dictionary<int, string>? _topLevelById;
    private Dictionary<int, string>? _partTypeKeyById;
    private int _transactionTypeId = -1;

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
            int trnId = -1;

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
            }

            _topLevelByName = byName;
            _topLevelById = byId;
            _partTypeKeyById = partKeyById;
            _transactionTypeId = trnId;
        }
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
        var result = new List<ObjectListItem>();

        var wantedTypes = ResolveTypes(typeFilter);
        if (wantedTypes.Count == 0) return result;

        var typesCsv = string.Join(",", wantedTypes);
        var sql = @"
SELECT e.EntityTypeId, e.EntityId, ev.EntityVersionName, ev.EntityVersionDescription
FROM Entity e
JOIN EntityVersion ev
  ON ev.EntityTypeId = e.EntityTypeId
 AND ev.EntityId = e.EntityId
 AND ev.EntityVersionId = e.EntityLastVersionId
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
            result.Add(new ObjectListItem
            {
                Id = r.GetInt32(1),
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                Type = _topLevelById!.GetValueOrDefault(typeId, $"Type{typeId}"),
                Description = r.IsDBNull(3) ? null : r.GetString(3),
            });
            n++;
        }
        return result;
    }

    // ---------- Read ----------

    public ObjectDetail? ReadObject(string name, string? type)
    {
        using var conn = Open();
        EnsureTypeMap(conn);

        var (typeId, entityId, versionId, versionName, versionDesc) = FindEntity(conn, name, type);
        if (entityId < 0) return null;

        var detail = new ObjectDetail
        {
            Name = versionName,
            Type = _topLevelById!.GetValueOrDefault(typeId, $"Type{typeId}"),
            Description = versionDesc,
        };

        const string partsSql = @"
SELECT c.ComponentEntityTypeId, c.ComponentEntityId, ev.EntityVersionData
FROM EntityVersionComposition c
JOIN Entity e
  ON e.EntityTypeId = c.ComponentEntityTypeId
 AND e.EntityId    = c.ComponentEntityId
JOIN EntityVersion ev
  ON ev.EntityTypeId = e.EntityTypeId
 AND ev.EntityId     = e.EntityId
 AND ev.EntityVersionId = e.EntityLastVersionId
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

    public List<SearchHit> Search(string query, string searchIn, int limit)
    {
        using var conn = Open();
        EnsureTypeMap(conn);
        var hits = new List<SearchHit>();
        var typesCsv = TopLevelTypesCsv();

        if (searchIn == "name" || searchIn == "both")
        {
            const string sql = @"
SELECT TOP (@lim) e.EntityTypeId, e.EntityId, ev.EntityVersionName
FROM Entity e
JOIN EntityVersion ev
  ON ev.EntityTypeId=e.EntityTypeId AND ev.EntityId=e.EntityId AND ev.EntityVersionId=e.EntityLastVersionId
WHERE e.EntityTypeId IN (SELECT CAST(value AS int) FROM STRING_SPLIT(@types, ','))
  AND ev.EntityVersionName LIKE @q
ORDER BY ev.EntityVersionName";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@lim", limit);
            cmd.Parameters.AddWithValue("@types", typesCsv);
            cmd.Parameters.AddWithValue("@q", ToLikePattern(query));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                hits.Add(new SearchHit
                {
                    Id = r.GetInt32(1),
                    Name = r.IsDBNull(2) ? "" : r.GetString(2),
                    Type = _topLevelById!.GetValueOrDefault(r.GetInt32(0), "?"),
                });
            }
        }

        if (searchIn == "code" || searchIn == "both")
        {
            int budget = Math.Max(0, limit - hits.Count);
            if (budget > 0)
            {
                // Resolve part type IDs for source-bearing parts. Names come from
                // PartNameAliases above; we filter to the ones that hold text source.
                var sourceTypeIds = _partTypeKeyById!
                    .Where(kv => kv.Value is "source" or "rules" or "events" or "conditions")
                    .Select(kv => kv.Key)
                    .ToList();
                if (sourceTypeIds.Count == 0) return hits;
                var sourceIdsCsv = string.Join(",", sourceTypeIds);

                var sql = $@"
SELECT TOP (@scan) c.CompoundEntityTypeId, c.CompoundEntityId, ev.EntityVersionData
FROM EntityVersionComposition c
JOIN Entity e
  ON e.EntityTypeId=c.ComponentEntityTypeId AND e.EntityId=c.ComponentEntityId
JOIN EntityVersion ev
  ON ev.EntityTypeId=e.EntityTypeId AND ev.EntityId=e.EntityId AND ev.EntityVersionId=e.EntityLastVersionId
WHERE c.ComponentEntityTypeId IN ({sourceIdsCsv})
  AND c.CompoundEntityTypeId IN (SELECT CAST(value AS int) FROM STRING_SPLIT(@types, ','))
  AND DATALENGTH(ev.EntityVersionData) > 0";

                var pending = new List<(int type, int id, string snippet)>();
                var seen = new HashSet<(int, int)>(hits.Select(h => (TypeIdOf(h.Type), h.Id)));

                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@scan", 4000);
                    cmd.Parameters.AddWithValue("@types", typesCsv);
                    using var r = cmd.ExecuteReader();
                    while (r.Read() && pending.Count + hits.Count < limit)
                    {
                        int parentType = r.GetInt32(0);
                        int parentId = r.GetInt32(1);
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
                    hits.Add(new SearchHit
                    {
                        Id = parentId,
                        Type = _topLevelById!.GetValueOrDefault(parentType, "?"),
                        Name = ResolveName(conn, parentType, parentId),
                        Snippet = snippet,
                    });
                }
            }
        }

        return hits;
    }

    // ---------- Attributes ----------

    public List<AttributeInfo>? ListAttributes(string transactionName)
    {
        using var conn = Open();
        EnsureTypeMap(conn);
        var (typeId, entityId, _, _, _) = FindEntity(conn, transactionName, "Transaction");
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

    private (int typeId, int entityId, int versionId, string name, string? desc) FindEntity(
        SqlConnection conn, string name, string? type)
    {
        var typeFilter = "";
        var types = ResolveTypes(type);
        if (types.Count > 0)
            typeFilter = " AND e.EntityTypeId IN (" + string.Join(",", types) + ")";

        var sql = @"
SELECT TOP 1 e.EntityTypeId, e.EntityId, e.EntityLastVersionId, ev.EntityVersionName, ev.EntityVersionDescription
FROM Entity e
JOIN EntityVersion ev
  ON ev.EntityTypeId=e.EntityTypeId AND ev.EntityId=e.EntityId AND ev.EntityVersionId=e.EntityLastVersionId
WHERE ev.EntityVersionName = @n " + typeFilter + @"
ORDER BY e.EntityTypeId";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@n", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (-1, -1, -1, "", null);
        return (
            r.GetInt32(0),
            r.GetInt32(1),
            r.GetInt32(2),
            r.IsDBNull(3) ? "" : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4)
        );
    }

    private static string ToLikePattern(string raw)
    {
        if (raw.Contains('%') || raw.Contains('_')) return raw;
        return "%" + raw + "%";
    }

    private int TypeIdOf(string typeName) =>
        _topLevelByName!.TryGetValue(typeName, out var id) ? id : -1;

    private static string ResolveName(SqlConnection conn, int typeId, int entityId)
    {
        using var cmd = new SqlCommand(@"
SELECT ev.EntityVersionName
FROM Entity e
JOIN EntityVersion ev ON ev.EntityTypeId=e.EntityTypeId AND ev.EntityId=e.EntityId AND ev.EntityVersionId=e.EntityLastVersionId
WHERE e.EntityTypeId=@t AND e.EntityId=@i", conn);
        cmd.Parameters.AddWithValue("@t", typeId);
        cmd.Parameters.AddWithValue("@i", entityId);
        return cmd.ExecuteScalar() as string ?? "";
    }
}
