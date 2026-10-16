using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageDock;

// QA-only inspection of our own NotifyIcon, never another application's tray objects.
internal static class TrayInspection
{
    [StructLayout(LayoutKind.Sequential)] private struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rectangle rect);
    public static object Read(NotifyIcon tray)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var id = (uint?)typeof(NotifyIcon).GetField("_id", flags)?.GetValue(tray);
        var window = typeof(NotifyIcon).GetField("_window", flags)?.GetValue(tray) as NativeWindow;
        if (id is null || window is null) return new { registered = false, reason = "Runtime inspection unavailable" };
        var identifier = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Window = window.Handle, Id = id.Value };
        var result = Shell_NotifyIconGetRect(ref identifier, out var rect);
        return new { registered = result == 0, hresult = result, nativeWindow = window.Handle.ToInt64(), id,
            bounds = new { rect.Left, rect.Top, rect.Right, rect.Bottom }, icon = "White OpenAI vector from upstream codex.svg" };
    }
}
