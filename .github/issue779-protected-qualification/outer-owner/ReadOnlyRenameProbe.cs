using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Performs the immutable replacement denial probe without File.Move's copy/delete fallback.</summary>
internal static class ReadOnlyRenameProbe
{
    private const int AtFdcwd = -100;
    private const uint RenameNoReplace = 1;
    private const int Eacces = 13;
    private const int Ebusy = 16;
    private const int Erofs = 30;
    private const int OCloseOnExec = 0x80000;
    private const int ONoFollow = 0x20000;
    private const int OPath = 0x200000;
    private const int MaximumSourceBytes = 16 * 1024 * 1024;

    /// <summary>Requires atomic no-replace rename denial and unchanged source/destination custody.</summary>
    internal static void RequireDenied(string source, string destination)
    {
        var sourceBefore = OwnerCustody.StatPath(source);
        Require(sourceBefore.Type == OwnerCustody.RegularType && sourceBefore.Links == 1,
            "immutable-replace-source-shape");
        var sourceBytes = OwnerCustody.ReadRootFile(source, sourceBefore.Uid, sourceBefore.Mode, MaximumSourceBytes);
        Require((ulong)sourceBytes.Length == sourceBefore.Size, "immutable-replace-source-size");
        var sourceDigest = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        RequireDestinationAbsent(destination);

        var result = RenameAt2(AtFdcwd, source, AtFdcwd, destination, RenameNoReplace);
        if (result == 0) throw new IsolationException("immutable-replace-unexpected-success");
        var error = Marshal.GetLastPInvokeError();
        Require(IsExpectedDenial(error), "immutable-replace-unexpected-error");

        var sourceAfter = OwnerCustody.StatPath(source);
        var sourceBytesAfter = OwnerCustody.ReadRootFile(source, sourceBefore.Uid, sourceBefore.Mode, MaximumSourceBytes);
        Require(SameNode(sourceBefore, sourceAfter) && (ulong)sourceBytesAfter.Length == sourceBefore.Size
            && Convert.ToHexStringLower(SHA256.HashData(sourceBytesAfter)) == sourceDigest,
            "immutable-replace-source-changed");
        RequireDestinationAbsent(destination);
    }

    /// <summary>Portable errno policy seam: only permission/read-only/busy denials count as expected.</summary>
    internal static bool IsExpectedDenial(int errno) => errno is Eacces or Erofs or Ebusy;

    private static void RequireDestinationAbsent(string destination)
    {
        var descriptor = Open(destination, OPath | ONoFollow | OCloseOnExec);
        if (descriptor >= 0)
        {
            _ = Close(descriptor);
            throw new IsolationException("immutable-replace-destination-exists");
        }
        if (Marshal.GetLastPInvokeError() != 2) // ENOENT only; EACCES, ENOTDIR, symlink, etc. reject.
            throw new IsolationException("immutable-replace-destination-stat");
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new IsolationException(code);
    }

    private static bool SameNode(OwnerCustody.NodeFact left, OwnerCustody.NodeFact right) =>
        left.Device == right.Device && left.Inode == right.Inode && left.Type == right.Type
        && left.Mode == right.Mode && left.Uid == right.Uid && left.Gid == right.Gid
        && left.Links == right.Links && left.Size == right.Size;

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, string oldPath, int newDirectory,
        string newPath, uint flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
