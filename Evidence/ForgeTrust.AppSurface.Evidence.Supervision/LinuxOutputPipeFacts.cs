namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Data-only policy for observations obtained by LinuxOutputPipes.CreatePair.</summary>
/// <remarks>
/// Production supplies these observations only after pipe2, fcntl and stat acquisition. Validation stays
/// split at those boundaries so invalid descriptors or flags reject before metadata acquisition. This
/// policy performs no syscall and creates no handle, LinuxOutputPipes instance or native authority.
/// Copied observations in portable tests cannot authenticate a process, account, admission, unit or pipe.
/// </remarks>
internal static class LinuxOutputPipeFactsPolicy
{
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;
    private const int AccessMask = 3;
    private const int CloseOnExec = 1;
    private const ushort FifoType = 0x1000;

    /// <summary>Validates only the two raw descriptor facts immediately after pipe2 returns.</summary>
    internal static void ValidateDescriptors(int readDescriptor, int writeDescriptor)
    {
        if (readDescriptor < 0 || writeDescriptor < 0 || readDescriptor == writeDescriptor) throw Invalid();
    }

    /// <summary>Validates fcntl access and close-on-exec observations before stat metadata is consumed.</summary>
    internal static void ValidateFlags(int readAccessFlags, int writeAccessFlags,
        int readDescriptorFlags, int writeDescriptorFlags)
    {
        if (readAccessFlags < 0 || writeAccessFlags < 0
            || (readAccessFlags & AccessMask) != ReadOnly || (writeAccessFlags & AccessMask) != WriteOnly
            || readDescriptorFlags < 0 || writeDescriptorFlags < 0
            || (readDescriptorFlags & CloseOnExec) != CloseOnExec
            || (writeDescriptorFlags & CloseOnExec) != CloseOnExec) throw Invalid();
    }

    /// <summary>Validates both actual stat samples as one root-owned FIFO pair.</summary>
    internal static void ValidateMetadata(LinuxProtectedMetadata readMetadata, LinuxProtectedMetadata writeMetadata)
    {
        if ((readMetadata.Mode & 0xf000) != FifoType || (writeMetadata.Mode & 0xf000) != FifoType
            || readMetadata.Inode == 0 || readMetadata.DeviceMajor != writeMetadata.DeviceMajor
            || readMetadata.DeviceMinor != writeMetadata.DeviceMinor || readMetadata.Inode != writeMetadata.Inode
            || readMetadata.Uid != 0 || readMetadata.Gid != 0
            || writeMetadata.Uid != 0 || writeMetadata.Gid != 0) throw Invalid();
    }

    private static SupervisionOutputPipeException Invalid() =>
        new(SupervisionOutputPipeFailure.InvalidPipe);
}
