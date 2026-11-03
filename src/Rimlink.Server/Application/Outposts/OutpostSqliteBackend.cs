using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Rimlink.Server.Application.Outposts;

public enum OutpostDatabaseRole { Anchor, Domain }

public sealed record OutpostSqliteReport(
    string NativeVersion,
    string SourceId,
    string Vfs,
    string NativeSha256,
    string ProviderVersion,
    string JournalMode,
    int Synchronous);

// Qualification of the pinned SQLite backend only. The caller separately qualifies the filesystem,
// deployment and ordering protocol; this report is not a full-stack or power-loss guarantee.
public static class OutpostSqliteBackend
{
    private const string NativeVersion = "3.53.3";
    private const string NativeSourceId = "2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62";
    private const string ProviderVersion = "2.1.13.3146";
    private const string WindowsHash = "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e";
    private const string LinuxHash = "b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07";
    private const int SqliteFcntlVfsName = 12;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SourceId();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FileControl(sqlite3 db, [MarshalAs(UnmanagedType.LPUTF8Str)] string dbName, int op, ref IntPtr arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SqliteFree(IntPtr pointer);

    // Requires exclusive use of this connection while validating. The native installation and the
    // selected SQLitePCL provider must be trusted and immutable (no concurrent replacement/switch).
    public static OutpostSqliteReport Validate(SqliteConnection connection, OutpostDatabaseRole role)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            if (role is not (OutpostDatabaseRole.Anchor or OutpostDatabaseRole.Domain))
                throw new InvalidDataException("outpost-sqlite-unknown-role");
            if (connection.State != ConnectionState.Open || connection.Handle is not { IsInvalid: false, IsClosed: false })
                throw new InvalidDataException("outpost-sqlite-connection-not-open");
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                throw new InvalidDataException("outpost-sqlite-unsupported-platform");

            ValidateFileDatabase(connection);
            ValidateCompileOptions(connection);
            string journal = Convert.ToString(Scalar(connection, "PRAGMA main.journal_mode;")) ?? "";
            int synchronous = Convert.ToInt32(Scalar(connection, "PRAGMA main.synchronous;"));
            bool accepted = role == OutpostDatabaseRole.Anchor
                ? journal == "persist" && synchronous == 3
                : journal == "wal" && synchronous is 2 or 3;
            if (!accepted) throw new InvalidDataException("outpost-sqlite-journal-or-synchronous-mismatch");

            if (typeof(SqliteConnection).Assembly.GetName().Version?.ToString() != "10.0.14.0" ||
                typeof(raw).Assembly.GetName().Version?.ToString() != ProviderVersion ||
                raw.GetNativeLibraryName() != "e_sqlite3")
                throw new InvalidDataException("outpost-sqlite-managed-provider-mismatch");

            if (!Equals(Scalar(connection, "SELECT sqlite_version();"), NativeVersion) ||
                !Equals(Scalar(connection, "SELECT sqlite_source_id();"), NativeSourceId) ||
                raw.sqlite3_libversion().utf8_to_string() != NativeVersion ||
                raw.sqlite3_sourceid().utf8_to_string() != NativeSourceId)
                throw new InvalidDataException("outpost-sqlite-native-identity-mismatch");

            var (vfs, hash) = InspectNativeBackend(connection.Handle!);
            return new(NativeVersion, NativeSourceId, vfs, hash, ProviderVersion, journal, synchronous);
        }
        catch (Exception error) when (error is not (InvalidDataException or OutOfMemoryException or AccessViolationException))
        {
            // Missing loader/module/PRAGMA evidence is a refusal, not a fallback to another backend.
            throw new InvalidDataException("outpost-sqlite-qualification-unavailable", error);
        }
    }

    private static void ValidateFileDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA database_list;";
        using var reader = command.ExecuteReader();
        int mainCount = 0;
        while (reader.Read())
        {
            if (reader.GetString(1) != "main") continue;
            mainCount++;
            string file = reader.GetString(2);
            // Memory and temporary databases have no durable main filename, even if their
            // connection string happens to resemble a normal path.
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) || !File.Exists(file))
                throw new InvalidDataException("outpost-sqlite-main-not-file-backed");
        }
        if (mainCount != 1) throw new InvalidDataException("outpost-sqlite-main-not-file-backed");
    }

    private static void ValidateCompileOptions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options;";
        using var reader = command.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            count++;
            string option = reader.GetString(0);
            if (option.StartsWith("SQLITE_", StringComparison.Ordinal)) option = option[7..];
            string name = option.Split('=', 2)[0];
            if (name is "NO_SYNC" or "OMIT_DISKIO" || option == "THREADSAFE=0")
                throw new InvalidDataException("outpost-sqlite-unsafe-compile-options");
        }
        if (count == 0) throw new InvalidDataException("outpost-sqlite-compile-options-unavailable");
    }

    private static unsafe (string Vfs, string Hash) InspectNativeBackend(sqlite3 db)
    {
        // The inspected e_sqlite3 provider returns the native static source-id address, not a copy.
        // Do not load a logical DLL name and assume that it is the provider's actual module.
        utf8z sourceId = raw.sqlite3_sourceid();
        fixed (byte* pointer = sourceId)
        {
            IntPtr sourceAddress = (IntPtr)pointer;
            ulong address = (ulong)(nuint)sourceAddress;
            using var process = Process.GetCurrentProcess();
            ProcessModule[] matches = process.Modules.Cast<ProcessModule>().Where(module =>
                module.ModuleMemorySize > 0 && address >= (ulong)(nuint)module.BaseAddress &&
                address - (ulong)(nuint)module.BaseAddress < (ulong)module.ModuleMemorySize).ToArray();
            if (sourceAddress == IntPtr.Zero || matches.Length != 1)
                throw new InvalidDataException("outpost-sqlite-native-module-unresolved");
            string path = matches[0].FileName;
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidDataException("outpost-sqlite-native-module-path-unresolved");

            // This hashes the current installation FILE, not mapped memory. Safety relies on the
            // trusted immutable installation assumption above; replacing native files requires
            // requalification/restart, not a claim that this detects arbitrary mapped-memory tampering.
            using var file = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            string expectedHash = OperatingSystem.IsWindows() ? WindowsHash : LinuxHash;
            if (hash != expectedHash) throw new InvalidDataException("outpost-sqlite-native-hash-mismatch");

            IntPtr library = NativeLibrary.Load(path);
            try
            {
                var getSourceId = Marshal.GetDelegateForFunctionPointer<SourceId>(NativeLibrary.GetExport(library, "sqlite3_sourceid"));
                if (getSourceId() != sourceAddress)
                    throw new InvalidDataException("outpost-sqlite-native-module-pointer-mismatch");
                var fileControl = Marshal.GetDelegateForFunctionPointer<FileControl>(NativeLibrary.GetExport(library, "sqlite3_file_control"));
                var free = Marshal.GetDelegateForFunctionPointer<SqliteFree>(NativeLibrary.GetExport(library, "sqlite3_free"));
                IntPtr text = IntPtr.Zero;
                try
                {
                    int result = fileControl(db, "main", SqliteFcntlVfsName, ref text);
                    if (result != raw.SQLITE_OK || text == IntPtr.Zero)
                        throw new InvalidDataException("outpost-sqlite-vfs-unavailable");
                    string? vfs = Marshal.PtrToStringUTF8(text);
                    // VFSNAME is diagnostic evidence, not independent proof of flush semantics.
                    // Exact equality excludes wrappers, shims and undocumented VFS variants.
                    if (vfs != (OperatingSystem.IsWindows() ? "win32" : "unix"))
                        throw new InvalidDataException("outpost-sqlite-vfs-mismatch");
                    return (vfs, hash);
                }
                finally
                {
                    free(text); // Only the allocated VFSNAME string, never the static source-id.
                }
            }
            finally
            {
                NativeLibrary.Free(library); // Release only the extra reference acquired above.
            }
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
