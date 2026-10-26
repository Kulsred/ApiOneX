using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApiOneX.Models;

namespace ApiOneX.Services;

public interface IPowSolver
{
    Task<string> SolveAsync(JsonElement challenge, CancellationToken ct);
}

public sealed class NodePowSolver(AuthSession session, IConfiguration config, IHostEnvironment env) : IPowSolver
{
    public async Task<string> SolveAsync(JsonElement challenge, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(config["DeepSeek:NodePath"] ?? "node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        // Node's bundled CAs can differ from the Windows store used by .NET.
        // Supported by Node >=22.19 / >=24.6; never disable TLS verification.
        if (config.GetValue("DeepSeek:UseSystemCa", true)) start.Environment["NODE_USE_SYSTEM_CA"] = "1";
        start.ArgumentList.Add(Path.Combine(env.ContentRootPath, "Scripts", "pow-bridge.cjs"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Node.js.");
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { challenge, wasmUrl = session.Auth.WasmUrl }).AsMemory(), timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        var text = await output;
        var diagnostics = await error;
        if (process.ExitCode != 0 || !long.TryParse(text.Trim(), out var answer) || answer <= 0)
            throw new ProxyException(502, SafeFailure(diagnostics));
        var payload = new
        {
            algorithm = challenge.GetProperty("algorithm").GetString(),
            challenge = challenge.GetProperty("challenge").GetString(),
            salt = challenge.GetProperty("salt").GetString(), answer,
            signature = challenge.GetProperty("signature").GetString(),
            target_path = DeepSeekClient.CompletionPath
        };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    internal static string SafeFailure(string diagnostics)
    {
        // Only return known codes; raw stderr may contain paths or network details.
        string? code = null;
        try { using var json = JsonDocument.Parse(diagnostics); code = json.RootElement.GetProperty("code").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return code switch
        {
            "POW_TLS_CERTIFICATE" => "PoW WASM TLS certificate validation failed. Use Node >=22.19 or >=24.6 with system CA support, or configure NODE_EXTRA_CA_CERTS with your trusted CA.",
            "POW_DOWNLOAD" => "PoW WASM download failed. Check CDN access, proxy settings and wasmUrl.",
            "POW_TIMEOUT" => "PoW WASM download timed out.",
            "POW_UNSUPPORTED_ALGORITHM" => "DeepSeek returned an unsupported PoW algorithm.",
            "POW_WASM_ABI" => "PoW WASM exports are incompatible with the solver.",
            "POW_INVALID_INPUT" => "DeepSeek returned an invalid PoW challenge or WASM URL.",
            "POW_NO_SOLUTION" => "PoW solver could not find a valid answer for the challenge.",
            _ => "PoW worker failed. Check Node.js availability and the Scripts directory."
        };
    }
}
