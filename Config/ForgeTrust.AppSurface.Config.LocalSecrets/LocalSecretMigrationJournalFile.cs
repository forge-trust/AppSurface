using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Stores only operation metadata using the same private-path checks and durable replacement as file secrets.</summary>
/// <remarks>The caller holds the namespace lease throughout read, commit and subsequent store operations.</remarks>
internal sealed class LocalSecretMigrationJournalFile(string path)
{
    private static readonly DefaultFileAppSurfaceLocalSecretStoreFileSystem Files = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance;

    internal AppSurfaceLocalSecretMigrationJournal? Read()
    {
        if (Files.InspectExistingFilePosture(path).Kind == FileSecretPostureKind.Unsupported)
            throw new IOException("The migration journal path is unsafe.");
        if (!Files.FileExists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<AppSurfaceLocalSecretMigrationJournal>(Files.ReadAllText(path))
                ?? throw new IOException("The migration journal is empty.");
        }
        catch (JsonException exception) { throw new IOException("The migration journal is invalid.", exception); }
    }

    internal void Commit(AppSurfaceLocalSecretMigrationJournal journal)
    {
        if (Files.WriteAllTextWithPosture(path, JsonSerializer.Serialize(journal)).Kind == FileSecretPostureKind.Unsupported)
            throw new IOException("The migration journal cannot be durably committed.");
    }
}

/// <summary>Publishes a flushed temporary file and persists the directory entry before acknowledging a transition.</summary>
internal static partial class LocalSecretDurableFile
{
    internal static void Replace(string temporaryPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH persists the replacement before returning.
            if (!MoveFileEx(temporaryPath, path, 1 | 8)) throw new IOException("Durable file replacement failed.");
            return;
        }
        File.Move(temporaryPath, path, overwrite: true);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var handle = open(directory, 0);
        if (handle < 0) throw new IOException("The containing directory could not be opened for durable publication.");
        try
        {
            if (fsync(handle) != 0) throw new IOException("The containing directory could not be flushed.");
        }
        finally { close(handle); }
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int fsync(int descriptor);
    [LibraryImport("libc")]
    private static partial int close(int descriptor);
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, int flags);
}
