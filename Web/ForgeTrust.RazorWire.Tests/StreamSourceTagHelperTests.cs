using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public sealed class StreamSourceTagHelperTests
{
    [Fact]
    public void Process_ShouldAppendReplayQuery_WhenReplayIsEnabled()
    {
        var tagHelper = new StreamSourceTagHelper(new RazorWireOptions())
        {
            Channel = "harvest",
            Replay = true
        };
        var output = CreateOutput();

        tagHelper.Process(CreateContext(), output);

        Assert.Equal("/_rw/streams/harvest?replay=1", output.Attributes["src"].Value);

        var liveOnlyTagHelper = new StreamSourceTagHelper(new RazorWireOptions())
        {
            Channel = "harvest",
            Replay = false
        };
        var liveOnlyOutput = CreateOutput();

        liveOnlyTagHelper.Process(CreateContext(), liveOnlyOutput);

        Assert.Equal("/_rw/streams/harvest", liveOnlyOutput.Attributes["src"].Value);
    }

    private static TagHelperContext CreateContext()
    {
        return new TagHelperContext(
            [],
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));
    }

    private static TagHelperOutput CreateOutput()
    {
        return new TagHelperOutput(
            "rw:stream-source",
            [],
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
    }
}
