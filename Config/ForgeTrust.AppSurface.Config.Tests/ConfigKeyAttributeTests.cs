using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Options;
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

    [ConfigKey("Literal.Parent", root: true)]
    private class LiteralParent
    {
        [ConfigKey("Child.Part:Leaf")]
        public class Child { }
    }

    [Fact]
    public void GetLogicalKey_PublicHelperIsStrictForEachNestedFragment()
    {
        var key = ConfigKeyAttribute.GetLogicalKey(typeof(LiteralParent.Child));

        Assert.Equal<string>(["Literal.Parent", "Child.Part", "Leaf"], key.Segments);
        Assert.Equal(ConfigKeyInputOrigin.Typed, key.InputOrigin);
        Assert.Null(key.OriginalInput);
    }

    [Fact]
    public void GetLogicalKey_UntranslatedNestedInputUsesCanonicalOriginalInput()
    {
        var parser = new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions
        {
            LegacyDotPathBehavior = LegacyDotPathBehavior.Strict
        }));

        var key = ConfigKeyAttribute.GetLogicalKey(typeof(Parent.Child), parser);

        Assert.Equal("ConfigKeyAttributeTests:Parent:Child", key.Value);
        Assert.Equal(ConfigKeyInputOrigin.StrictString, key.InputOrigin);
        Assert.Equal(key.Value, key.OriginalInput);
    }

    [Fact]
    public void GetLogicalKey_RejectsNullTypeAndParser()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigKeyAttribute.GetLogicalKey(null!));
        Assert.Throws<ArgumentNullException>(() => ConfigKeyAttribute.GetLogicalKey(typeof(Parent), null!));
        Assert.Throws<ArgumentNullException>(() => new ConfigKeyAttribute((Type)null!));
    }

    [Fact]
    public void GetKeyPath_DeprecatedAliasRendersStrictColonIdentity()
    {
#pragma warning disable CS0618 // Exercise the deprecated alias intentionally; normal callers use GetLogicalKey.
        var rendered = ConfigKeyAttribute.GetKeyPath(typeof(LiteralParent.Child));
#pragma warning restore CS0618
        Assert.Equal("Literal.Parent:Child.Part:Leaf", rendered);
    }

    [Fact]
    public void GetLogicalKey_ReturnsClassNameWhenNoAttribute()
    {
        Assert.Equal("ConfigKeyAttributeTests:NoAttribute", ConfigKeyAttribute.GetLogicalKey(typeof(NoAttribute)).Value);
    }

    [Fact]
    public void GetLogicalKey_ReturnsCustomKeyFromAttribute()
    {
        Assert.Equal("ConfigKeyAttributeTests:Custom", ConfigKeyAttribute.GetLogicalKey(typeof(SimpleAttribute)).Value);
    }

    [Fact]
    public void GetLogicalKey_HandlesNestedClasses()
    {
        Assert.Equal("ConfigKeyAttributeTests:Parent:Child", ConfigKeyAttribute.GetLogicalKey(typeof(Parent.Child)).Value);
        Assert.Equal("ConfigKeyAttributeTests:Parent:CustomChild", ConfigKeyAttribute.GetLogicalKey(typeof(Parent.CustomChild)).Value);
    }

    [Fact]
    public void GetLogicalKey_HandlesRootOverrideInNestedClass()
    {
        Assert.Equal("RootChild", ConfigKeyAttribute.GetLogicalKey(typeof(Parent.RootChild)).Value);
    }

    [Fact]
    public void GetLogicalKey_ReturnsTypedColonSegments()
    {
        var key = ConfigKeyAttribute.GetLogicalKey(typeof(Parent.CustomChild));

        Assert.Equal("ConfigKeyAttributeTests:Parent:CustomChild", key.Value);
        Assert.Equal<string>(["ConfigKeyAttributeTests", "Parent", "CustomChild"], key.Segments);
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
        Assert.Equal("ConfigKeyAttributeTests:NoAttribute", attr.Key);
    }
}
