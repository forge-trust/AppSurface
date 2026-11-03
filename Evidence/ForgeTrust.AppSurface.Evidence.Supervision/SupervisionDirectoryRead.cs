using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Collects the original bounded directory blocks without acquiring a directory or owner.</summary>
/// <remarks>
/// The caller opens and authenticates the original directory, supplies its existing getdents operation,
/// and retains every metadata/name recheck outside this procedure. Callbacks run synchronously in order.
/// This procedure creates no handle, timer, retry, workspace, custody or admission. The borrowed buffer
/// belongs to the caller. Strict kernel-block decoding remains <see cref="LinuxDirectoryData.Parse(ReadOnlySpan{byte})"/>.
/// </remarks>
internal static class SupervisionDirectoryRead
{
    /// <summary>Runs the procedure against the caller's already opened native directory descriptor.</summary>
    /// <remarks>The caller validates Linux x64 and retains this handle through the synchronous call.
    /// The descriptor's directory position advances; the caller must supply its original fresh open-file
    /// description. Native read errors propagate unchanged. No descriptor is acquired or closed here.</remarks>
    internal static string[] ReadActual(SafeFileHandle directory, byte[] buffer, Action beforeRead,
        Action afterRead, int maximumNames, int? maximumBlocks, Func<string, bool>? acceptName,
        Func<Exception> reject) => Read(buffer, block => ReadDirectory(directory, block),
            beforeRead, afterRead, maximumNames, maximumBlocks, acceptName, reject);

    /// <summary>Reads through actual EOF, rejecting cross-block duplicates and the caller's original bounds.</summary>
    /// <param name="buffer">The caller's original bounded nonempty getdents buffer.</param>
    /// <param name="readBlock">The original synchronous native read, returning bytes written into that buffer.</param>
    /// <param name="beforeRead">Original cancellation/deadline check, including the EOF iteration.</param>
    /// <param name="afterRead">Original post-read check, before inspecting EOF or decoding any returned names.</param>
    /// <param name="maximumNames">Original total unique-name ceiling; zero permits only an empty directory.</param>
    /// <param name="maximumBlocks">Original read-attempt ceiling, including EOF, or null when none existed.</param>
    /// <param name="acceptName">Optional original exact-inventory predicate, checked after reserving each unique name.</param>
    /// <param name="reject">Original fixed exception factory for name, inventory or read-attempt bounds.</param>
    /// <returns>The original unique names sorted with ordinal comparison, after the EOF callback completes.</returns>
    /// <remarks>Read/check/decoder exceptions propagate unchanged; no partial inventory is returned.</remarks>
    internal static string[] Read(byte[] buffer, Func<byte[], int> readBlock, Action beforeRead,
        Action afterRead, int maximumNames, int? maximumBlocks, Func<string, bool>? acceptName,
        Func<Exception> reject)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(readBlock);
        ArgumentNullException.ThrowIfNull(beforeRead);
        ArgumentNullException.ThrowIfNull(afterRead);
        ArgumentNullException.ThrowIfNull(reject);
        if (buffer.Length is < 1 or > 32 * 1024) throw new ArgumentOutOfRangeException(nameof(buffer));
        if (maximumNames < 0) throw new ArgumentOutOfRangeException(nameof(maximumNames));
        if (maximumBlocks is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBlocks));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var blocks = 0;
        while (true)
        {
            beforeRead();
            if (maximumBlocks is { } bound && ++blocks > bound) throw reject();
            var count = readBlock(buffer);
            afterRead();
            if (count == 0) break;
            foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, count)))
                if (names.Count >= maximumNames || !names.Add(name)
                    || acceptName is not null && !acceptName(name)) throw reject();
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
}
