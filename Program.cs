using ApiOneX;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ApiOneX.Endpoints;
using ApiOneX.Models;
using ApiOneX.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:3001");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
await StartupBanner.ShowAsync(builder.Configuration["Urls"] ?? "http://127.0.0.1:3001");
var authPath = builder.Configuration["DeepSeek:AuthFile"] ?? "deepseek-auth.json";
var session = await AuthSession.LoadAsync(Path.GetFullPath(authPath, builder.Environment.ContentRootPath), CancellationToken.None);
builder.Services.AddSingleton(session);
builder.Services.AddSingleton<StreamParser>();
builder.Services.AddSingleton<IPowSolver, NodePowSolver>();
// One dedicated cookie jar/account. A singleton handler avoids cookie loss on factory rotation.
builder.Services.AddSingleton(_ =>
{
    var handler = new SocketsHttpHandler
    {
        CookieContainer = session.Cookies, UseCookies = true, AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20)
    };
    var http = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://chat.deepseek.com"), Timeout = TimeSpan.FromMinutes(2)
    };
    http.DefaultRequestHeaders.UserAgent.ParseAdd(session.Auth.UserAgent);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Auth.Token);
    http.DefaultRequestHeaders.Add("Origin", "https://chat.deepseek.com");
    http.DefaultRequestHeaders.Referrer = new Uri("https://chat.deepseek.com/");
    http.DefaultRequestHeaders.Add("x-client-platform", "web");
    http.DefaultRequestHeaders.Add("x-client-version", "2.0.0");
    http.DefaultRequestHeaders.Add("x-client-locale", "ru");
    http.DefaultRequestHeaders.Add("x-client-timezone-offset", "14400");
    if (!string.IsNullOrEmpty(session.Auth.HifDliq)) http.DefaultRequestHeaders.Add("x-hif-dliq", session.Auth.HifDliq);
    if (!string.IsNullOrEmpty(session.Auth.HifLeim)) http.DefaultRequestHeaders.Add("x-hif-leim", session.Auth.HifLeim);
    return http;
});
builder.Services.AddSingleton<DeepSeekClient>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
    catch (Exception ex)
    {
        var status = ex switch { ProxyException p => p.Status, BadHttpRequestException => 400, OperationCanceledException => 504, _ => 502 };
        var text = ex switch { ProxyException p => p.Message, BadHttpRequestException => "Invalid request JSON.", OperationCanceledException => "Upstream request timed out.", _ => "DeepSeek protocol or transport failure." };
        var payload = new { error = new { message = text, type = "proxy_error", code = status } };
        if (context.Response.HasStarted)
        {
            // HTTP status cannot change once SSE starts. Send an error, never a success marker.
            if (!context.RequestAborted.IsCancellationRequested)
                try { await ChatEndpoints.SendAsync(context, payload, context.RequestAborted); }
                catch (IOException) { context.Abort(); }
        }
        else
        {
            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
        }
    }
});
app.MapChatEndpoints();
await app.RunAsync();
