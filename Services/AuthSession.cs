using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiOneX.Services;

public sealed class DeepSeekAuth
{
    public string Token { get; init; } = "";
    public JsonElement Cookies { get; init; }
    public string? Cookie { get; init; }
    public string WasmUrl { get; init; } = "";
    public string UserAgent { get; init; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";
    [JsonPropertyName("hif_dliq")] public string? HifDliq { get; init; }
    [JsonPropertyName("hif_leim")] public string? HifLeim { get; init; }
}

public sealed record AuthSession(DeepSeekAuth Auth, CookieContainer Cookies)
{
    public static async Task<AuthSession> LoadAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var auth = await JsonSerializer.DeserializeAsync<DeepSeekAuth>(file,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
            ?? throw new InvalidDataException("Empty auth file.");
        if (string.IsNullOrWhiteSpace(auth.Token)) throw new InvalidDataException("Auth token is missing.");
        if (!Uri.TryCreate(auth.WasmUrl, UriKind.Absolute, out var wasm) ||
            wasm.Scheme != "https" || wasm.Host != "fe-static.deepseek.com")
            throw new InvalidDataException("wasmUrl must use https://fe-static.deepseek.com/.");
        var jar = new CookieContainer();
        var origin = new Uri("https://chat.deepseek.com");
        void Add(string name, string value) => jar.Add(origin, new Cookie(name, value, "/"));
        var raw = auth.Cookie;
        if (auth.Cookies.ValueKind == JsonValueKind.String) raw = auth.Cookies.GetString();
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Trim().Split('=', 2);
                if (parts.Length == 2) Add(parts[0], parts[1]);
            }
        if (auth.Cookies.ValueKind == JsonValueKind.Array)
            foreach (var cookie in auth.Cookies.EnumerateArray())
            {
                // Import browser cookies only if their domain applies to chat.deepseek.com.
                if (cookie.TryGetProperty("domain", out var domain))
                {
                    var host = domain.GetString()?.TrimStart('.');
                    if (host != "deepseek.com" && host != "chat.deepseek.com") continue;
                }
                Add(cookie.GetProperty("name").GetString()!, cookie.GetProperty("value").GetString()!);
            }
        if (auth.Cookies.ValueKind == JsonValueKind.Object)
            foreach (var cookie in auth.Cookies.EnumerateObject()) Add(cookie.Name, cookie.Value.GetString()!);
        if (jar.Count == 0) throw new InvalidDataException("Auth cookies are missing.");
        return new(auth, jar);
    }
}
