using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public sealed class FormInteractionTagHelperTests
{
    [Fact]
    public void Toggle_Process_EmitsCanonicalDataAttributes()
    {
        var output = CreateOutput("input", "rw-form-toggle", "no-action", "rw-form-toggle-invert", "true");

        new FormToggleTagHelper { TargetName = "no-action", Invert = true }.Process(CreateContext("input"), output);

        Assert.Equal("no-action", output.Attributes["data-rw-form-toggle"].Value);
        Assert.Equal("true", output.Attributes["data-rw-form-toggle-invert"].Value);
        Assert.False(output.Attributes.ContainsName("rw-form-toggle"));
        Assert.False(output.Attributes.ContainsName("rw-form-toggle-invert"));
    }

    [Fact]
    public void ToggleTarget_Process_EmitsDisableWhenHiddenWhenRequested()
    {
        var output = CreateOutput("fieldset", "rw-form-toggle-target", "no-action");

        new FormToggleTargetTagHelper { TargetName = "no-action", DisableWhenHidden = true }
            .Process(CreateContext("fieldset"), output);

        Assert.Equal("no-action", output.Attributes["data-rw-form-toggle-target"].Value);
        Assert.Equal("true", output.Attributes["data-rw-form-toggle-disable-when-hidden"].Value);
        Assert.False(output.Attributes.ContainsName("rw-form-toggle-target"));
    }

    [Fact]
    public void Collection_Process_EmitsNameLabelAndRemoveMode()
    {
        var output = CreateOutput("div", "rw-form-collection", "Actions");

        new FormCollectionTagHelper
        {
            Name = "Actions",
            Label = "action",
            RemoveMode = "mark"
        }.Process(CreateContext("div"), output);

        Assert.Equal("Actions", output.Attributes["data-rw-form-collection"].Value);
        Assert.Equal("action", output.Attributes["data-rw-form-collection-label"].Value);
        Assert.Equal("mark", output.Attributes["data-rw-form-collection-remove-mode"].Value);
        Assert.False(output.Attributes.ContainsName("rw-form-collection"));
    }

    [Fact]
    public void Row_Process_EmitsRowAndIndexMarkers()
    {
        var output = CreateOutput("fieldset", "rw-form-collection-row", "true", "rw-form-index", "0");

        new FormCollectionRowTagHelper { Index = "0" }.Process(CreateContext("fieldset"), output);

        Assert.Equal("true", output.Attributes["data-rw-form-collection-row"].Value);
        Assert.Equal("0", output.Attributes["data-rw-form-index"].Value);
        Assert.False(output.Attributes.ContainsName("rw-form-collection-row"));
        Assert.False(output.Attributes.ContainsName("rw-form-index"));
    }

    [Fact]
    public void Template_Process_EmitsCanonicalTemplateMarker()
    {
        var output = CreateOutput("template", "rw-form-collection-template", "true");

        new FormCollectionTemplateTagHelper().Process(CreateContext("template"), output);

        Assert.Equal("true", output.Attributes["data-rw-form-collection-template"].Value);
        Assert.False(output.Attributes.ContainsName("rw-form-collection-template"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("duplicate")]
    [InlineData("remove")]
    public void Command_Process_SetsTypeButtonWhenAbsent(string command)
    {
        var output = CreateOutput("button", $"rw-form-collection-{command}", "true");

        switch (command)
        {
            case "add":
                new FormCollectionAddTagHelper().Process(CreateContext("button"), output);
                break;
            case "duplicate":
                new FormCollectionDuplicateTagHelper().Process(CreateContext("button"), output);
                break;
            default:
                new FormCollectionRemoveTagHelper().Process(CreateContext("button"), output);
                break;
        }

        Assert.Equal("button", output.Attributes["type"].Value);
        Assert.Equal(command == "remove" ? "physical" : "true", output.Attributes[$"data-rw-form-collection-{command}"].Value);
        Assert.False(output.Attributes.ContainsName($"rw-form-collection-{command}"));
    }

    [Fact]
    public void Remove_Process_PreservesExplicitMarkModeAndExistingType()
    {
        var output = CreateOutput("button", "type", "submit", "rw-form-collection-remove", "mark");

        new FormCollectionRemoveTagHelper { Mode = "mark" }.Process(CreateContext("button"), output);

        Assert.Equal("submit", output.Attributes["type"].Value);
        Assert.Equal("mark", output.Attributes["data-rw-form-collection-remove"].Value);
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
