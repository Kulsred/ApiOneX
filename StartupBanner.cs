using System.Runtime.InteropServices;
using System.Text;

namespace ApiOneX;

internal static class StartupBanner
{
    private static readonly string[] Logo =
    [
        @"     ___         _   ____             _  __",
        @"    /   |  ____  (_) / __ \____  ___  | |/ /",
        @"   / /| | / __ \/ / / / / / __ \/ _ \ |   / ",
        @"  / ___ |/ /_/ / / / /_/ / / / /  __//   |  ",
        @" /_/  |_/ .___/_/  \____/_/ /_/\___//_/|_|  ",
        @"       /_/                                 "
    ];

    public static async Task ShowAsync(string address, CancellationToken ct = default)
    {
        var interactive = !Console.IsOutputRedirected &&
            Environment.GetEnvironmentVariable("NO_COLOR") is null &&
            Environment.GetEnvironmentVariable("TERM") != "dumb" && EnableAnsi();
        if (!interactive)
        {
            Console.WriteLine(string.Join(Environment.NewLine, Logo));
            Console.WriteLine($"  ApiOneX / kulsred dev :3\n  Сделано по фану за 10 минут с Кодексом\n  DeepSeek Web -> OpenAI API\n  Listen: {address}\n");
            return;
        }

        const string reset = "\x1b[0m";
        Console.WriteLine();
        // Redraw only our logo, leaving the terminal's earlier output intact.
        try
        {
            Console.Write("\x1b[?25l");
            for (var frame = 0; frame < 20; frame++)
            {
                if (frame > 0) Console.Write($"\x1b[{Logo.Length}A");
                var buffer = new StringBuilder();
                for (var row = 0; row < Logo.Length; row++)
                {
                    buffer.Append("\r\x1b[2K");
                    for (var col = 0; col < Logo[row].Length; col++)
                    {
                        var reveal = col < (frame + 1) * 3;
                        var highlight = Math.Abs(col - frame * 3) < 5;
                        var color = highlight ? "\x1b[97m" : row < 3 ? "\x1b[38;5;51m" : "\x1b[38;5;141m";
                        buffer.Append(color).Append(reveal ? Logo[row][col] : ' ');
                    }
                    buffer.Append(reset).AppendLine();
                }
                Console.Write(buffer);
                await Task.Delay(32, ct);
            }
            Console.WriteLine("\x1b[38;5;141m  ───────────────────────────────────────────────" + reset);
            Console.WriteLine("  \x1b[97mLOCAL AI GATEWAY\x1b[0m     \x1b[38;5;141mby kulsred dev :3\x1b[0m");
            Console.WriteLine("  \x1b[38;5;245mСделано по фану за 10 минут с Кодексом\x1b[0m");
            Console.WriteLine("  \x1b[38;5;245mDeepSeek Web  ·  OpenAI compatible  ·  SSE\x1b[0m");
            Console.WriteLine();
            Console.WriteLine($"  \x1b[38;5;51m>\x1b[0m  LISTEN    \x1b[97m{address}\x1b[0m");
            Console.WriteLine("  \x1b[38;5;51m+\x1b[0m  POST      /v1/chat/completions");
            Console.WriteLine("  \x1b[38;5;141m+\x1b[0m  GET       /v1/models");
            Console.WriteLine("  \x1b[38;5;141m+\x1b[0m  GET       /health");
            Console.WriteLine();
            Console.WriteLine("  \x1b[38;5;245m[ CTRL+C to shut down ]\x1b[0m");
            Console.WriteLine("\x1b[38;5;141m  ───────────────────────────────────────────────\x1b[0m\n");
        }
        finally { Console.Write(reset + "\x1b[?25h"); }
    }

    private static bool EnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var handle = GetStdHandle(-11);
        return GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | 0x0004);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
