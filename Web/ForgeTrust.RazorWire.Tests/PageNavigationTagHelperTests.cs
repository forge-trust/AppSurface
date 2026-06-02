using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public sealed class PageNavigationTagHelperTests
{
    [Fact]
    public void Root_Process_WhenEnabled_EmitsRootMarkerAndRemovesSourceAttribute()
    {
        var output = CreateOutput("nav", "rw-page-nav", "true");

        new PageNavigationRootTagHelper().Process(CreateContext("nav"), output);

        Assert.Equal("true", output.Attributes["data-rw-page-nav"].Value);
        Assert.False(output.Attributes.ContainsName("rw-page-nav"));
    }

    [Fact]
    public void Root_Process_WhenDisabled_RemovesSourceAttributeWithoutMarker()
    {
        var output = CreateOutput("nav", "rw-page-nav", "false");

        new PageNavigationRootTagHelper { Enabled = false }.Process(CreateContext("nav"), output);

        Assert.False(output.Attributes.ContainsName("data-rw-page-nav"));
        Assert.False(output.Attributes.ContainsName("rw-page-nav"));
    }

    [Fact]
    public void Link_Process_EmitsLinkMarker()
    {
        var output = CreateOutput("a", "href", "#overview", "rw-page-nav-link", "true");

        new PageNavigationLinkTagHelper().Process(CreateContext("a"), output);

        Assert.Equal("true", output.Attributes["data-rw-page-nav-link"].Value);
        Assert.Equal("#overview", output.Attributes["href"].Value);
        Assert.False(output.Attributes.ContainsName("rw-page-nav-link"));
    }

    [Fact]
    public void Toggle_Process_EmitsToggleMarkerTypeAndAriaControls()
    {
        var output = CreateOutput("button", "rw-page-nav-toggle", "page-sections-panel");

        new PageNavigationToggleTagHelper { Controls = "page-sections-panel" }.Process(CreateContext("button"), output);

        Assert.Equal("true", output.Attributes["data-rw-page-nav-toggle"].Value);
        Assert.Equal("button", output.Attributes["type"].Value);
        Assert.Equal("page-sections-panel", output.Attributes["aria-controls"].Value);
        Assert.False(output.Attributes.ContainsName("rw-page-nav-toggle"));
    }

    [Fact]
    public void Toggle_Process_PreservesExistingTypeAndAriaControls()
    {
        var output = CreateOutput(
            "button",
            "type",
            "submit",
            "aria-controls",
            "existing-panel",
            "rw-page-nav-toggle",
            "new-panel");

        new PageNavigationToggleTagHelper { Controls = "new-panel" }.Process(CreateContext("button"), output);

        Assert.Equal("submit", output.Attributes["type"].Value);
        Assert.Equal("existing-panel", output.Attributes["aria-controls"].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Toggle_Process_WhenControlsIsEmptyOrBooleanSentinel_DoesNotEmitAriaControls(string controls)
    {
        var output = CreateOutput("button", "rw-page-nav-toggle", controls);

        new PageNavigationToggleTagHelper { Controls = controls }.Process(CreateContext("button"), output);

        Assert.Equal("true", output.Attributes["data-rw-page-nav-toggle"].Value);
        Assert.False(output.Attributes.ContainsName("aria-controls"));
    }

    [Fact]
    public void Link_Process_WhenDisabled_RemovesSourceAttributeWithoutMarker()
    {
        var output = CreateOutput("a", "href", "#overview", "rw-page-nav-link", "false");

        new PageNavigationLinkTagHelper { Enabled = false }.Process(CreateContext("a"), output);

        Assert.False(output.Attributes.ContainsName("data-rw-page-nav-link"));
        Assert.False(output.Attributes.ContainsName("rw-page-nav-link"));
    }

    [Fact]
    public void Panel_Process_EmitsPanelMarker()
    {
        var output = CreateOutput("div", "rw-page-nav-panel", "true");

        new PageNavigationPanelTagHelper().Process(CreateContext("div"), output);

        Assert.Equal("true", output.Attributes["data-rw-page-nav-panel"].Value);
        Assert.False(output.Attributes.ContainsName("rw-page-nav-panel"));
    }

    [Fact]
    public void Panel_Process_WhenDisabled_RemovesSourceAttributeWithoutMarker()
    {
        var output = CreateOutput("div", "rw-page-nav-panel", "false");

        new PageNavigationPanelTagHelper { Enabled = false }.Process(CreateContext("div"), output);

        Assert.False(output.Attributes.ContainsName("data-rw-page-nav-panel"));
        Assert.False(output.Attributes.ContainsName("rw-page-nav-panel"));
    }

    private static TagHelperContext CreateContext(string tagName)
    {
        return new TagHelperContext(
            tagName,
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));
    }

    private static TagHelperOutput CreateOutput(string tagName, params string[] nameValuePairs)
    {
        var attributes = new TagHelperAttributeList();
        for (var i = 0; i + 1 < nameValuePairs.Length; i += 2)
        {
            attributes.SetAttribute(nameValuePairs[i], nameValuePairs[i + 1]);
        }

        return new TagHelperOutput(
            tagName,
            attributes,
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
    }
}
