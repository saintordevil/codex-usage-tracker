using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UsageDock;

internal static class WindowsStartup
{
    public static void Configure(string executable, bool enable, string? directory = null)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Keep UsageDock.exe in a permanent folder before enabling startup.");
        directory ??= Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Usage Dock.lnk");
        if (!enable && !File.Exists(path)) return;
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        object shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Could not create the Windows shortcut helper.");
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            if (File.Exists(path) && !string.Equals(Path.GetFullPath((string)link.TargetPath), executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A Usage Dock startup shortcut already points to another folder. Remove that shortcut from shell:startup before changing installations.");
            if (enable)
            {
                link.TargetPath = executable; link.Arguments = "--background";
                link.WorkingDirectory = Path.GetDirectoryName(executable)!;
                link.IconLocation = executable + ",0"; link.Description = "Start Usage Dock quietly in the notification area";
                link.WindowStyle = 7; link.Save();
            }
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        if (!enable) File.Delete(path);
    }
}
