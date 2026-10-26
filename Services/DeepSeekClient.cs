using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ApiOneX.Models;

namespace ApiOneX.Services;

public sealed class DeepSeekClient(HttpClient http, IPowSolver pow, StreamParser parser)
{
    public const string CompletionPath = "/api/v0/chat/completion";
    public static readonly string[] Models = ["deepseek-chat", "deepseek-reasoner", "deepseek-chat-search", "deepseek-reasoner-search"];

    public static void Validate(ChatRequest request)
    {
        if (!Models.Contains(request.Model)) throw new ProxyException(400, "Unsupported model. See /v1/models.");
        if (request.Messages is not { Count: > 0 }) throw new ProxyException(400, "messages must not be empty.");
        ChatCompatibility.Validate(request);
        if (request.Extra is { Count: > 0 })
            throw new ProxyException(400, "Unsupported parameters: " + string.Join(", ", request.Extra.Keys));
    }

    public async IAsyncEnumerable<TextDelta> CompleteAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Validate(request);
        var session = await PostJsonAsync("/api/v0/chat_session/create", new { }, ct);
        var biz = session.GetProperty("data").GetProperty("biz_data");
        var id = biz.TryGetProperty("chat_session", out var chat) ? chat.GetProperty("id") : biz.GetProperty("id");
        var challengeResult = await PostJsonAsync("/api/v0/chat/create_pow_challenge", new { target_path = CompletionPath }, ct);
        var challenge = challengeResult.GetProperty("data").GetProperty("biz_data").GetProperty("challenge");
        var proof = await pow.SolveAsync(challenge, ct);
        // A new remote session per request isolates concurrent clients. Replay complete local history.
        var prompt = ChatCompatibility.BuildPrompt(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, CompletionPath)
        {
            Content = JsonContent.Create(new
            {
                chat_session_id = id, parent_message_id = (string?)null, model_type = "default", prompt,
                ref_file_ids = Array.Empty<string>(), thinking_enabled = request.Model.Contains("reasoner"),
                search_enabled = request.Model.EndsWith("-search"), action = (string?)null, preempt = false
            })
        };
        message.Headers.Add("x-ds-pow-response", proof);
        message.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new ProxyException(502, "DeepSeek returned a non-SSE completion response.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var delta in parser.ParseAsync(stream, ct)) yield return delta;
    }

    private async Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        EnsureSuccess(response);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var n) && n != 0 ||
            root.TryGetProperty("data", out var data) && data.TryGetProperty("biz_code", out var bizCode) && bizCode.TryGetInt32(out var b) && b != 0)
            throw new ProxyException(502, "DeepSeek rejected the Web API request.");
        return root;
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        throw new ProxyException(status is 401 or 403 or 429 ? status : 502, $"DeepSeek upstream HTTP {status}.");
    }
}
