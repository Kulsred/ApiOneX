using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ApiOneX.Models;

namespace ApiOneX.Services;

public sealed class StreamParser
{
    public async IAsyncEnumerable<TextDelta> ParseAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var state = new PatchState();
        await foreach (var frame in ReadFramesAsync(stream, ct))
        {
            if (frame == "[DONE]") yield break;
            JsonObject root;
            try { root = JsonNode.Parse(frame) as JsonObject ?? throw new FormatException(); }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
            { throw new ProxyException(502, "Invalid DeepSeek SSE JSON."); }
            yield return state.Apply(root);
        }
        if (!state.Finished) throw new ProxyException(502, "DeepSeek stream ended without a completion marker.");
    }

    public static async IAsyncEnumerable<string> ReadFramesAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString().TrimEnd('\n'); data.Clear(); }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' ')) value = value[1..];
                data.Append(value).Append('\n');
                if (data.Length > 4 * 1024 * 1024) throw new ProxyException(502, "DeepSeek SSE frame too large.");
            }
        }
        if (data.Length > 0) yield return data.ToString().TrimEnd('\n');
    }

    private sealed class PatchState
    {
        private readonly List<JsonObject> fragments = [];
        private string path = "", content = "", reasoning = "";
        public bool Finished { get; private set; }
        private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        private void Append(JsonNode? value)
        {
            if (value is JsonArray array) foreach (var item in array) Append(item);
            else if (value is JsonObject obj) fragments.Add((JsonObject)obj.DeepClone());
        }
        public TextDelta Apply(JsonObject root)
        {
            if (Text(root["type"]) == "error" || root["error"] is not null)
                throw new ProxyException(502, "DeepSeek reported a generation error.");
            var before = content; var beforeReasoning = reasoning;
            if (root["p"] is not null) path = Text(root["p"]);
            var value = root["v"];
            if (value is JsonObject wrapper && wrapper["response"] is JsonObject response)
            {
                if (response["content"] is not null) content = Text(response["content"]);
                if (response["fragments"] is JsonArray items) { fragments.Clear(); Append(items); }
                if (Text(response["status"]) == "FINISHED" || Text(response["finish_reason"]) is { Length: > 0 }) Finished = true;
            }
            if (path == "response/fragments") Append(value);
            if (path == "response" && value is JsonArray ops)
                foreach (var op in ops.OfType<JsonObject>())
                {
                    if (Text(op["p"]) == "fragments" && Text(op["o"]) == "APPEND") Append(op["v"]);
                    if (Text(op["p"]) == "status" && Text(op["v"]) == "FINISHED") Finished = true;
                    if (Text(op["p"]) == "finish_reason" && Text(op["v"]).Length > 0) Finished = true;
                }
            if (path == "response/fragments/-1/content" && fragments.Count > 0)
            {
                var last = fragments[^1];
                last["content"] = Text(root["o"]) == "SET" ? Text(value) : Text(last["content"]) + Text(value);
            }
            if (path == "response/content") content = Text(root["o"]) == "SET" ? Text(value) : content + Text(value);
            if (fragments.Count > 0)
            {
                content = string.Concat(fragments.Where(f => Text(f["type"]) is "RESPONSE" or "SEARCH").Select(f => Text(f["content"])));
                reasoning = string.Concat(fragments.Where(f => Text(f["type"]) is "THINK" or "REASONING").Select(f => Text(f["content"])));
            }
            if (Text(root["finish_reason"]).Length > 0 ||
                path == "response/finish_reason" && Text(value).Length > 0 ||
                path == "response/status" && Text(value) == "FINISHED") Finished = true;
            // OpenAI deltas cannot retract text that has already been sent.
            if (!content.StartsWith(before, StringComparison.Ordinal) || !reasoning.StartsWith(beforeReasoning, StringComparison.Ordinal))
                throw new ProxyException(502, "DeepSeek replaced emitted text; append-only SSE cannot represent this patch.");
            return new(content[before.Length..], reasoning[beforeReasoning.Length..]);
        }
    }
}
