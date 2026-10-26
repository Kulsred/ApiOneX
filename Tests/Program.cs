using System.Net;
using System.Text;
using System.Text.Json;
using ApiOneX.Models;
using ApiOneX.Services;
using ApiOneX.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
await PowChecks.RunAsync();
static async Task<List<TextDelta>> Parse(string s)
{
    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(s));
    var values = new List<TextDelta>();
    await foreach (var item in new StreamParser().ParseAsync(stream, default)) values.Add(item);
    return values;
}
var deltas = await Parse("data: {\"v\":{\"response\":{\"fragments\":[{\"type\":\"THINK\",\"content\":\"дум\"}]}}}\r\n\r\ndata: {\"p\":\"response/fragments/-1/content\",\"v\":\"аю\"}\n\ndata: {\"p\":\"response/fragments\",\"v\":[{\"type\":\"RESPONSE\",\"content\":\"Привет 🌍\"}]}\n\ndata: [DONE]\n\n");
Check(string.Concat(deltas.Select(d => d.Reasoning)) == "думаю", "reasoning patches");
Check(string.Concat(deltas.Select(d => d.Content)) == "Привет 🌍", "Unicode content");
var frames = new List<string>();
await foreach (var f in StreamParser.ReadFramesAsync(new MemoryStream(Encoding.UTF8.GetBytes(": comment\r\ndata: {\r\ndata: \"x\":1}\r\n\r\ndata: [DONE]")), default)) frames.Add(f);
Check(frames.Count == 2 && JsonDocument.Parse(frames[0]).RootElement.GetProperty("x").GetInt32() == 1, "multiline SSE and EOF flush");
try { await Parse("data: {\"p\":\"response/content\",\"v\":\"partial\"}\n\n"); throw new Exception("Missing truncation error"); }
catch (ProxyException) { Console.WriteLine("PASS truncated stream error"); }
try { await Parse("data: {\"type\":\"error\"}\n\n"); throw new Exception("Missing upstream error"); }
catch (ProxyException) { Console.WriteLine("PASS upstream error"); }
var handler = new FakeHandler();
var client = new DeepSeekClient(new HttpClient(handler) { BaseAddress = new Uri("https://chat.deepseek.com") }, new FakePow(), new StreamParser());
var request = JsonSerializer.Deserialize<ChatRequest>("{\"model\":\"deepseek-reasoner\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}", Wire.Json)!;
var result = "";
await foreach (var d in client.CompleteAsync(request, default)) result += d.Content;
Check(result == "ok" && handler.Calls == 3, "client session / challenge / streaming pipeline");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await ParseCancelled(cancelled.Token); throw new Exception("Missing cancellation"); }
catch (OperationCanceledException) { Console.WriteLine("PASS cancellation"); }
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(client);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
await using (var app = builder.Build())
{
    app.MapChatEndpoints();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    Check((await http.GetStringAsync("/")).Contains("ApiOneX"), "root endpoint");
    Check((await http.GetAsync("/health")).IsSuccessStatusCode, "health endpoint");
    Check((await http.GetStringAsync("/v1/models")).Contains("deepseek-reasoner"), "models endpoint");
    using var normal = await http.PostAsJsonAsync("/v1/chat/completions", request, Wire.Json);
    var json = JsonDocument.Parse(await normal.Content.ReadAsStringAsync());
    Check(normal.IsSuccessStatusCode && json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() == "ok", "non-stream endpoint");
    using var streaming = await http.PostAsJsonAsync("/v1/chat/completions", request with { Stream = true }, Wire.Json);
    var sse = await streaming.Content.ReadAsStringAsync();
    Check(streaming.Content.Headers.ContentType!.MediaType == "text/event-stream" && sse.Contains("\"content\":\"ok\"") && sse.EndsWith("data: [DONE]\n\n"), "stream endpoint");
    var toolRequest = JsonSerializer.Deserialize<ChatRequest>("""
        {"model":"deepseek-reasoner","messages":[{"role":"developer","content":[{"type":"text","text":"Help with files"}]},{"role":"user","content":"Read hello.txt"}],
        "stream_options":{"include_usage":true},"store":false,"max_completion_tokens":2048,
        "tools":[{"type":"function","function":{"name":"read_file","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}}],
        "tool_choice":"required","parallel_tool_calls":false}
        """, Wire.Json)!;
    DeepSeekClient.Validate(toolRequest);
    Check(ChatCompatibility.BuildPrompt(toolRequest).Contains("read_file") && ChatCompatibility.BuildPrompt(toolRequest).Contains("2048"), "client parameters and tool prompt");
    handler.Reply = ChatCompatibility.OpenMarker + "{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"hello.txt\"}}]}" + ChatCompatibility.CloseMarker;
    using var toolResponse = await http.PostAsJsonAsync("/v1/chat/completions", toolRequest, Wire.Json);
    var toolJson = JsonDocument.Parse(await toolResponse.Content.ReadAsStringAsync());
    var toolChoice = toolJson.RootElement.GetProperty("choices")[0];
    var assistant = toolChoice.GetProperty("message").Deserialize<ChatMessage>(Wire.Json)!;
    var callId = assistant.ToolCalls![0].Id;
    Check(toolResponse.IsSuccessStatusCode && toolChoice.GetProperty("finish_reason").GetString() == "tool_calls" && assistant.ToolCalls[0].Function.Name == "read_file" && assistant.Content.ValueKind == JsonValueKind.Null, "non-stream tool_calls response");
    Check(JsonDocument.Parse(assistant.ToolCalls[0].Function.Arguments).RootElement.GetProperty("path").GetString() == "hello.txt", "JSON tool arguments");
    using var toolStreamResponse = await http.PostAsJsonAsync("/v1/chat/completions", toolRequest with { Stream = true }, Wire.Json);
    var toolSse = await toolStreamResponse.Content.ReadAsStringAsync();
    var chunks = new List<JsonElement>();
    await foreach (var frame in StreamParser.ReadFramesAsync(new MemoryStream(Encoding.UTF8.GetBytes(toolSse)), default))
        if (frame != "[DONE]") chunks.Add(JsonDocument.Parse(frame).RootElement.Clone());
    var callChunk = chunks.Single(c => c.GetProperty("choices").GetArrayLength() > 0 && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("tool_calls", out _));
    Check(callChunk.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0].GetProperty("index").GetInt32() == 0 &&
        chunks.Any(c => c.GetProperty("choices").GetArrayLength() > 0 && c.GetProperty("choices")[0].GetProperty("finish_reason").GetString() == "tool_calls"), "SSE tool_calls indexes and finish reason");
    Check(chunks[^1].GetProperty("choices").GetArrayLength() == 0 && chunks[^1].GetProperty("usage").ValueKind == JsonValueKind.Null && toolSse.EndsWith("data: [DONE]\n\n"), "include_usage without invented counts");
    handler.Reply = "The file says hello.";
    var followup = toolRequest with
    {
        ToolChoice = JsonSerializer.SerializeToElement("auto"),
        Messages = [..toolRequest.Messages, assistant, new("tool", JsonSerializer.SerializeToElement("hello")) { ToolCallId = callId }]
    };
    using var followupResponse = await http.PostAsJsonAsync("/v1/chat/completions", followup, Wire.Json);
    Check(followupResponse.IsSuccessStatusCode && handler.LastPrompt.Contains(callId) && handler.LastPrompt.Contains("tool:\n") &&
        (await followupResponse.Content.ReadAsStringAsync()).Contains("The file says hello."), "assistant tool call and tool result round trip");
    var noTools = toolRequest with { ToolChoice = JsonSerializer.SerializeToElement("none") };
    Check(!ChatCompatibility.CanCallTools(noTools), "tool_choice none");
    try { ChatCompatibility.ParseToolCalls(toolRequest, "ordinary reply"); throw new Exception("Missing required call error"); }
    catch (ProxyException) { Console.WriteLine("PASS required tool enforcement"); }
    try { ChatCompatibility.ParseToolCalls(toolRequest, ChatCompatibility.OpenMarker + "{\"tool_calls\":[{\"name\":\"undeclared\",\"arguments\":{}}]}" + ChatCompatibility.CloseMarker); throw new Exception("Missing unknown tool error"); }
    catch (ProxyException) { Console.WriteLine("PASS undeclared tool rejection"); }
    try { ChatCompatibility.ParseToolCalls(toolRequest, ChatCompatibility.OpenMarker + "broken"); throw new Exception("Missing broken tool error"); }
    catch (ProxyException) { Console.WriteLine("PASS incomplete envelope rejection"); }
    var automatic = toolRequest with { ToolChoice = JsonSerializer.SerializeToElement("auto") };
    Check(ChatCompatibility.ParseToolCalls(automatic, "Example JSON: {\"name\":\"read_file\"}").Count == 0, "ordinary text is not treated as a tool call");
    await app.StopAsync();
}
static async Task ParseCancelled(CancellationToken ct)
{
    await foreach (var _ in new StreamParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n\n")), ct)) { }
}
sealed class FakePow : IPowSolver
{
    public Task<string> SolveAsync(JsonElement challenge, CancellationToken ct) => Task.FromResult("proof");
}
sealed class FakeHandler : HttpMessageHandler
{
    public int Calls;
    public string Reply = "ok";
    public string LastPrompt = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        Calls++;
        var path = r.RequestUri!.AbsolutePath;
        var text = path switch
        {
            "/api/v0/chat_session/create" => "{\"data\":{\"biz_data\":{\"chat_session\":{\"id\":\"test\"}}}}",
            "/api/v0/chat/create_pow_challenge" => "{\"data\":{\"biz_data\":{\"challenge\":{}}}}",
            DeepSeekClient.CompletionPath => "data: " + JsonSerializer.Serialize(new { p = "response/content", v = Reply }) + "\n\ndata: [DONE]\n\n",
            _ => throw new Exception("Unexpected route")
        };
        if (path == DeepSeekClient.CompletionPath)
        {
            if (r.Headers.GetValues("x-ds-pow-response").Single() != "proof") throw new Exception("PoW header");
            var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            LastPrompt = body.RootElement.GetProperty("prompt").GetString()!;
            if (!body.RootElement.GetProperty("thinking_enabled").GetBoolean()) throw new Exception("Reasoning mode");
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, path == DeepSeekClient.CompletionPath ? "text/event-stream" : "application/json") };
    }
}
