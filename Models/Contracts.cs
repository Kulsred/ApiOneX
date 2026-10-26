using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiOneX.Models;

public sealed record ChatMessage(string Role, JsonElement Content);
public sealed record ChatRequest(string Model, List<ChatMessage> Messages, bool Stream = false)
{
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
