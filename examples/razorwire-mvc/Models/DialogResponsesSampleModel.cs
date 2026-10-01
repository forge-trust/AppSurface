using System.ComponentModel.DataAnnotations;

namespace RazorWireWebExample.Models;

/// <summary>
/// Page state for the server-selected dialog response sample.
/// </summary>
public sealed class DialogResponsesSampleModel
{
    /// <summary>
    /// Gets or sets the latest page-level result message.
    /// </summary>
    public string ResultMessage { get; set; } = "No dialog response has been submitted yet.";

    /// <summary>
    /// Gets or sets the optional status result rendered inline by the HTML fallback.
    /// </summary>
    public DialogStatusSampleModel? Status { get; set; }

    /// <summary>
    /// Gets or sets the form values displayed by the HTML fallback.
    /// </summary>
    public DialogResponseFormModel Form { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the HTML fallback should show the follow-up form inline.
    /// </summary>
    public bool ShowDialogForm { get; set; }
}

/// <summary>
/// Status body rendered by the dialog partial and its full HTML fallback.
/// </summary>
public sealed class DialogStatusSampleModel
{
    /// <summary>
    /// Gets or sets the local sample status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text changed by the stream action queued after the dialog open.
    /// </summary>
    public string ProofMessage { get; set; } = string.Empty;
}

/// <summary>
/// Follow-up form state for the dialog response sample.
/// </summary>
public sealed class DialogResponseFormModel
{
    /// <summary>
    /// Gets or sets the submitted name, which must contain at least two characters and no more than forty characters.
    /// </summary>
    [Required(ErrorMessage = "Name is required.")]
    [MinLength(2, ErrorMessage = "Name must be at least 2 characters.")]
    [MaxLength(40, ErrorMessage = "Name must be 40 characters or fewer.")]
    public string? Name { get; set; }
}
