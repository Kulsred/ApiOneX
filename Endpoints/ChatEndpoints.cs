using System.Text;
using System.Text.Json;
using ApiOneX.Models;
using ApiOneX.Services;

namespace ApiOneX.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            name = "ApiOneX", author = "kulsred dev :3", status = "running",
            description = "DeepSeek Web / OpenAI-compatible local API",
            endpoints = new[] { "GET /health", "GET /v1/models", "POST /v1/chat/completions" }
        }));
        app.MapGet("/health", () => Results.Ok(new { status = "ok", upstream_verified = false }));
        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list", data = DeepSeekClient.Models.Select(id => new { id, @object = "model", created = 0, owned_by = "deepseek-web" })
        }));
        app.MapPost("/v1/chat/completions", HandleAsync);
    }

    private static async Task HandleAsync(ChatRequest request, HttpContext context, DeepSeekClient client)
    {
        DeepSeekClient.Validate(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var content = new StringBuilder(); var reasoning = new StringBuilder();
        await foreach (var delta in client.CompleteAsync(request, ct))
        {
            if (!request.Stream) { content.Append(delta.Content); reasoning.Append(delta.Reasoning); continue; }
            if (!context.Response.HasStarted)
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await SendAsync(context, Chunk(new { role = "assistant" }), ct);
            }
            if (delta.Content.Length > 0) await SendAsync(context, Chunk(new { content = delta.Content }), ct);
            if (delta.Reasoning.Length > 0) await SendAsync(context, Chunk(new { reasoning_content = delta.Reasoning }), ct);
        }
        if (request.Stream)
        {
            if (!context.Response.HasStarted) context.Response.ContentType = "text/event-stream; charset=utf-8";
            await SendAsync(context, new { id, @object = "chat.completion.chunk", created, model = request.Model,
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } }, ct);
            await context.Response.WriteAsync("data: [DONE]\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
        else
            await context.Response.WriteAsJsonAsync(new { id, @object = "chat.completion", created, model = request.Model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = content.ToString(),
                    reasoning_content = reasoning.ToString() }, finish_reason = "stop" } } }, Wire.Json, ct);

        object Chunk(object delta) => new { id, @object = "chat.completion.chunk", created, model = request.Model,
            choices = new[] { new { index = 0, delta, finish_reason = (string?)null } } };
    }

    public static async Task SendAsync(HttpContext context, object payload, CancellationToken ct)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload, Wire.Json) + "\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
}
