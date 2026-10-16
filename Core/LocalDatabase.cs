using System.Runtime.InteropServices;

namespace UsageDock.Core;

// Windows ships SQLite. Bind parameters, keep an 8 MiB cache, and never open a Codex database for writing.
internal sealed class LocalDatabase : IDisposable
{
    private IntPtr db;
    public LocalDatabase(string path)
    {
        Check(Native.sqlite3_open_v2(path, out db, 6, IntPtr.Zero));
        Native.sqlite3_busy_timeout(db, 3000);
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-8192; PRAGMA temp_store=FILE;");
    }
    internal void Check(int code) { if (code != 0) throw new IOException("Local usage database could not complete an operation (" + code + ")."); }
    public void Execute(string sql)
    {
        var code = Native.sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        Check(code);
    }
    public Statement Prepare(string sql) => new(this, sql);
    public void Dispose() { if (db != IntPtr.Zero) { Native.sqlite3_close_v2(db); db = IntPtr.Zero; } }
    public sealed class Statement : IDisposable
    {
        private readonly LocalDatabase owner;
        private IntPtr handle;
        internal Statement(LocalDatabase owner, string sql)
        { this.owner = owner; owner.Check(Native.sqlite3_prepare_v2(owner.db, sql, -1, out handle, IntPtr.Zero)); }
        public Statement Bind(params object?[] values)
        {
            owner.Check(Native.sqlite3_reset(handle)); owner.Check(Native.sqlite3_clear_bindings(handle));
            for (int i = 0; i < values.Length; i++) owner.Check(values[i] switch
            {
                null => Native.sqlite3_bind_null(handle, i + 1),
                string s => Native.sqlite3_bind_text(handle, i + 1, s, -1, new IntPtr(-1)),
                double n => Native.sqlite3_bind_double(handle, i + 1, n),
                _ => Native.sqlite3_bind_int64(handle, i + 1, Convert.ToInt64(values[i], System.Globalization.CultureInfo.InvariantCulture))
            });
            return this;
        }
        public bool Step() { var code = Native.sqlite3_step(handle); if (code == 100) return true; if (code == 101) return false; owner.Check(code); return false; }
        public string Text(int col) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(handle, col)) ?? "";
        public long Long(int col) => Native.sqlite3_column_int64(handle, col);
        public double Double(int col) => Native.sqlite3_column_double(handle, col);
        public void Dispose() { if (handle != IntPtr.Zero) { Native.sqlite3_finalize(handle); handle = IntPtr.Zero; } }
    }
    private static class Native
    {
        private const string Lib = "winsqlite3.dll";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr pointer);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int col, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int col, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_double(IntPtr statement, int col, double value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_clear_bindings(IntPtr statement);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr statement, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
    }
}
