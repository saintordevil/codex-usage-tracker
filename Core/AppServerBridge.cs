using System.Diagnostics;
using System.Text.Json;

namespace UsageDock.Core;

public static class AppServerBridge
{
    public static string? FindExecutable()
    {
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktop))
        {
            var found = Directory.EnumerateFiles(desktop, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found;
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        if (Directory.Exists(npm)) return Directory.EnumerateFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
        return null;
    }
    public static async Task<UsageSnapshot> ReadAsync(string home, CancellationToken cancellation)
    {
        var path = FindExecutable() ?? throw new InvalidOperationException("Open Codex and sign in with ChatGPT, then refresh. No Codex app-server was found.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        start.Environment["CODEX_HOME"] = home;
        using var child = Process.Start(start) ?? throw new IOException("Could not start Codex's account reader.");
        // Drain, but never log stderr or accumulate unbounded diagnostic output.
        var drain = DrainAsync(child.StandardError, token);
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "usage_dock", title = "Usage Dock", version = "1.0.0" } } });
            await Reply(1);
            await Send(new { method = "initialized" });
            await Send(new { id = 2, method = "account/read", @params = new { refreshToken = false } });
            var account = await Reply(2);
            var info = J.Get(account, "account");
            if (J.Text(info, "type") != "chatgpt") throw new InvalidOperationException("Sign in to Codex with your ChatGPT account, then refresh.");
            await Send(new { id = 3, method = "account/rateLimits/read" });
            var result = await Reply(3);
            return QuotaMapper.FromAppServer(result, J.Text(info, "planType") ?? "ChatGPT", DateTimeOffset.UtcNow);
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.HasExited)
            {
                using var end = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await child.WaitForExitAsync(end.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            }
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            timeout.Cancel();
            try { await drain.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        async Task Send(object message)
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
            await child.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        async Task<JsonElement> Reply(int id)
        {
            for (var i = 0; i < 100; i++)
            {
                var line = await child.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) throw new IOException("Codex's account reader stopped unexpectedly.");
                if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException("Codex returned an oversized account response.");
                using var json = JsonDocument.Parse(line);
                if (J.Number(json.RootElement, "id") != id) continue;
                if (J.Get(json.RootElement, "error").ValueKind == JsonValueKind.Object)
                    throw new InvalidOperationException("Codex could not read subscription limits. Check your sign-in in Codex.");
                return J.Get(json.RootElement, "result").Clone();
            }
            throw new IOException("Codex's account reader returned too many unrelated messages.");
        }
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
    }
}
