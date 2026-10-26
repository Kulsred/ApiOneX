using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiOneX.Models;

public sealed record ChatMessage(string Role, JsonElement Content)
{
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
}
public sealed record FunctionCall(string Name, string Arguments);
public sealed record ToolCall(string Id, string Type, FunctionCall Function);
public sealed record FunctionDefinition(string Name, string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Parameters);
public sealed record ToolDefinition(string Type, FunctionDefinition Function);
public sealed record StreamOptions(bool IncludeUsage = false);
public sealed record ChatRequest(string Model, List<ChatMessage> Messages, bool Stream = false)
{
    public StreamOptions? StreamOptions { get; init; }
    public bool? Store { get; init; }
    public int? MaxCompletionTokens { get; init; }
    public int? MaxTokens { get; init; }
    public List<ToolDefinition>? Tools { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement ToolChoice { get; init; }
    public bool? ParallelToolCalls { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public string? User { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Metadata { get; init; }
    public int? N { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record TextDelta(string Content, string Reasoning);
public sealed class ProxyException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
