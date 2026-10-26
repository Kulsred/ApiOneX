using System.Text;
using System.Text.Json;
using ApiOneX.Models;

namespace ApiOneX.Services;

// Web chat has no native function-calling API. This adapter translates tools to
// a constrained text protocol, then returns OpenAI tool_calls to the caller.
// It never executes a tool or gives the model access to local functions itself.
public static class ChatCompatibility
{
    public const string OpenMarker = "<apionex_tool_calls>";
    public const string CloseMarker = "</apionex_tool_calls>";

    public static string ContentText(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in value.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) ||
                    type.GetString() != "text" || !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new ProxyException(400, "Only text content parts are supported.");
                parts.Add(text.GetString()!);
            }
            return string.Join("\n", parts);
        }
        throw new ProxyException(400, "Message content must be text, text parts, or null for assistant tool calls.");
    }

    public static string Choice(ChatRequest request)
    {
        var choice = request.ToolChoice;
        if (choice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "auto";
        if (choice.ValueKind == JsonValueKind.String && choice.GetString() is "auto" or "none" or "required") return choice.GetString()!;
        if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("type", out var type) && type.GetString() == "function" &&
            choice.TryGetProperty("function", out var function) && function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
            request.Tools?.Any(t => t.Function?.Name == name.GetString()) == true) return "function:" + name.GetString();
        throw new ProxyException(400, "Invalid tool_choice or unknown function name.");
    }

    public static bool CanCallTools(ChatRequest request) => request.Tools is { Count: > 0 } && Choice(request) != "none";

    public static void Validate(ChatRequest request)
    {
        if (request.MaxCompletionTokens is <= 0 || request.MaxTokens is <= 0)
            throw new ProxyException(400, "Token limits must be positive.");
        if (request.N is not null and not 1) throw new ProxyException(400, "Only n=1 is supported.");
        if (request.Temperature is < 0 or > 2 || request.TopP is < 0 or > 1)
            throw new ProxyException(400, "Invalid temperature or top_p.");
        foreach (var message in request.Messages)
        {
            if (message is null || message.Role is not ("system" or "developer" or "user" or "assistant" or "tool"))
                throw new ProxyException(400, "Unsupported message role.");
            var text = ContentText(message.Content);
            if (message.Content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined &&
                !(message.Role == "assistant" && message.ToolCalls is { Count: > 0 }))
                throw new ProxyException(400, "Content is required unless an assistant message contains tool_calls.");
            if (message.Role == "tool" && string.IsNullOrWhiteSpace(message.ToolCallId))
                throw new ProxyException(400, "Tool results require tool_call_id.");
            if (message.ToolCalls is { Count: > 0 })
            {
                if (message.Role != "assistant") throw new ProxyException(400, "Only assistant messages may contain tool_calls.");
                foreach (var call in message.ToolCalls)
                {
                    if (call is null || call.Type != "function" || string.IsNullOrWhiteSpace(call.Id) || call.Function is null || string.IsNullOrWhiteSpace(call.Function.Name))
                        throw new ProxyException(400, "Invalid assistant tool call.");
                    try { using var arguments = JsonDocument.Parse(call.Function.Arguments); }
                    catch (Exception ex) when (ex is JsonException or ArgumentNullException)
                    { throw new ProxyException(400, "Tool call arguments must be JSON strings."); }
                }
            }
        }
        if (request.Tools is { Count: > 0 })
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in request.Tools)
                if (tool is null || tool.Type != "function" || tool.Function is null || string.IsNullOrWhiteSpace(tool.Function.Name) ||
                    !names.Add(tool.Function.Name) || tool.Function.Parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
                    throw new ProxyException(400, "Only uniquely named function tools with JSON object parameters are supported.");
        }
        var choice = Choice(request);
        if (choice != "auto" && choice != "none" && request.Tools is not { Count: > 0 })
            throw new ProxyException(400, "tool_choice requires tools.");
    }

    public static string BuildPrompt(ChatRequest request)
    {
        var prompt = new StringBuilder();
        foreach (var message in request.Messages)
        {
            prompt.Append(message.Role).Append(":\n");
            if (message.ToolCallId is not null) prompt.Append("tool_call_id: ").Append(message.ToolCallId).AppendLine();
            prompt.AppendLine(ContentText(message.Content));
            if (message.ToolCalls is { Count: > 0 })
                prompt.Append("tool_calls: ").AppendLine(JsonSerializer.Serialize(message.ToolCalls, Wire.Json));
            prompt.AppendLine();
        }
        var budget = request.MaxCompletionTokens ?? request.MaxTokens;
        if (budget is not null) prompt.AppendLine($"system: Keep this response within approximately {budget} output tokens.");
        if (CanCallTools(request))
        {
            prompt.AppendLine("system: The client can execute these function tools. Their definitions follow:");
            prompt.AppendLine(JsonSerializer.Serialize(request.Tools, Wire.Json));
            prompt.AppendLine("To invoke tools, output ONLY this exact envelope, with valid JSON and no Markdown fences:");
            prompt.AppendLine(OpenMarker + "{\"tool_calls\":[{\"name\":\"FUNCTION_NAME\",\"arguments\":{\"argument\":\"value\"}}]}" + CloseMarker);
            prompt.AppendLine("Arguments must be JSON objects matching the declared tool schemas. Never invent tool names or tool results. The client returns results as tool messages.");
            var choice = Choice(request);
            if (choice == "required") prompt.AppendLine("You MUST invoke at least one tool in this response.");
            else if (choice.StartsWith("function:", StringComparison.Ordinal)) prompt.AppendLine("You MUST invoke only this function: " + choice[9..]);
            else prompt.AppendLine("If no tool is needed, respond with ordinary text and do not use the tool envelope.");
            if (request.ParallelToolCalls == false) prompt.AppendLine("Invoke exactly one tool at a time.");
        }
        else if (request.Tools is { Count: > 0 }) prompt.AppendLine("system: Do not invoke tools. Respond with ordinary text only.");
        prompt.Append("assistant:\n");
        return prompt.ToString();
    }

    public static List<ToolCall> ParseToolCalls(ChatRequest request, string text)
    {
        if (!CanCallTools(request)) return [];
        var choice = Choice(request);
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(OpenMarker, StringComparison.Ordinal))
        {
            if (choice != "auto") throw new ProxyException(502, "DeepSeek did not produce the required tool call.");
            return [];
        }
        if (!trimmed.EndsWith(CloseMarker, StringComparison.Ordinal)) throw new ProxyException(502, "Incomplete tool-call envelope.");
        try
        {
            using var doc = JsonDocument.Parse(trimmed[OpenMarker.Length..^CloseMarker.Length]);
            var calls = doc.RootElement.GetProperty("tool_calls");
            if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0) throw new FormatException();
            var result = new List<ToolCall>();
            foreach (var call in calls.EnumerateArray())
            {
                var name = call.GetProperty("name").GetString();
                if (name is null || request.Tools!.All(t => t.Function.Name != name) || choice.StartsWith("function:") && name != choice[9..])
                    throw new FormatException();
                var args = call.GetProperty("arguments");
                if (args.ValueKind != JsonValueKind.Object) throw new FormatException();
                result.Add(new("call_" + Guid.NewGuid().ToString("N"), "function", new(name, args.GetRawText())));
            }
            if (request.ParallelToolCalls == false && result.Count != 1) throw new FormatException();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ProxyException(502, "DeepSeek produced an invalid or undeclared tool call."); }
    }
}
