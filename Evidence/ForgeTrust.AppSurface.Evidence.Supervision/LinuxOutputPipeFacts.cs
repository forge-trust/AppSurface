namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Copied observations from one actual native pipe pair; this value creates no handle or authority.</summary>
/// <remarks>
/// Production code supplies these fields only after pipe2, fcntl and stat acquisition. The policy is split at
/// those acquisition boundaries so a failed descriptor or flag syscall is reported before any metadata is read.
/// Tests may copy observations into this value to exercise rejection rules, but cannot create a LinuxOutputPipes
/// instance or authenticate a process, account, admission, unit or output transport.
/// </remarks>
internal readonly record struct LinuxOutputPipeFacts(
    int ReadDescriptor,
    int WriteDescriptor,
    int ReadAccessFlags,
    int WriteAccessFlags,
    int ReadDescriptorFlags,
    int WriteDescriptorFlags,
    LinuxProtectedMetadata ReadMetadata,
    LinuxProtectedMetadata WriteMetadata);

/// <summary>Data-only policy for observations obtained by LinuxOutputPipes.CreatePair.</summary>
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
