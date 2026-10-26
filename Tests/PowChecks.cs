using System.Net;
using System.Text;
using System.Text.Json;
using ApiOneX.Models;
using ApiOneX.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

internal static class PowChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "apionex-pow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Scripts"));
        try
        {
            var script = Path.Combine(directory, "Scripts", "pow-bridge.cjs");
            var auth = new AuthSession(new DeepSeekAuth { WasmUrl = "https://fe-static.deepseek.com/test.wasm" }, new CookieContainer());
            var solver = new NodePowSolver(auth, new ConfigurationBuilder().Build(), new TestEnvironment(directory));
            await File.WriteAllTextAsync(script, "process.stdin.resume(); process.stdin.on('end',()=>{if(process.env.NODE_USE_SYSTEM_CA!=='1')process.exit(2);console.log(42);});");
            var challenge = JsonSerializer.SerializeToElement(new { algorithm = "DeepSeekHashV1", challenge = "test", salt = "salt", signature = "signature" });
            var proof = await solver.SolveAsync(challenge, default);
            using var parsed = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(proof)));
            if (parsed.RootElement.GetProperty("answer").GetInt64() != 42 || parsed.RootElement.GetProperty("target_path").GetString() != DeepSeekClient.CompletionPath)
                throw new Exception("Invalid PoW proof");
            Console.WriteLine("PASS PoW child process receives system CA and returns encoded proof");
            await File.WriteAllTextAsync(script, "console.error(JSON.stringify({code:'POW_TLS_CERTIFICATE'}));process.exitCode=1;");
            try { await solver.SolveAsync(challenge, default); throw new Exception("Missing TLS error"); }
            catch (ProxyException e) when (e.Message.Contains("certificate validation")) { Console.WriteLine("PASS PoW TLS failure diagnostic"); }
            await File.WriteAllTextAsync(script, "console.error('private-debug-data');process.exitCode=1;");
            try { await solver.SolveAsync(challenge, default); throw new Exception("Missing worker error"); }
            catch (ProxyException e) when (!e.Message.Contains("private-debug-data")) { Console.WriteLine("PASS PoW stderr is not exposed"); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private sealed class TestEnvironment(string directory) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ApiOneX.Tests";
        public string ContentRootPath { get; set; } = directory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
