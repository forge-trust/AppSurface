using System.ComponentModel.DataAnnotations;

namespace RazorWireWebExample.Models;

/// <summary>
/// View model for the RazorWire form-interactions sample.
/// </summary>
public sealed class FormInteractionsSampleModel
{
    /// <summary>
    /// Gets or sets a value indicating whether the reviewer expects no follow-up action.
    /// </summary>
    public bool ExpectedNoAction { get; set; }

    /// <summary>
    /// Gets the one-dimensional model-bound action rows submitted by the sample form.
    /// </summary>
    public List<FormInteractionActionModel> Actions { get; } = [];

    /// <summary>
    /// Gets or sets the server-rendered result shown after a valid submission.
    /// </summary>
    public string? Result { get; set; }
}

/// <summary>
/// One model-bound row in the RazorWire form-interactions sample.
/// </summary>
public sealed class FormInteractionActionModel
{
    /// <summary>
    /// Gets or sets the client-side sparse index token used to re-render failed validation without renumbering.
    /// </summary>
    public string? ClientIndex { get; set; }

    /// <summary>
    /// Gets or sets the persisted action identifier. Empty values represent unsaved draft rows.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server should delete this persisted row.
    /// </summary>
    public bool Delete { get; set; }

    /// <summary>
    /// Gets or sets the action title, limited to 40 characters.
    /// </summary>
    [StringLength(40)]
    public string? Title { get; set; }
}
