using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Marks an application-authored form control as a RazorWire conditional form toggle.
/// </summary>
[HtmlTargetElement(Attributes = "rw-form-toggle")]
public sealed class FormToggleTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the logical target name this control toggles.
    /// </summary>
    [HtmlAttributeName("rw-form-toggle")]
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the control's active state should be inverted.
    /// </summary>
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
/// Marks an application-authored element as the target controlled by a RazorWire conditional form toggle.
/// </summary>
[HtmlTargetElement(Attributes = "rw-form-toggle-target")]
public sealed class FormToggleTargetTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the logical toggle name that controls this target.
    /// </summary>
    [HtmlAttributeName("rw-form-toggle-target")]
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether descendant controls should be disabled while the target is hidden.
    /// </summary>
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
[HtmlTargetElement(Attributes = "rw-form-collection")]
public sealed class FormCollectionTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the ASP.NET Core model property name for the collection, such as <c>Actions</c>.
    /// </summary>
    [HtmlAttributeName("rw-form-collection")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets optional reader-facing copy for generated announcements.
    /// </summary>
    [HtmlAttributeName("rw-form-collection-label")]
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the default remove mode for row commands inside this collection.
    /// </summary>
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
[HtmlTargetElement(Attributes = "rw-form-collection-row")]
public sealed class FormCollectionRowTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the row's current model-binding index.
    /// </summary>
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
[HtmlTargetElement("button", Attributes = "rw-form-collection-remove")]
public sealed class FormCollectionRemoveTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the remove mode for this command: <c>physical</c> or <c>mark</c>.
    /// </summary>
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
