namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Defines the input syntax shared by candidate feed preparation and admission.</summary>
internal static class PackedCandidateIdentity
{
    /// <summary>Checks the bounded characters supported by the exact candidate version.</summary>
    internal static bool IsVersion(string version)
        => version.Length is > 0 and <= 128 && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>Checks the hexadecimal spelling of a full SHA-1 or SHA-256 source commit.</summary>
    internal static bool IsSourceCommit(string commit)
        => commit.Length is 40 or 64 && commit.All(char.IsAsciiHexDigit);
}
