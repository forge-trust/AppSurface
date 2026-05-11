namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Describes how AppSurface console apps should balance command-owned output against ambient host lifecycle diagnostics.
/// </summary>
public enum ConsoleOutputMode
{
    /// <summary>
    /// Uses the default Generic Host console behavior, which may include lifecycle information alongside command output.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Treats the command as the public output owner, suppressing ambient host and command-runner lifecycle information.
    /// </summary>
    /// <remarks>
    /// Use this mode for public CLI tools where help, validation, and progress output should come from commands rather than
    /// startup and shutdown banners.
    /// </remarks>
    CommandFirst = 1
}
