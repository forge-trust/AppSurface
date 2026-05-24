using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Tag helper that renders an <c>rw-stream-source</c> element to establish a connection to a RazorWire stream.
/// </summary>
[HtmlTargetElement("rw:stream-source")]
public class StreamSourceTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the channel name of the stream to connect to. This attribute is required.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the stream source should be preserved across Turbo page loads.
    /// </summary>
    public bool Permanent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether retained channel messages should be delivered before live messages.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. When <see cref="Replay"/> is <see langword="true"/>,
    /// <see cref="StreamSourceTagHelper"/> appends <c>?replay=1</c> to the generated stream URL so
    /// <c>MapRazorWire</c> can request retained messages before live delivery. Use replay for clients that need recent
    /// context before subscribing to new events. Replay can increase initial latency and bandwidth, may duplicate messages
    /// a client already processed before reconnecting, and depends on the hub implementation's retention policy.
    /// </remarks>
    public bool Replay { get; set; }

    private readonly RazorWireOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="StreamSourceTagHelper"/> with the provided RazorWire options.
    /// </summary>
    /// <param name="options">The RazorWire configuration used to construct stream URLs.</param>
    public StreamSourceTagHelper(RazorWireOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Configures the <c>rw-stream-source</c> element by setting its <c>src</c> attribute and handling the <c>permanent</c> option.
    /// </summary>
    /// <param name="context">Contains information associated with the current HTML tag.</param>
    /// <param name="output">A stateful HTML element used to generate an HTML tag.</param>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Channel"/> is null, empty, or contains only whitespace.</exception>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "rw-stream-source";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (string.IsNullOrWhiteSpace(Channel))
        {
            throw new InvalidOperationException(
                $"The 'channel' attribute is required for the 'rw:stream-source' tag helper.");
        }

        var src = Replay
            ? $"{_options.Streams.BasePath}/{Channel}?replay=1"
            : $"{_options.Streams.BasePath}/{Channel}";
        output.Attributes.SetAttribute("src", src);

        if (Permanent)
        {
            output.Attributes.SetAttribute("data-turbo-permanent", "");
        }
    }
}
