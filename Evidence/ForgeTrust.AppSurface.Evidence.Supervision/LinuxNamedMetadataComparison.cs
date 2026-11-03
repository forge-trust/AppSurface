namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Pure comparison of retained-node metadata with metadata sampled through its directory name.</summary>
/// <remarks>
/// The caller must open and stat the actual named entry first. This comparison neither performs that I/O
/// nor proves that either value came from the kernel. Directories compare stable ancestor identity and access
/// policy, ignoring child-list metadata; non-directories retain exact sampled-metadata equality.
/// </remarks>
internal static class LinuxNamedMetadataComparison
{
    /// <summary>Checks the original directory-identity or exact non-directory metadata rule.</summary>
    internal static bool Matches(LinuxProtectedMetadata actual, LinuxProtectedMetadata expected) =>
        (expected.Mode & 0xf000) == 0x4000
            ? actual.SameAncestorAs(expected)
            : actual == expected;
}
