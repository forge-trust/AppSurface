namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Assembles one already selected report range from the original bounded reader.</summary>
/// <remarks>This procedure owns no file, path, admission or report custody. Native selection and range
/// validation remain in <see cref="LinuxProducerArtifactCustody"/> before dispatch. Successful byte
/// assembly is not report authenticity, accepted coverage, or successful producer execution.</remarks>
internal static class SupervisionReportChunkTransfer
{
    /// <summary>Joins the original 64 KiB reads for one validated range, preserving every work check.</summary>
    /// <param name="reportLength">The nonnegative length of the original retained inventory row.</param>
    /// <param name="offset">The caller-validated next offset; zero for an empty report.</param>
    /// <param name="maximumBytes">The original validated request bound, from one byte through 128 KiB.</param>
    /// <param name="checkWork">The native caller's original owner, account, workspace and deadline check.</param>
    /// <param name="read">The original retained set's read operation, bound to the selected path and token.</param>
    /// <returns>Only the joined requested bytes, after the final work check succeeds.</returns>
    /// <remarks>Only the internal native caller supplies the two operations. An empty report still requires
    /// its original reader's single EOF check. Nonempty reads require each complete requested piece and
    /// never retry after a short read or exception. The enclosing native holder retains its lock and sticky
    /// failure catch. Callbacks, cancellation, reads and final checks are synchronous and remain joined.</remarks>
    internal static byte[] ReadValidated(long reportLength, long offset, int maximumBytes,
        Action checkWork, Func<long, int, byte[]> read)
    {
        ArgumentNullException.ThrowIfNull(checkWork);
        ArgumentNullException.ThrowIfNull(read);
        if (reportLength == 0)
        {
            var empty = read(0, Math.Min(maximumBytes, LinuxRetainedReportSet.MaximumChunkBytes));
            checkWork();
            return empty;
        }

        var length = checked((int)Math.Min(maximumBytes, reportLength - offset));
        var result = new byte[length];
        var copied = 0;
        while (copied < result.Length)
        {
            checkWork();
            var requested = Math.Min(LinuxRetainedReportSet.MaximumChunkBytes, result.Length - copied);
            var piece = read(offset + copied, requested);
            if (piece.Length != requested) throw new IOException("The producer report set is unavailable or changed.");
            piece.CopyTo(result, copied);
            copied += piece.Length;
        }
        checkWork();
        return result;
    }
}
