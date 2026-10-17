using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RazorWireWebExample.Models;

/// <summary>
/// Consumer-owned form model for the version-bound Skoolit V4 action/date proof.
/// </summary>
/// <remarks>
/// The form uses ASP.NET Core explicit collection indexes. The consumer-rendered <c>ClientIndex</c> values are
/// independent of persisted identifiers. After ordinary binding, the bounded controller adapter copies each canonical
/// marker key onto the matching bound row in posted order so validation paths and rendered names stay aligned. It
/// rejects missing, duplicate, blank, or count-mismatched markers and never substitutes list ordinals or clears
/// ModelState.
/// </remarks>
public sealed class SkoolitV4CollectionEditorModel
{
    /// <summary>Gets or sets action rows in the order produced by ASP.NET Core's explicit-index binder.</summary>
    public List<SkoolitV4CollectionActionModel> Actions { get; set; } = [];

    /// <summary>Gets or sets the consumer's row-removal policy, either <c>physical</c> or <c>mark</c>.</summary>
    public string RemoveMode { get; set; } = "physical";

    /// <summary>Gets or sets the consumer-owned result shown after an accepted ordinary form submission.</summary>
    [BindNever]
    public string? Result { get; set; }
}

/// <summary>An action row whose nested date keys are owned by that action's date collection.</summary>
public sealed class SkoolitV4CollectionActionModel
{
    /// <summary>Gets or sets the consumer's rendering key carried with this action row.</summary>
    public string ClientIndex { get; set; } = string.Empty;

    /// <summary>Gets or sets the nullable persisted action identity, which duplication clears.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the concurrency value, which duplication clears.</summary>
    public string? RowVersion { get; set; }

    /// <summary>Gets or sets the consumer-owned deletion marker.</summary>
    public bool Delete { get; set; }

    /// <summary>Gets or sets the action title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets this action's dates in the order produced by its own posted date markers.</summary>
    public List<SkoolitV4CollectionDateModel> Dates { get; set; } = [];

    /// <summary>Gets a safe, request-local prefix for accessible field and error-description IDs.</summary>
    [BindNever]
    public string DomId { get; } = $"skoolit-v4-action-{Guid.NewGuid():N}";
}

/// <summary>A V4 date editor row, retaining source date parts rather than inferring a date or year.</summary>
public sealed class SkoolitV4CollectionDateModel
{
    /// <summary>Gets or sets the consumer's rendering key carried with this date row.</summary>
    public string ClientIndex { get; set; } = string.Empty;

    /// <summary>Gets or sets the consumer rendering key of the owning action, carried with this date row.</summary>
    public string ActionClientIndex { get; set; } = string.Empty;

    /// <summary>Gets or sets the nullable persisted date identity, which action duplication clears.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the concurrency value, which action duplication clears.</summary>
    public string? ConcurrencyToken { get; set; }

    /// <summary>Gets or sets the consumer-owned deletion marker.</summary>
    public bool Delete { get; set; }

    /// <summary>Gets or sets the V4 date kind: <c>eventStart</c>, <c>dueBy</c>, or <c>overdueSince</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the V4 date shape: <c>Discrete</c> or <c>Range</c>.</summary>
    public string? Shape { get; set; }

    /// <summary>Gets or sets the source start month without applying a default year.</summary>
    public string? StartMonth { get; set; }

    /// <summary>Gets or sets the source start day without applying a default year.</summary>
    public string? StartDay { get; set; }

    /// <summary>Gets or sets the optional source start year; blank means the source did not state a year.</summary>
    public string? StartYear { get; set; }

    /// <summary>Gets or sets the optional range end month.</summary>
    public string? EndMonth { get; set; }

    /// <summary>Gets or sets the optional range end day.</summary>
    public string? EndDay { get; set; }

    /// <summary>Gets or sets the optional range end year; blank means the source did not state an end year.</summary>
    public string? EndYear { get; set; }

    /// <summary>Gets or sets the source start time.</summary>
    public string? StartTime { get; set; }

    /// <summary>Gets or sets the source end time.</summary>
    public string? EndTime { get; set; }

    /// <summary>Gets or sets the source time-zone identifier.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Gets the consumer's saved source parts used to distinguish an unchanged date from an unsaved draft.</summary>
    [BindNever]
    public SkoolitV4CollectionSavedDate? SavedDate { get; set; }

    /// <summary>Gets or sets the consumer-rendered temporal result or unsaved-draft feedback.</summary>
    /// <remarks>Populated dates without a saved identity remain unsaved across submissions; an empty newly added date retains its initial entry prompt.</remarks>
    [BindNever]
    public string TemporalFeedback { get; set; } = "Enter the source month and day. The year is optional and should remain blank when the source omits it.";

    /// <summary>Gets a safe, request-local prefix for accessible field and error-description IDs.</summary>
    [BindNever]
    public string DomId { get; } = $"skoolit-v4-date-{Guid.NewGuid():N}";
}

/// <summary>Consumer-owned saved source parts used by the fixture's temporal feedback.</summary>
public sealed record SkoolitV4CollectionSavedDate(
    string? Kind,
    string? Shape,
    string? StartMonth,
    string? StartDay,
    string? StartYear,
    string? EndMonth,
    string? EndDay,
    string? EndYear,
    string? StartTime,
    string? EndTime,
    string? TimeZone);
