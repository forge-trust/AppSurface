namespace ForgeTrust.AppSurface.Config;

/// <summary>Attaches stable, safe remediation metadata to framework-owned exceptions.</summary>
internal static class EnvironmentConfigCandidateErrors
{
    internal const string CandidateErrorsUri = "https://appsurface.dev/guides/config-logical-keys#candidate-errors";
    internal const string LogicalKeyGuideUri = "https://appsurface.dev/guides/config-logical-keys";

    /// <summary>Enriches the original known exception in place and returns that same object.</summary>
    /// <remarks>Call only at a known framework validation boundary; arbitrary host callbacks are not certified.</remarks>
    /// <typeparam name="T">Original exception type, retained without wrapping.</typeparam>
    /// <param name="exception">Original failure; its message, HResult, cause and origin stack are retained.</param>
    /// <param name="code">Stable framework failure identifier.</param>
    /// <param name="operation">Framework operation name, never caller input.</param>
    /// <param name="fix">Safe actionable repair containing no raw input or values.</param>
    /// <param name="recovery">Documented retry/rebuild disposition.</param>
    /// <param name="helpUri">Canonical error reference, also assigned to HelpLink.</param>
    /// <returns>The same exception instance.</returns>
    internal static T Enrich<T>(T exception, string code, string operation, string fix, string recovery, string helpUri)
        where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data["Code"] = code;
        exception.Data["Operation"] = operation;
        exception.Data["Fix"] = fix;
        exception.Data["Recovery"] = recovery;
        exception.Data["SideEffects"] = "none";
        exception.Data["HelpUri"] = helpUri;
        exception.HelpLink = helpUri;
        return exception;
    }

    internal static ArgumentNullException Null(string parameter, string operation) =>
        Enrich(new ArgumentNullException(parameter), "config-candidate-null", operation,
            $"Supply a non-null {parameter}.", "fix_and_retry", LogicalKeyGuideUri + "#config-candidate-null");

    /// <summary>Validates immutable public DTO inputs before storing or copying them.</summary>
    internal static T Require<T>(T? value, string parameter, string operation) where T : class =>
        value ?? throw Null(parameter, operation);

    /// <summary>Reports a null candidate element with safe, parameter-only remediation metadata.</summary>
    internal static ArgumentException NullEntry(string parameter, string operation) =>
        Enrich(new ArgumentException("Candidate entries cannot be null.", parameter), "config-candidate-null", operation,
            "Supply only non-null candidate entries.", "fix_and_retry", LogicalKeyGuideUri + "#config-candidate-null");
}
