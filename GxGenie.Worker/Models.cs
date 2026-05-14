using System.Text.Json.Serialization;

namespace GxGenie.Worker;

public sealed class WorkerRequest
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("params")]
    public Dictionary<string, object?>? Params { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public sealed class WorkerResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    public static WorkerResponse Ok(object? data, string? id = null)
        => new() { Success = true, Data = data, Id = id };

    public static WorkerResponse Fail(string error, string? id = null)
        => new() { Success = false, Error = error, Id = id };
}

public sealed class ObjectListItem
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Description { get; set; }
    public string? Module { get; set; }
    public int Id { get; set; }
}

public sealed class AttributeInfo
{
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Length { get; set; }
    public int Decimals { get; set; }
    public bool IsKey { get; set; }
    public string? Header { get; set; }
}

public sealed class ObjectDetail
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Description { get; set; }
    public Dictionary<string, string?> Parts { get; set; } = new();
    public List<AttributeInfo>? Attributes { get; set; }
}

public sealed class KbInfo
{
    public string KbName { get; set; } = "";
    public string KbPath { get; set; } = "";
    public int KbVersion { get; set; }
    public List<ModelInfo> Models { get; set; } = new();
    public Dictionary<string, int> ObjectCounts { get; set; } = new();
    public int TotalEntities { get; set; }
}

public sealed class ModelInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Type { get; set; }
}

public sealed class SearchHit
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Id { get; set; }
    public string? Snippet { get; set; }
}
