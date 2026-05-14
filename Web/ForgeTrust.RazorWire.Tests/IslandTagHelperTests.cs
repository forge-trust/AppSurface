using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public class IslandTagHelperTests
{
    [Fact]
    public void Process_RendersTurboFrameWithCorrectId()
    {
        var helper = new IslandTagHelper { Id = "test-island" };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.Equal("turbo-frame", output.TagName);
        Assert.Equal("test-island", output.Attributes["id"].Value);
    }

    [Fact]
    public void Process_WithSrc_RendersSrcAttribute()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            Src = "/demo/fragment"
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.Equal("/demo/fragment", output.Attributes["src"].Value);
    }

    [Fact]
    public void Process_WithLoading_RendersLoadingAttribute()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            Loading = "lazy"
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.Equal("lazy", output.Attributes["loading"].Value);
    }

    [Fact]
    public void Process_WithPermanent_RendersDataTurboPermanent()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            Permanent = true
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.True(output.Attributes.ContainsName("data-turbo-permanent"));
    }

    [Fact]
    public void Process_WithSwr_RendersDataTurboPermanentAndDataRwSwr()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            Swr = true
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.True(output.Attributes.ContainsName("data-turbo-permanent"));
        Assert.Equal("true", output.Attributes["data-rw-swr"].Value);
    }

    [Fact]
    public void Process_WithTransitionName_RendersStyle()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            TransitionName = "hero-image"
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.Contains("view-transition-name: hero-image", output.Attributes["style"].Value.ToString());
    }

    [Fact]
    public void Process_WithTransitionName_SanitizesValue()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            TransitionName = "hero image!!!"
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        // "hero image!!!" -> "hero-image" via StringUtils.ToSafeId
        Assert.Contains("view-transition-name: hero-image", output.Attributes["style"].Value.ToString());
    }

    [Fact]
    public void Process_WithExport_RendersDataRwExport()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            Export = "Profile"
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.Equal("Profile", output.Attributes["data-rw-export"].Value);
    }

    [Fact]
    public void Process_WithClientModule_RendersDataRwModuleAndProps()
    {
        var helper = new IslandTagHelper
        {
            Id = "test",
            ClientModule = "./App.js",
            ClientProps = "{\"name\":\"test\"}",
            ClientStrategy = "load"
        };
        var output = CreateOutput();
        helper.Process(CreateContext(), output);

        Assert.Equal("./App.js", output.Attributes["data-rw-module"].Value);
        Assert.Equal("{\"name\":\"test\"}", output.Attributes["data-rw-props"].Value);
        Assert.Equal("load", output.Attributes["data-rw-strategy"].Value);
    }

    [Fact]
    public void Process_WithNullId_ThrowsArgumentException()
    {
        var helper = new IslandTagHelper { Id = null! };
        var output = CreateOutput();

        Assert.Throws<ArgumentException>(() => helper.Process(CreateContext(), output));
    }

    [Fact]
    public void Process_WithWhiteSpaceId_ThrowsArgumentException()
    {
        var helper = new IslandTagHelper { Id = "   " };
        var output = CreateOutput();

        Assert.Throws<ArgumentException>(() => helper.Process(CreateContext(), output));
    }

    private static TagHelperContext CreateContext() =>
        new(new TagHelperAttributeList(), new Dictionary<object, object>(), Guid.NewGuid().ToString());

    private static TagHelperOutput CreateOutput() =>
        new(
            "rw:island",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
}
