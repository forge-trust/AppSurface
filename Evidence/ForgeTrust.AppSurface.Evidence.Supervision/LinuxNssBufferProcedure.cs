namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Runs one bounded synchronous NSS lookup through its fixed retryable result-buffer sizes.</summary>
/// <remarks>
/// The callback owns the actual native call and any parsing of a successful non-null result. For a nonzero
/// code, its value is ignored and it must not decode native fields. For code zero, a null value denotes an
/// actual null result pointer.
/// This procedure owns no account, platform check, token, pointer, or native capability. Only Linux ERANGE
/// (34) retries; all other errors and exhausted retries use the caller's fixed failure factory.
/// </remarks>
internal static class LinuxNssBufferProcedure
{
    private const int InitialBufferBytes = 1024;
    private const int MaximumBufferBytes = 65_536;
    private const int LinuxRangeError = 34;

    /// <summary>Allocates each original doubling buffer and returns the first successful parsed result.</summary>
    /// <typeparam name="T">Detached parsed account data; no ownership or authority object.</typeparam>
    /// <param name="lookup">One synchronous native lookup and success-only parser for the supplied buffer.</param>
    /// <param name="failure">The caller's fixed closed failure factory.</param>
    /// <returns>The parsed data, or null only when the native lookup succeeded with a null result pointer.</returns>
    internal static T? Execute<T>(Func<byte[], (int Code, T? Value)> lookup, Func<Exception> failure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(failure);

        for (var size = InitialBufferBytes; size <= MaximumBufferBytes; size *= 2)
        {
            var (code, value) = lookup(new byte[size]);
            if (code == LinuxRangeError) continue;
            if (code != 0) throw failure();
            return value;
        }

        throw failure();
    }
}
