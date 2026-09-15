namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class EnvironmentCloneIsolationRegressionTests
{
    [Fact]
    public void FailedSiblingPatchCannotMutateSharedGetterOnlyList()
    {
        var source = new SharedListSettings();
        source.Items.Clear();
        source.Items.Add(1);
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__ITEMS", "[2]"), ("APP__PORT", "bad")]));

        var result = ((IConfigValuePatcher)provider).Patch(new("Production", AppSurfaceConfigKey.Parse("App")), source);

        Assert.Equal(ConfigPatchStatus.Terminal, result.Status);
        Assert.Equal([1], source.Items);
        Assert.Equal(3, source.Port);
        Assert.Null(result.Value);
    }

    [Fact]
    public void UnpatchedGetterOnlyScalarCannotBeReplacedByConstructorDefault()
    {
        var source = new ConstructorSettings("custom") { Name = "before" };
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__NAME", "after")]));

        var result = ((IConfigValuePatcher)provider).Patch(new("Production", AppSurfaceConfigKey.Parse("App")), source);

        Assert.Equal(ConfigPatchStatus.Terminal, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("custom", source.Region);
        Assert.Equal("before", source.Name);
    }

    [Fact]
    public void GetterOnlyArrayIsCopiedIntoIndependentConstructedStorage()
    {
        var source = new ArraySettings();
        source.Items[0].Name = "retained";

        var copy = (ArraySettings)new EnvironmentObjectClone(32, default).Copy(source, source.GetType());

        Assert.NotSame(source.Items, copy.Items);
        Assert.NotSame(source.Items[0], copy.Items[0]);
        Assert.Equal("retained", copy.Items[0].Name);
        copy.Items[0].Name = "changed";
        Assert.Equal("retained", source.Items[0].Name);
    }

    [Fact]
    public void GetterOnlyArrayWithDifferentConstructionLengthCannotLoseElements()
    {
        var source = new ArraySettings(2);
        source.Items[1].Name = "retained";

        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(source, source.GetType()));

        Assert.Equal(2, source.Items.Length);
        Assert.Equal("retained", source.Items[1].Name);
    }

    [Fact]
    public void GetterOnlyEnumerableCopiesItemsIntoIndependentConstructedList()
    {
        var source = new EnumerableSettings();
        var sourceItems = Assert.IsType<List<Item?>>(source.Items);
        sourceItems[0]!.Name = "retained";
        sourceItems.Add(null);

        var copy = (EnumerableSettings)new EnvironmentObjectClone(32, default).Copy(source, source.GetType());

        Assert.NotSame(source.Items, copy.Items);
        var copiedItems = copy.Items.ToArray();
        Assert.Equal(2, copiedItems.Length);
        Assert.NotSame(sourceItems[0], copiedItems[0]);
        Assert.Equal("retained", copiedItems[0]!.Name);
        Assert.Null(copiedItems[1]);
        copiedItems[0]!.Name = "changed";
        Assert.Equal("retained", sourceItems[0]!.Name);
    }

    [Fact]
    public void MatchingGetterOnlyScalarAndNullSurviveConstruction()
    {
        var source = new ScalarAndNullSettings { Name = "retained" };

        var copy = (ScalarAndNullSettings)new EnvironmentObjectClone(32, default).Copy(source, source.GetType());

        Assert.NotSame(source, copy);
        Assert.Equal(7, copy.Port);
        Assert.Null(copy.Child);
        Assert.Equal("retained", copy.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetterOnlyNullMismatchCannotReplaceOriginalState(bool sourceHasValue)
    {
        IReadOnlyChildSettings source = sourceHasValue
            ? new NullDefaultChildSettings(new Item { Name = "retained" })
            : new PopulatedDefaultChildSettings(null);
        var originalChild = source.Child;

        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(source, source.GetType()));

        Assert.Same(originalChild, source.Child);
        Assert.Equal(sourceHasValue ? "retained" : null, source.Child?.Name);
    }

    private sealed class SharedListSettings
    {
        private static readonly List<int> Shared = [];
        public List<int> Items => Shared;
        public int Port { get; set; } = 3;
    }

    private sealed class ConstructorSettings
    {
        public ConstructorSettings() : this("default") { }
        public ConstructorSettings(string region) { Region = region; }
        public string Region { get; }
        public string Name { get; set; } = "default";
    }

    private sealed class ArraySettings
    {
        public ArraySettings() : this(1) { }
        public ArraySettings(int length) { Items = Enumerable.Range(0, length).Select(_ => new Item()).ToArray(); }
        public Item[] Items { get; }
    }

    private sealed class EnumerableSettings
    {
        public IEnumerable<Item?> Items { get; } = new List<Item?> { new() };
    }

    private sealed class ScalarAndNullSettings
    {
        public int Port { get; } = 7;
        public Item? Child { get; }
        public string Name { get; set; } = "default";
    }

    private interface IReadOnlyChildSettings
    {
        Item? Child { get; }
    }

    private sealed class NullDefaultChildSettings : IReadOnlyChildSettings
    {
        public NullDefaultChildSettings() : this(null) { }
        public NullDefaultChildSettings(Item? child) { Child = child; }
        public Item? Child { get; }
    }

    private sealed class PopulatedDefaultChildSettings : IReadOnlyChildSettings
    {
        public PopulatedDefaultChildSettings() : this(new Item()) { }
        public PopulatedDefaultChildSettings(Item? child) { Child = child; }
        public Item? Child { get; }
    }

    private sealed class Item { public string Name { get; set; } = "default"; }
}
