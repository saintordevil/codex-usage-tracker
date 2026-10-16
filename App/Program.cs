using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using UsageDock.Core;

namespace UsageDock;

public static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);
    [STAThread]
    public static int Main(string[] args)
    {
        var qa = args.Contains("--qa");
        var demo = args.Contains("--demo");
        var state = Argument(args, "--state");
        if (demo) state ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageDockDemo");
        if (args.Contains("--enable-startup") || args.Contains("--disable-startup"))
        {
            try
            {
                WindowsStartup.Configure(Environment.ProcessPath!, args.Contains("--enable-startup"), Argument(args, "--startup-dir"));
                if (!args.Contains("--quiet")) MessageBox.Show(args.Contains("--enable-startup") ? "Usage Dock will start quietly when you sign into Windows." : "Usage Dock will no longer start with Windows.", "Usage Dock");
                return 0;
            }
            catch (Exception e) { if (!args.Contains("--quiet")) MessageBox.Show(e.Message, "Usage Dock", MessageBoxButton.OK, MessageBoxImage.Warning); return 1; }
        }
        if (Argument(args, "--render-example") is not null && !demo) return 2;
        if (Argument(args, "--write-icon") is { } iconPath) { File.WriteAllBytes(iconPath, IconAsset.IconBytes()); return 0; }
        if (args.Contains("--quit") || args.Contains("--refresh"))
        {
            var prefix = demo ? "Local\\UsageDock.Demo" : qa ? "Local\\UsageDock.QA" : "Local\\UsageDock";
            try { using var signal = EventWaitHandle.OpenExisting(prefix + (args.Contains("--quit") ? ".Quit" : ".Refresh")); signal.Set(); return 0; }
            catch (WaitHandleCannotBeOpenedException) { return 1; }
        }
        if (args.Contains("--diagnose"))
        {
            if (demo) return 2;
            try { return Diagnose(args, state).GetAwaiter().GetResult(); }
            catch (Exception e) { if (Argument(args, "--output") is { } output) File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { error = e.GetType().Name, message = SafeError(e) })); return 1; }
        }
        using var mutex = new Mutex(true, demo ? "Local\\UsageDock.Demo" : qa ? "Local\\UsageDock.QA" : "Local\\UsageDock.Personal", out var first);
        if (!first)
        {
            // A repeated sign-in launch must leave the existing panel hidden.
            if (args.Contains("--background")) return 0;
            // Signal the existing instance without a socket or a second tray icon.
            try { using var signal = EventWaitHandle.OpenExisting(demo ? "Local\\UsageDock.Demo.Show" : "Local\\UsageDock.Show"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (qa && Argument(args, "--qa-output") is { } qaOutput)
            {
                Directory.CreateDirectory(qaOutput);
                File.WriteAllText(Path.Combine(qaOutput, "unhandled.json"), JsonSerializer.Serialize(new { error = e.Exception.GetType().Name, detail = e.Exception.Message, stack = e.Exception.StackTrace }));
                e.Handled = true; app.Shutdown(1); return;
            }
            MessageBox.Show(SafeError(e.Exception), "Usage Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        try
        {
            using var controller = new AppController(new AppStorage(state), qa, args);
            return app.Run();
        }
        catch (Exception e)
        {
            if (qa && Argument(args, "--qa-output") is { } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "startup-error.json"), JsonSerializer.Serialize(new { error = e.GetType().Name, detail = e.Message, stack = e.StackTrace }));
            }
            else MessageBox.Show(SafeError(e), "Usage Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }
    public static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    public static string SafeError(Exception e) => e switch
    {
        OperationCanceledException => "The request timed out. Refresh will retry shortly.",
        System.Net.Http.HttpRequestException => "Codex could not be reached. Check your connection; your last reading is retained.",
        UnauthorizedAccessException => "Usage Dock cannot read the selected Codex folder. Choose your own Codex home in Settings.",
        JsonException => "A saved reading was invalid. Refresh to try again.",
        InvalidOperationException or InvalidDataException => e.Message,
        _ => "This action could not finish. Check your Codex sign-in and refresh."
    };
    private static async Task<int> Diagnose(string[] args, string? root)
    {
        var storage = new AppStorage(root);
        var settings = storage.Read<AppSettings>("settings.json") ?? new();
        var home = storage.CodexHome(settings);
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(args.Contains("--history-only") ? 10 : 2));
        using var client = new CodexClient();
        var watch = Stopwatch.StartNew();
        var usage = args.Contains("--history-only") ? null : args.Contains("--bridge") ? await AppServerBridge.ReadAsync(home, token.Token) : await client.ReadAsync(home, token.Token);
        var history = await new HistoryReader(storage, new Pricing()).ScanAsync(home, token.Token);
        var result = new { usage, history, elapsedMs = watch.ElapsedMilliseconds, identity = CodexClient.Identity(home), noGenerationRequests = true };
        if (Argument(args, "--output") is { } output)
            await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
