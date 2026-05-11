using ForgeTrust.RazorWire.Forms;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// A Tag Helper that enhances a standard <c>&lt;form&gt;</c> element with RazorWire/Turbo features.
/// </summary>
[HtmlTargetElement("form", Attributes = "rw-active")]
public class RazorWireFormTagHelper : TagHelper
{
    private readonly RazorWireOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorWireFormTagHelper"/> class.
    /// </summary>
    /// <param name="options">RazorWire options used to determine failed-form defaults.</param>
    public RazorWireFormTagHelper(RazorWireOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Gets or sets a value indicating whether RazorWire/Turbo enhancement is enabled for this form.
    /// Defaults to <c>true</c>.
    /// </summary>
    [HtmlAttributeName("rw-active")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the identifier of the Turbo Frame that this form should target.
    /// </summary>
    [HtmlAttributeName("rw-target")]
    public string? TargetFrame { get; set; }


    /// <summary>
    /// Processes a form tag by removing attributes that start with "rw-" and configuring Turbo attributes based on the tag helper's properties.
    /// </summary>
    /// <param name="context">The context for the current tag helper execution.</param>
    /// <param name="output">The tag helper output whose attributes will be modified.</param>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var attributesToRemove = output.Attributes
            .Where(a => a.Name.StartsWith("rw-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var attr in attributesToRemove)
        {
            output.Attributes.Remove(attr);
        }

        if (!Enabled)
        {
            output.Attributes.SetAttribute("data-turbo", "false");

            return;
        }

        output.Attributes.SetAttribute("data-turbo", "true");

        if (!string.IsNullOrEmpty(TargetFrame))
        {
            output.Attributes.SetAttribute("data-turbo-frame", TargetFrame);
        }

        ApplyFormFailureConvention(output);
    }

    private void ApplyFormFailureConvention(TagHelperOutput output)
    {
        if (!_options.Forms.EnableFailureUx)
        {
            return;
        }

        var configuredMode = output.Attributes["data-rw-form-failure"]?.Value?.ToString();
        var mode = ResolveMode(configuredMode);
        if (mode == RazorWireFormFailureMode.Off)
        {
            return;
        }

        output.Attributes.SetAttribute("data-rw-form", "true");
        output.Attributes.SetAttribute("data-rw-form-failure", ToAttributeValue(mode));
        output.PostContent.AppendHtml(CreateHiddenInput(RazorWireFormFields.FormMarker, "1"));

        var failureTarget = output.Attributes["data-rw-form-failure-target"]?.Value?.ToString();
        if (RazorWireFormFailureTarget.TryNormalizeIdTarget(failureTarget, out var normalizedFailureTarget))
        {
            output.PostContent.AppendHtml(CreateHiddenInput(RazorWireFormFields.FailureTarget, normalizedFailureTarget));
        }
    }

    private RazorWireFormFailureMode ResolveMode(string? configuredMode)
    {
        if (string.Equals(configuredMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return RazorWireFormFailureMode.Auto;
        }

        if (string.Equals(configuredMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return RazorWireFormFailureMode.Manual;
        }

        if (string.Equals(configuredMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return RazorWireFormFailureMode.Off;
        }

        return _options.Forms.FailureMode;
    }

    private static string ToAttributeValue(RazorWireFormFailureMode mode)
    {
        return mode == RazorWireFormFailureMode.Manual ? "manual" : "auto";
    }

    private static TagBuilder CreateHiddenInput(string name, string value)
    {
        var input = new TagBuilder("input")
        {
            TagRenderMode = TagRenderMode.SelfClosing
        };
        input.Attributes["type"] = "hidden";
        input.Attributes["name"] = name;
        input.Attributes["value"] = value;

        return input;
    }
}
