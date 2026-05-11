using System.Collections.Generic;

namespace ForgeTrust.AppSurface.Console;

/// <summary>
/// Defines a contract for suggesting alternative options when an unknown option is provided.
/// </summary>
public interface IOptionSuggester
{
    /// <summary>
    /// Gets a list of suggested options based on the unknown option and the list of valid options.
    /// </summary>
    /// <remarks>
    /// <see cref="GetSuggestions"/> implementations should treat a null or empty <paramref name="unknownOption"/>, or a
    /// null or empty <paramref name="validOptions"/> sequence, as producing no suggestions. The returned
    /// <see cref="IReadOnlyList{T}"/> must be non-null, should not contain duplicates, and should be deterministic:
    /// relevance-first when a scorer exists, then alphabetical for ties.
    /// </remarks>
    /// <param name="unknownOption">The unknown option provided by the user, or <see langword="null"/> when parsing did not identify one.</param>
    /// <param name="validOptions">The valid options for the current command, or <see langword="null"/> when none are available.</param>
    /// <returns>A non-null collection of suggested options.</returns>
    IReadOnlyList<string> GetSuggestions(string? unknownOption, IEnumerable<string>? validOptions);
}
