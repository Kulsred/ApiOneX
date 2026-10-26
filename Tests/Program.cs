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
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        Calls++;
        var path = r.RequestUri!.AbsolutePath;
        var text = path switch
        {
            "/api/v0/chat_session/create" => "{\"data\":{\"biz_data\":{\"chat_session\":{\"id\":\"test\"}}}}",
            "/api/v0/chat/create_pow_challenge" => "{\"data\":{\"biz_data\":{\"challenge\":{}}}}",
            DeepSeekClient.CompletionPath => "data: {\"p\":\"response/content\",\"v\":\"ok\"}\n\ndata: [DONE]\n\n",
            _ => throw new Exception("Unexpected route")
        };
        if (path == DeepSeekClient.CompletionPath)
        {
            if (r.Headers.GetValues("x-ds-pow-response").Single() != "proof") throw new Exception("PoW header");
            var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (!body.RootElement.GetProperty("thinking_enabled").GetBoolean()) throw new Exception("Reasoning mode");
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, path == DeepSeekClient.CompletionPath ? "text/event-stream" : "application/json") };
    }
}
