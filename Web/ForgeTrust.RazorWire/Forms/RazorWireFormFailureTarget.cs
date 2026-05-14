namespace ForgeTrust.RazorWire.Forms;

/// <summary>
/// Normalizes form failure targets that can be addressed safely by server-generated Turbo Streams.
/// </summary>
internal static class RazorWireFormFailureTarget
{
    /// <summary>
    /// Attempts to normalize a simple element-id target for a failed RazorWire form.
    /// </summary>
    /// <param name="failureTarget">
    /// The value supplied by <c>data-rw-form-failure-target</c>. A leading <c>#</c> is accepted and removed.
    /// </param>
    /// <param name="normalized">When this method returns <c>true</c>, contains the id without a leading <c>#</c>.</param>
    /// <returns>
    /// <c>true</c> when <paramref name="failureTarget"/> is a non-empty element id; otherwise <c>false</c>.
    /// CSS selector-like values that start with <c>.</c> or <c>[</c>, and values containing whitespace, are rejected
    /// because server-generated Turbo Stream <c>target</c> attributes address one element id rather than arbitrary selectors.
    /// </returns>
    public static bool TryNormalizeIdTarget(string? failureTarget, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(failureTarget))
        {
            return false;
        }

        var trimmed = failureTarget.Trim();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Any(char.IsWhiteSpace) || trimmed[0] is '.' or '[')
        {
            return false;
        }

        normalized = trimmed;

        return true;
    }
}
