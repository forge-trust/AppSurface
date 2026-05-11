using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Tag helper that renders a <c>&lt;time&gt;</c> element with a UTC timestamp for client-side time formatting.
/// </summary>
/// <remarks>
/// When <c>rw-type="local"</c>, the JavaScript runtime formats the timestamp to the user's local timezone.
/// When <c>rw-type="utc"</c>, the timestamp is formatted in UTC.
/// </remarks>
/// <example>
/// <code>
/// &lt;time rw-type="local" datetime="@Model.Timestamp"&gt;&lt;/time&gt;
/// &lt;time rw-type="local" datetime="@Model.Timestamp" rw-display="relative"&gt;&lt;/time&gt;
/// &lt;time rw-type="local" datetime="@Model.Timestamp" rw-display="datetime" rw-format="short"&gt;&lt;/time&gt;
/// &lt;time rw-type="utc" datetime="@Model.Timestamp"&gt;&lt;/time&gt;
/// </code>
/// </example>
[HtmlTargetElement("time", Attributes = "rw-type")]
public class LocalTimeTagHelper : TagHelper
{
    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "local",
        "utc"
    };

    private static readonly HashSet<string> ValidDisplayModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "time",
        "date",
        "datetime",
        "relative"
    };

    private static readonly HashSet<string> ValidFormatStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "short",
        "medium",
        "long",
        "full"
    };

    /// <summary>
    /// Gets or sets the timezone type for RazorWire time formatting.
    /// Valid values: <c>local</c> (user's browser timezone) or <c>utc</c> (UTC timezone).
    /// </summary>
    [HtmlAttributeName("rw-type")]
    public string? RwType { get; set; }

    /// <summary>
    /// The UTC timestamp to display, sourced from the <c>datetime</c> attribute.
    /// </summary>
    [HtmlAttributeName("datetime")]
    public DateTimeOffset Value { get; set; }

    /// <summary>
    /// Gets or sets the display mode for the formatted time.
    /// Valid values: <c>time</c> (default), <c>date</c>, <c>datetime</c>, or <c>relative</c>.
    /// </summary>
    [HtmlAttributeName("rw-display")]
    public string Display { get; set; } = "time";

    /// <summary>
    /// Gets or sets the format style for absolute time formatting.
    /// Valid values: <c>short</c>, <c>medium</c> (default), <c>long</c>, or <c>full</c>.
    /// </summary>
    [HtmlAttributeName("rw-format")]
    public string? Format { get; set; }

    /// <summary>
    /// Renders a <c>&lt;time&gt;</c> element with the UTC timestamp in the <c>datetime</c> attribute
    /// and data attributes for client-side JavaScript formatting.
    /// </summary>
    /// <param name="context">Contains information associated with the current HTML tag.</param>
    /// <param name="output">A stateful HTML element used to generate an HTML tag.</param>
    /// <exception cref="ArgumentException">Thrown when <see cref="Value"/> is the default value or if <see cref="Display"/> or <see cref="Format"/> have invalid values.</exception>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (string.IsNullOrWhiteSpace(RwType) || !ValidTypes.Contains(RwType))
        {
            return;
        }

        var isUtc = string.Equals(RwType, "utc", StringComparison.OrdinalIgnoreCase);

        if (Value == default)
        {
            throw new ArgumentException(
                "The 'datetime' attribute is required for the local time tag helper.",
                nameof(Value));
        }

        if (!ValidDisplayModes.Contains(Display))
        {
            throw new ArgumentException(
                $"Invalid display mode '{Display}'. Valid values are: {string.Join(", ", ValidDisplayModes)}",
                nameof(Display));
        }

        if (!string.IsNullOrWhiteSpace(Format) && !ValidFormatStyles.Contains(Format))
        {
            throw new ArgumentException(
                $"Invalid format style '{Format}'. Valid values are: {string.Join(", ", ValidFormatStyles)}",
                nameof(Format));
        }

        output.TagMode = TagMode.StartTagAndEndTag;

        var utcTime = Value.ToUniversalTime();
        var fallbackContent = utcTime.ToString("yyyy-MM-dd HH:mm:ss UTC");
        var isoTimestamp = utcTime.ToString("o");

        // TagHelperOutput handles HTML encoding for both attributes and content automatically.
        // We use local variables here for clarity and defense-in-depth.
        output.Content.SetContent(fallbackContent);
        output.Attributes.SetAttribute("datetime", isoTimestamp);

        // Add data attribute to signal JavaScript formatting
        output.Attributes.SetAttribute("data-rw-time", string.Empty);

        // Add timezone attribute when UTC
        if (isUtc)
        {
            output.Attributes.SetAttribute("data-rw-time-tz", "utc");
        }

        // Add display mode if not default
        if (!string.Equals(Display, "time", StringComparison.OrdinalIgnoreCase))
        {
            output.Attributes.SetAttribute("data-rw-time-display", Display.ToLowerInvariant());
        }

        // Add format style if specified
        if (!string.IsNullOrWhiteSpace(Format))
        {
            output.Attributes.SetAttribute("data-rw-time-format", Format.ToLowerInvariant());
        }

        // Remove the trigger attribute
        output.Attributes.RemoveAll("rw-type");
    }
}
