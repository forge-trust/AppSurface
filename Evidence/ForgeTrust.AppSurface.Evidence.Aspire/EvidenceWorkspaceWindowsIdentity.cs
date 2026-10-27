using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Retains a Windows directory object and its volume/128-bit file identity through workspace cleanup.</summary>
/// <remarks>Opens the final component without following reparse points and rejects nondirectories.
/// Sharing permits ordinary producer writes, rename and deletion; a renamed original remains alive until release.
/// Creation timestamps are not identity because Windows can preserve them when a path is recreated.</remarks>
internal sealed partial class EvidenceWorkspaceWindowsIdentity : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileIdentity _identity;

    private EvidenceWorkspaceWindowsIdentity(SafeFileHandle handle, FileIdentity identity)
    {
        _handle = handle;
        _identity = identity;
    }

    /// <summary>Retains an existing directory using an extended absolute path and no-follow native open.</summary>
    internal static EvidenceWorkspaceWindowsIdentity Open(string path)
    {
        // FILE_READ_ATTRIBUTES, read/write/delete sharing, OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
        var handle = CreateFile(ExtendedPath(Path.GetFullPath(path)), 0x80, 0x7, 0, 3, 0x02200000, 0);
        try
        {
            if (handle.IsInvalid || !GetAttributes(handle, 9, out var attributes, 8)
                || (attributes.Attributes & 0x410) != 0x10
                || !GetIdentity(handle, 18, out var identity, 24) || !IsUsableFileId(identity.Low, identity.High))
                throw new IOException("Evidence workspace directory identity is unavailable.");
            return new EvidenceWorkspaceWindowsIdentity(handle, identity);
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Checks a no-follow reopen against the still-retained original directory.</summary>
    internal bool Matches(string path)
    {
        if (_handle.IsClosed) return false;
        using var current = Open(path);
        return current._identity.Equals(_identity);
    }

    /// <summary>Releases the original directory after cleanup or failed admission.</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>Rejects an unavailable all-zero native file ID before it can authorize a root.</summary>
    internal static bool IsUsableFileId(ulong low, ulong high) => low != 0 || high != 0;

    /// <summary>Formats an already absolute drive or UNC path for a Unicode native open.</summary>
    /// <remarks>The caller normalizes navigation before adding this prefix; it grants no filesystem access.</remarks>
    internal static string ExtendedPath(string absolutePath)
    {
        var path = absolutePath.Replace('/', '\\');
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal ulong Volume;
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, nint security, uint disposition, uint flags, nint template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAttributes(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIdentity(SafeFileHandle handle, int informationClass, out FileIdentity information, uint size);
}
