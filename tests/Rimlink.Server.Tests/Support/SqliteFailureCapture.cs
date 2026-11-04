using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Rimlink.Server.Tests.Support;

// Temporary read-only diagnostics: observe the failing handle before EF closes it.
internal sealed class SqliteFailureCapture : DbCommandInterceptor
{
    public string? LastFailure { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SystemErrno(SQLitePCL.sqlite3 connection);

    private void Capture(DbCommand command)
    {
        try
        {
            if (command.Connection is not SqliteConnection { Handle: { IsClosed: false, IsInvalid: false } handle }) return;
            using var process = Process.GetCurrentProcess();
            var module = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName is "e_sqlite3.dll" or "libe_sqlite3.so");
            IntPtr library = NativeLibrary.Load(module.FileName);
            try
            {
                var error = Marshal.GetDelegateForFunctionPointer<SystemErrno>(NativeLibrary.GetExport(library, "sqlite3_system_errno"));
                LastFailure = $"sqlite3_system_errno={error(handle)}; connection={command.Connection.State}";
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception error) { LastFailure = "diagnostic-unavailable: " + error; }
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Capture(command);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Capture(command);
        return Task.CompletedTask;
    }
}
