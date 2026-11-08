using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Publishes a complete owned file through a single create-if-absent filesystem operation.</summary>
internal static class AtomicFilePublication
{
    // File.Move on Unix may check destination existence before rename, allowing a
    // concurrent winner to be replaced. link creates the destination atomically and
    // fails when any destination node exists. Staging must be on the same filesystem.
    internal static void Publish(string ownedStagedFile, string absentDestination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(ownedStagedFile, absentDestination, overwrite: false);
            return;
        }
        var result = OperatingSystem.IsMacOS() ? LinkMac(ownedStagedFile, absentDestination) : LinkUnix(ownedStagedFile, absentDestination);
        if (result != 0) throw new IOException("Atomic absent-destination publication failed.");
        // Publication is committed. Failure to unlink our staging name must not
        // turn the accepted output into a reported failure or delete the winner.
        try { File.Delete(ownedStagedFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinkMac(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinkUnix(string existingPath, string newPath);
}
