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
        await error; // Drain stderr, but never return upstream diagnostics or auth to a client.
        if (process.ExitCode != 0 || !long.TryParse(text.Trim(), out var answer) || answer <= 0)
            throw new ProxyException(502, "PoW solver failed; check WASM compatibility.");
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
}
