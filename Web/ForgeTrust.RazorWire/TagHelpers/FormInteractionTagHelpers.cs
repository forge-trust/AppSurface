using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Marks an application-authored form control as a RazorWire conditional form toggle.
/// </summary>
/// <remarks>
/// Use this helper when Razor markup should normalize to the canonical
/// <c>data-rw-form-toggle</c> attribute. RazorWire interprets null, blank,
/// <c>true</c>, and the sentinel <c>rw-form-toggle</c> value as the default
/// logical name <c>true</c>. When applied to a <c>button</c>, the helper sets
/// <c>type="button"</c> when the app has not supplied a type so toggles do not
/// submit forms by default. The helper does not move focus, validate business
/// rules, or generate target markup; the app owns the control and target.
/// </remarks>
[HtmlTargetElement(Attributes = "rw-form-toggle")]
public sealed class FormToggleTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the logical target name this control toggles.
    /// </summary>
    /// <remarks>
    /// Values are trimmed. Blank, <c>true</c>, and <c>rw-form-toggle</c>
    /// normalize to <c>true</c> so attribute-only Razor usage has a stable
    /// default name.
    /// </remarks>
    [HtmlAttributeName("rw-form-toggle")]
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the control's active state should be inverted.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false" />. Set this for controls such as
    /// "no action expected" checkboxes where the checked state should hide the
    /// target instead of showing it.
    /// </remarks>
    [HtmlAttributeName("rw-form-toggle-invert")]
    public bool Invert { get; set; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-toggle");
        output.Attributes.RemoveAll("rw-form-toggle-invert");

        var targetName = NormalizeName(TargetName, "rw-form-toggle");
        output.Attributes.SetAttribute("data-rw-form-toggle", targetName);
        if (Invert)
        {
            output.Attributes.SetAttribute("data-rw-form-toggle-invert", "true");
        }

        EnsureButtonType(output);
    }

    private static string NormalizeName(string? value, string sentinel)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
               || string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, sentinel, StringComparison.OrdinalIgnoreCase)
            ? "true"
            : normalized;
    }

    private static void EnsureButtonType(TagHelperOutput output)
    {
        if (string.Equals(output.TagName, "button", StringComparison.OrdinalIgnoreCase)
            && !output.Attributes.ContainsName("type"))
        {
            output.Attributes.SetAttribute("type", "button");
        }
    }
}

/// <summary>
/// Marks an application-authored element as the target controlled by a RazorWire conditional form toggle.
/// </summary>
/// <remarks>
/// The helper emits only <c>data-rw-form-toggle-target</c> metadata. Target
/// contents, model fields, validation messages, and layout remain app-owned.
/// Null, blank, <c>true</c>, and <c>rw-form-toggle-target</c> normalize to the
/// default logical name <c>true</c>.
/// </remarks>
[HtmlTargetElement(Attributes = "rw-form-toggle-target")]
public sealed class FormToggleTargetTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the logical toggle name that controls this target.
    /// </summary>
    /// <remarks>
    /// Values are trimmed and normalized to <c>true</c> for attribute-only
    /// usage. The value must match the toggle name inside the same form.
    /// </remarks>
    [HtmlAttributeName("rw-form-toggle-target")]
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether descendant controls should be disabled while the target is hidden.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false" />. When enabled, RazorWire
    /// disables only controls it owns during the hidden state and restores only
    /// those controls when the target is shown again.
    /// </remarks>
    [HtmlAttributeName("rw-form-toggle-disable-when-hidden")]
    public bool DisableWhenHidden { get; set; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-toggle-target");
        output.Attributes.RemoveAll("rw-form-toggle-disable-when-hidden");

        output.Attributes.SetAttribute("data-rw-form-toggle-target", NormalizeName(TargetName, "rw-form-toggle-target"));
        if (DisableWhenHidden)
        {
            output.Attributes.SetAttribute("data-rw-form-toggle-disable-when-hidden", "true");
        }
    }

    private static string NormalizeName(string? value, string sentinel)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
               || string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, sentinel, StringComparison.OrdinalIgnoreCase)
            ? "true"
            : normalized;
    }
}

/// <summary>
/// Marks an application-authored element as a one-dimensional model-bound collection root.
/// </summary>
/// <remarks>
/// The collection root scopes row commands, templates, sparse index markers,
/// and diagnostics for one ASP.NET Core collection property. RazorWire stable
/// v1 intentionally does not support nested collections, reordering, generated
/// model fields, or app persistence decisions.
/// </remarks>
[HtmlTargetElement(Attributes = "rw-form-collection")]
public sealed class FormCollectionTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the ASP.NET Core model property name for the collection, such as <c>Actions</c>.
    /// </summary>
    /// <remarks>
    /// The value is trimmed and emitted as <c>data-rw-form-collection</c>.
    /// It must match the prefix used by app-authored field names and hidden
    /// <c>.index</c> markers.
    /// </remarks>
    [HtmlAttributeName("rw-form-collection")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets optional reader-facing copy for generated announcements.
    /// </summary>
    /// <remarks>
    /// The value is used only for local status messages such as add, duplicate,
    /// and remove announcements. It does not change model binding names.
    /// </remarks>
    [HtmlAttributeName("rw-form-collection-label")]
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the default remove mode for row commands inside this collection.
    /// </summary>
    /// <remarks>
    /// Values are passed through after trimming. The runtime recognizes
    /// <c>mark</c> and <c>mark-remove</c> as mark-for-removal; other values fall
    /// back to physical remove behavior.
    /// </remarks>
    [HtmlAttributeName("rw-form-collection-remove-mode")]
    public string? RemoveMode { get; set; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-collection");
        output.Attributes.RemoveAll("rw-form-collection-label");
        output.Attributes.RemoveAll("rw-form-collection-remove-mode");

        output.Attributes.SetAttribute("data-rw-form-collection", Name?.Trim() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(Label))
        {
            output.Attributes.SetAttribute("data-rw-form-collection-label", Label.Trim());
        }

        if (!string.IsNullOrWhiteSpace(RemoveMode))
        {
            output.Attributes.SetAttribute("data-rw-form-collection-remove-mode", RemoveMode.Trim());
        }
    }
}

/// <summary>
/// Marks an application-authored element as a row inside a RazorWire form collection.
/// </summary>
/// <remarks>
/// Rows must contain app-owned hidden <c>Collection.index</c> markers for ASP.NET
/// Core sparse collection binding. The helper marks the row and may emit the
/// optional current index, but it does not create hidden markers or generated
/// input fields.
/// </remarks>
[HtmlTargetElement(Attributes = "rw-form-collection-row")]
public sealed class FormCollectionRowTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the row's current model-binding index.
    /// </summary>
    /// <remarks>
    /// When provided, the trimmed value is emitted as <c>data-rw-form-index</c>.
    /// Keep it aligned with the row's enabled hidden <c>.index</c> marker.
    /// </remarks>
    [HtmlAttributeName("rw-form-index")]
    public string? Index { get; set; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-collection-row");
        output.Attributes.RemoveAll("rw-form-index");
        output.Attributes.SetAttribute("data-rw-form-collection-row", "true");
        if (!string.IsNullOrWhiteSpace(Index))
        {
            output.Attributes.SetAttribute("data-rw-form-index", Index.Trim());
        }
    }
}

/// <summary>
/// Marks a template containing app-authored collection row markup.
/// </summary>
/// <remarks>
/// The template must contain the literal <c>__index__</c> token in row field
/// names, ids, label references, validation references, and the hidden
/// <c>.index</c> value. RazorWire clones and rewrites this markup but does not
/// invent app fields.
/// </remarks>
[HtmlTargetElement("template", Attributes = "rw-form-collection-template")]
public sealed class FormCollectionTemplateTagHelper : TagHelper
{
    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-collection-template");
        output.Attributes.SetAttribute("data-rw-form-collection-template", "true");
    }
}

/// <summary>
/// Marks a button that adds a row from the nearest collection template.
/// </summary>
/// <remarks>
/// The helper emits <c>data-rw-form-collection-add</c> and sets
/// <c>type="button"</c> when the app has not supplied a type. The nearest
/// collection root and template determine what row is inserted.
/// </remarks>
[HtmlTargetElement("button", Attributes = "rw-form-collection-add")]
public sealed class FormCollectionAddTagHelper : TagHelper
{
    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-collection-add");
        output.Attributes.SetAttribute("data-rw-form-collection-add", "true");
        EnsureButtonType(output);
    }

    private static void EnsureButtonType(TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("type"))
        {
            output.Attributes.SetAttribute("type", "button");
        }
    }
}

/// <summary>
/// Marks a button that duplicates the nearest collection row.
/// </summary>
/// <remarks>
/// The helper emits <c>data-rw-form-collection-duplicate</c> and sets
/// <c>type="button"</c> when absent. Duplicate requires the source row to have a
/// resolvable sparse index so names, ids, labels, and validation references can
/// be rewritten safely.
/// </remarks>
[HtmlTargetElement("button", Attributes = "rw-form-collection-duplicate")]
public sealed class FormCollectionDuplicateTagHelper : TagHelper
{
    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-collection-duplicate");
        output.Attributes.SetAttribute("data-rw-form-collection-duplicate", "true");
        EnsureButtonType(output);
    }

    private static void EnsureButtonType(TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("type"))
        {
            output.Attributes.SetAttribute("type", "button");
        }
    }
}

/// <summary>
/// Marks a button that removes or marks the nearest collection row.
/// </summary>
/// <remarks>
/// Use physical remove for draft-only rows whose absence from the payload is
/// enough. Use mark-for-removal for persisted rows when the app provides hidden
/// id/delete fields and server-side delete semantics. The helper never creates
/// those app-owned persistence fields.
/// </remarks>
[HtmlTargetElement("button", Attributes = "rw-form-collection-remove")]
public sealed class FormCollectionRemoveTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the remove mode for this command: <c>physical</c> or <c>mark</c>.
    /// </summary>
    /// <remarks>
    /// Null, blank, <c>true</c>, and <c>rw-form-collection-remove</c> normalize
    /// to <c>physical</c>. The runtime also accepts <c>mark-remove</c> as a
    /// mark-for-removal command value.
    /// </remarks>
    [HtmlAttributeName("rw-form-collection-remove")]
    public string? Mode { get; set; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("rw-form-collection-remove");
        output.Attributes.SetAttribute("data-rw-form-collection-remove", NormalizeMode(Mode));
        EnsureButtonType(output);
    }

    private static string NormalizeMode(string? mode)
    {
        var normalized = mode?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
               || string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "rw-form-collection-remove", StringComparison.OrdinalIgnoreCase)
            ? "physical"
            : normalized;
    }

    private static void EnsureButtonType(TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("type"))
        {
            output.Attributes.SetAttribute("type", "button");
        }
    }
}
