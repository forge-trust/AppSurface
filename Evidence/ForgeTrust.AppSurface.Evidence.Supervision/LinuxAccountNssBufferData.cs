using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Bounds-only decoding of pointers returned into an authentic caller-owned NSS buffer.</summary>
/// <remarks>This helper owns no pointer, performs no NSS or kernel call, and grants no account authority.
/// The address must point into the retained buffer supplied by the same native NSS call until decoding ends.</remarks>
internal static unsafe class LinuxAccountNssBufferData
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Decodes one NUL-terminated strict UTF-8 value within the retained NSS buffer.</summary>
    /// <param name="address">A pointer returned by the active NSS call into <paramref name="buffer"/>.</param>
    /// <param name="buffer">The address of the pinned caller-owned NSS result buffer.</param>
    /// <param name="size">The exact allocated buffer size.</param>
    /// <param name="maximum">The closed field-specific byte limit before the NUL terminator.</param>
    /// <returns>The decoded value without the terminator.</returns>
    /// <exception cref="LinuxRunAccountException">Thrown as the fixed <see cref="LinuxRunAccountFailure.NssFailed"/> diagnostic for an invalid pointer, bound, terminator, or UTF-8 sequence.</exception>
    internal static string ReadString(nint address, nint buffer, int size, int maximum)
    {
        if (!Contains(address, 1, buffer, size)) throw NssFailed();
        var available = (int)((nuint)buffer + (nuint)size - (nuint)address);
        var bytes = new ReadOnlySpan<byte>((void*)address, Math.Min(available, maximum + 1));
        var length = bytes.IndexOf((byte)0);
        if (length < 0 || length > maximum) throw NssFailed();
        try { return StrictUtf8.GetString(bytes[..length]); }
        catch (DecoderFallbackException) { throw NssFailed(); }
    }

    /// <summary>Checks that a native pointer range is wholly inside the retained caller buffer.</summary>
    /// <remarks>This is a pointer arithmetic predicate only; it does not dereference or authenticate the range.</remarks>
    internal static bool Contains(nint address, int length, nint buffer, int size) =>
        (nuint)address >= (nuint)buffer && (nuint)address <= (nuint)buffer + (nuint)size - (nuint)length;

    private static LinuxRunAccountException NssFailed() => new(LinuxRunAccountFailure.NssFailed);
}
