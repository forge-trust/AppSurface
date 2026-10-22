using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RazorWireWebExample.Models;

/// <summary>Consumer-owned rendering model for the nested action/date sample.</summary>
/// <remarks>Posted explicit-index keys are paired with bound rows in marker order. Keys are rendering identities, never persisted IDs.</remarks>
public sealed class NestedCollectionsSampleModel
{
    /// <summary>Gets the bound actions in posted marker order.</summary>
    public List<NestedActionModel> Actions { get; } = [];
    /// <summary>Gets or sets the sample's removal policy.</summary>
    public string RemoveMode { get; set; } = "physical";
    /// <summary>Gets or sets the successful submission result.</summary>
    [BindNever]
    public string? Result { get; set; }
}

/// <summary>An application-authored action with an independently indexed date collection.</summary>
public sealed class NestedActionModel
{
    /// <summary>Gets or sets the preserved explicit-index rendering key.</summary>
    public string ClientIndex { get; set; } = string.Empty;
    /// <summary>Gets or sets a nullable persisted identity; duplication clears it.</summary>
    public string? Id { get; set; }
    /// <summary>Gets or sets a concurrency value; duplication clears it.</summary>
    public string? RowVersion { get; set; }
    /// <summary>Gets or sets the application-owned deletion flag.</summary>
    public bool Delete { get; set; }
    /// <summary>Gets or sets the action title.</summary>
    public string? Title { get; set; }
    /// <summary>Gets the bound dates in their parent's posted marker order.</summary>
    public List<NestedDateModel> Dates { get; } = [];
}

/// <summary>An application-authored date; validation and deletion remain consumer policy.</summary>
public sealed class NestedDateModel
{
    /// <summary>Gets or sets the preserved explicit-index rendering key.</summary>
    public string ClientIndex { get; set; } = string.Empty;
    /// <summary>Gets or sets a nullable persisted identity.</summary>
    public string? Id { get; set; }
    /// <summary>Gets or sets a concurrency value.</summary>
    public string? ConcurrencyToken { get; set; }
    /// <summary>Gets or sets the application-owned deletion flag.</summary>
    public bool Delete { get; set; }
    /// <summary>Gets or sets the date kind; the application requires a value for live dates.</summary>
    public string? Kind { get; set; }
    /// <summary>Gets or sets the date; failed conversions remain in ModelState for redisplay.</summary>
    public DateOnly? Date { get; set; }
}
