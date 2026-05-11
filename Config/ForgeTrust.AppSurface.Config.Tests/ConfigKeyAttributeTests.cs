using ForgeTrust.AppSurface.Config;
using Xunit;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigKeyAttributeTests
{
    private class NoAttribute { }

    [ConfigKey("Custom")]
    private class SimpleAttribute { }

    private class Parent
    {
        public class Child { }

        [ConfigKey("CustomChild")]
        public class CustomChild { }

        [ConfigKey("RootChild", root: true)]
        public class RootChild { }
    }

    [Fact]
    public void GetKeyPath_ReturnsClassNameWhenNoAttribute()
    {
        Assert.Equal("ConfigKeyAttributeTests.NoAttribute", ConfigKeyAttribute.GetKeyPath(typeof(NoAttribute)));
    }

    [Fact]
    public void GetKeyPath_ReturnsCustomKeyFromAttribute()
    {
        Assert.Equal("ConfigKeyAttributeTests.Custom", ConfigKeyAttribute.GetKeyPath(typeof(SimpleAttribute)));
    }

    [Fact]
    public void GetKeyPath_HandlesNestedClasses()
    {
        Assert.Equal("ConfigKeyAttributeTests.Parent.Child", ConfigKeyAttribute.GetKeyPath(typeof(Parent.Child)));
        Assert.Equal("ConfigKeyAttributeTests.Parent.CustomChild", ConfigKeyAttribute.GetKeyPath(typeof(Parent.CustomChild)));
    }

    [Fact]
    public void GetKeyPath_HandlesRootOverrideInNestedClass()
    {
        Assert.Equal("RootChild", ConfigKeyAttribute.GetKeyPath(typeof(Parent.RootChild)));
    }

    [Fact]
    public void ExtractKey_ReturnsKeyFromAttribute()
    {
        Assert.Equal("Custom", ConfigKeyAttribute.ExtractKey(typeof(SimpleAttribute)));
        Assert.Equal("Custom", ConfigKeyAttribute.ExtractKey(new SimpleAttribute()));
        Assert.Null(ConfigKeyAttribute.ExtractKey(typeof(NoAttribute)));
    }

    [Fact]
    public void Constructor_SetsProperties()
    {
        var attr = new ConfigKeyAttribute("MyKey", true);
        Assert.Equal("MyKey", attr.Key);
        Assert.True(attr.Root);

        var attrFromType = new ConfigKeyAttribute(typeof(Parent.RootChild));
        Assert.Equal("RootChild", attrFromType.Key);
        Assert.True(attrFromType.Root);
    }

    [Fact]
    public void Constructor_WithTypeAndNoAttribute_SetsDefaultRoot()
    {
        var attr = new ConfigKeyAttribute(typeof(NoAttribute));
        Assert.False(attr.Root);
        Assert.Equal("ConfigKeyAttributeTests.NoAttribute", attr.Key);
    }
}
