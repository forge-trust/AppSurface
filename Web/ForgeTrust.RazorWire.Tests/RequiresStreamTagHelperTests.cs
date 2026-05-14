using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public class RequiresStreamTagHelperTests
{
    [Fact]
    public void Process_WithMissingValue_LeavesOutputUnchanged()
    {
        // Arrange
        var helper = new RequiresStreamTagHelper { RequiresStream = "   " };
        var output = CreateOutput("button");
        output.Attributes.Add("requires-stream", "stream-a");

        // Act
        helper.Process(CreateContext(), output);

        // Assert
        Assert.True(output.Attributes.ContainsName("requires-stream"));
        Assert.False(output.Attributes.ContainsName("data-rw-requires-stream"));
        Assert.False(output.Attributes.ContainsName("aria-disabled"));
        Assert.False(output.Attributes.ContainsName("disabled"));
    }

    [Fact]
    public void Process_ForSupportedFormElement_SetsDisabledAttributes()
    {
        // Arrange
        var helper = new RequiresStreamTagHelper { RequiresStream = "updates" };
        var output = CreateOutput("button");
        output.Attributes.Add("requires-stream", "updates");

        // Act
        helper.Process(CreateContext(), output);

        // Assert
        Assert.Equal("updates", output.Attributes["data-rw-requires-stream"].Value);
        Assert.Equal("true", output.Attributes["aria-disabled"].Value);
        Assert.Equal("disabled", output.Attributes["disabled"].Value);
        Assert.False(output.Attributes.ContainsName("requires-stream"));
    }

    [Fact]
    public void Process_ForNonFormElement_DoesNotSetDisabledAttribute()
    {
        // Arrange
        var helper = new RequiresStreamTagHelper { RequiresStream = "updates" };
        var output = CreateOutput("div");
        output.Attributes.Add("requires-stream", "updates");

        // Act
        helper.Process(CreateContext(), output);

        // Assert
        Assert.Equal("updates", output.Attributes["data-rw-requires-stream"].Value);
        Assert.Equal("true", output.Attributes["aria-disabled"].Value);
        Assert.False(output.Attributes.ContainsName("disabled"));
        Assert.False(output.Attributes.ContainsName("requires-stream"));
    }

    private static TagHelperContext CreateContext() =>
        new(new TagHelperAttributeList(), new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));

    private static TagHelperOutput CreateOutput(string tagName) =>
        new(
            tagName,
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
}
