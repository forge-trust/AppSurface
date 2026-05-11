using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireFormTagHelperTests
{
    [Fact]
    public void Process_WhenEnabled_EmitsTurboAndRazorWireMarkers()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("rw-active", "true");

        helper.Process(CreateContext(), output);

        Assert.Equal("true", output.Attributes["data-turbo"].Value);
        Assert.Equal("true", output.Attributes["data-rw-form"].Value);
        Assert.Equal("auto", output.Attributes["data-rw-form-failure"].Value);
        Assert.Contains("__RazorWireForm", output.PostContent.GetContent());
        Assert.False(output.Attributes.ContainsName("rw-active"));
    }

    [Fact]
    public void Process_WhenPerFormManual_EmitsMarkerAndManualMode()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("data-rw-form-failure", "manual");

        helper.Process(CreateContext(), output);

        Assert.Equal("manual", output.Attributes["data-rw-form-failure"].Value);
        Assert.Contains("__RazorWireForm", output.PostContent.GetContent());
    }

    [Fact]
    public void Process_WhenPerFormAuto_EmitsMarkerAndAutoMode()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("data-rw-form-failure", "auto");

        helper.Process(CreateContext(), output);

        Assert.Equal("auto", output.Attributes["data-rw-form-failure"].Value);
        Assert.Contains("__RazorWireForm", output.PostContent.GetContent());
    }

    [Fact]
    public void Process_WhenFailureTargetIsId_EmitsServerFailureTargetMarker()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("data-rw-form-failure-target", "#form-errors");

        helper.Process(CreateContext(), output);

        var content = output.PostContent.GetContent();
        Assert.Contains("__RazorWireFormFailureTarget", content);
        Assert.Contains("value=\"form-errors\"", content);
    }

    [Fact]
    public void Process_WhenFailureTargetIsSelector_DoesNotEmitServerFailureTargetMarker()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("data-rw-form-failure-target", "[data-rw-form-errors]");

        helper.Process(CreateContext(), output);

        Assert.DoesNotContain("__RazorWireFormFailureTarget", output.PostContent.GetContent());
    }

    [Fact]
    public void Process_WhenFailureTargetIsOnlyHash_DoesNotEmitServerFailureTargetMarker()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("data-rw-form-failure-target", "#");

        helper.Process(CreateContext(), output);

        Assert.DoesNotContain("__RazorWireFormFailureTarget", output.PostContent.GetContent());
    }

    [Fact]
    public void Process_WhenPerFormOff_SkipsRazorWireFailureMarkers()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions());
        var output = CreateOutput("data-rw-form-failure", "off");

        helper.Process(CreateContext(), output);

        Assert.Equal("true", output.Attributes["data-turbo"].Value);
        Assert.False(output.Attributes.ContainsName("data-rw-form"));
        Assert.DoesNotContain("__RazorWireForm", output.PostContent.GetContent());
    }

    [Fact]
    public void Process_WhenGlobalFailureUxDisabled_SkipsRazorWireFailureMarkers()
    {
        var options = new RazorWireOptions();
        options.Forms.EnableFailureUx = false;
        var helper = new RazorWireFormTagHelper(options);
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("true", output.Attributes["data-turbo"].Value);
        Assert.False(output.Attributes.ContainsName("data-rw-form"));
        Assert.DoesNotContain("__RazorWireForm", output.PostContent.GetContent());
    }

    [Fact]
    public void Process_WhenDisabled_DisablesTurbo()
    {
        var helper = new RazorWireFormTagHelper(new RazorWireOptions()) { Enabled = false };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("false", output.Attributes["data-turbo"].Value);
        Assert.False(output.Attributes.ContainsName("data-rw-form"));
    }

    private static TagHelperContext CreateContext()
    {
        return new TagHelperContext(
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));
    }

    private static TagHelperOutput CreateOutput(params string[] nameValuePairs)
    {
        var attributes = new TagHelperAttributeList { { "rw-active", "true" } };
        for (var i = 0; i + 1 < nameValuePairs.Length; i += 2)
        {
            attributes.SetAttribute(nameValuePairs[i], nameValuePairs[i + 1]);
        }

        return new TagHelperOutput(
            "form",
            attributes,
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
    }
}
