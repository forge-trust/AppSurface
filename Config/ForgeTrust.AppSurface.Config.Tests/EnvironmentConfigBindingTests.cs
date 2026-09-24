using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Config.Tests;

public class EnvironmentConfigBindingTests
{
    [Fact]
    public void ClonePreservesWritableDagIdentityAndNullCollectionItems()
    {
        var source = new Dag();
        source.Second = source.First;
        source.Items = [null, source.First];
        var copy = (Dag)new EnvironmentObjectClone(32, default).Copy(source, typeof(Dag));
        Assert.NotSame(source.First, copy.First);
        Assert.Same(copy.First, copy.Second);
        Assert.Null(copy.Items[0]);
        Assert.Same(copy.First, copy.Items[1]);
    }

    [Fact]
    public void CloneUsesPublicParameterizedConstructionWhenNoDefaultConstructorExists()
    {
        var source = new Parameterized("retained");
        var copy = (Parameterized)new EnvironmentObjectClone(32, default).Copy(source, typeof(Parameterized));
        Assert.NotSame(source, copy);
        Assert.Equal("retained", copy.Name);
    }

    [Fact]
    public void CloneFailsIfAGetterOnlyDagCannotBeReconnectedPublicly()
    {
        var source = new ReadOnlyDag(true);
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(source, typeof(ReadOnlyDag)));
        Assert.Same(source.First, source.Second);
    }

    [Fact]
    public void CloneRejectsNullOrUnsupportedSerializationWithoutLeakingValues()
    {
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(new NullClone(1), typeof(NullClone)));
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(new UnsupportedClone(1), typeof(UnsupportedClone)));
        var dictionary = new NullDictionary();
        Assert.NotSame(dictionary, new EnvironmentObjectClone(32, default).Copy(dictionary, typeof(NullDictionary)));
        var cycle = new Hashtable();
        cycle["self"] = cycle;
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(cycle, typeof(Hashtable)));
    }

    [Fact]
    public void BindingPlansAreCachedImmutableAndDescribeSupportedShapes()
    {
        var plans = Enumerable.Range(0, 64).AsParallel().Select(_ => EnvironmentBindingPlan.For(typeof(Dag))).ToArray();
        Assert.All(plans, plan => Assert.Same(plans[0], plan));
        Assert.Throws<NotSupportedException>(() => ((IList<EnvironmentBindingMember>)plans[0].Members).Clear());
        Assert.Null(EnvironmentBindingPlan.CollectionElementType(typeof(int[,])));
        Assert.Null(EnvironmentBindingPlan.MaterializeCollection(typeof(HashSet<int>), typeof(int), new List<int>()));
        Assert.Null(EnvironmentBindingPlan.CollectionCount(new object()));
        Assert.Equal(1, EnvironmentBindingPlan.CollectionCount(new ReadOnlyCount()));
        Assert.Null(EnvironmentBindingPlan.Create(typeof(Action)));
    }

    [Fact]
    public void CloneRetainsDictionaryGetterOnlyChildrenAndEnforcesItsDepthLimit()
    {
        var source = new Dictionary<string, GetterChild> { ["item"] = new() };
        source["item"].Child.Name = "retained";
        var copy = (Dictionary<string, GetterChild>)new EnvironmentObjectClone(32, default).Copy(source, source.GetType());
        Assert.NotSame(source["item"].Child, copy["item"].Child);
        Assert.Equal("retained", copy["item"].Child.Name);
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(2, default).Copy(source, source.GetType()));
        var provider = new EnvironmentConfigProvider(new EnvironmentFixture([("APP__STATE__PORT", "10")]));
        var original = new StructParent { State = new State { Port = 1, Child = new() } };
        var result = ((IConfigValuePatcher)provider).Patch(new("Production", AppSurfaceConfigKey.Parse("App")), original);
        Assert.Equal(ConfigPatchStatus.Applied, result.Status);
        Assert.Equal(10, result.Value!.State.Port);
        Assert.Equal(1, original.State.Port);
        Assert.NotSame(original.State.Child, result.Value.State.Child);
    }

    [Fact]
    public void GetterOnlyDictionaryUsesExistingPublicCollectionAndPreservesNulls()
    {
        var source = new GetterDictionary();
        source.Values["null"] = null;
        source.Values["node"] = new() { Name = "retained" };
        var copy = (GetterDictionary)new EnvironmentObjectClone(32, default).Copy(source, source.GetType());
        Assert.NotSame(source.Values, copy.Values);
        Assert.Null(copy.Values["null"]);
        Assert.NotSame(source.Values["node"], copy.Values["node"]);
        Assert.Equal("retained", copy.Values["node"]!.Name);
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(new NoDefaultDictionary(1), typeof(NoDefaultDictionary)));
        var shared = new SharedDictionary();
        Assert.Throws<EnvironmentCloneException>(() => new EnvironmentObjectClone(32, default).Copy(shared, shared.GetType()));
        Assert.Empty(shared.Values);
    }

    private sealed class GetterDictionary { public Dictionary<string, Node?> Values { get; } = []; }
    private sealed class NoDefaultDictionary(int value) : Dictionary<string, string> { public int Value { get; } = value; }
    private sealed class SharedDictionary
    {
        private static readonly Dictionary<string, Node?> Shared = [];
        public Dictionary<string, Node?> Values => Shared;
    }

    private sealed class GetterChild { public Node Child { get; } = new(); }
    private sealed class StructParent { public State State { get; set; } }
    private struct State { public int Port { get; set; } public Node Child { get; set; } }

    private sealed class Dag
    {
        public Node First { get; set; } = new();
        public Node Second { get; set; } = new();
        public List<Node?> Items { get; set; } = [];
    }
    private sealed class Node { public string Name { get; set; } = "value"; }
    private sealed class Parameterized(string name) { public string Name { get; } = name; }
    private sealed class ReadOnlyDag
    {
        public ReadOnlyDag() : this(false) { }
        public ReadOnlyDag(bool share) { First = new(); Second = share ? First : new(); }
        public Node First { get; }
        public Node Second { get; }
    }
    private sealed class ReadOnlyCount : IReadOnlyCollection<int>
    {
        public int Count => 1;
        public IEnumerator<int> GetEnumerator() => Enumerable.Repeat(1, 1).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [JsonConverter(typeof(NullCloneConverter))]
    private sealed class NullClone(int value) { public int Value { get; } = value; }
    private sealed class NullCloneConverter : JsonConverter<NullClone>
    {
        public override NullClone? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) { reader.Skip(); return null; }
        public override void Write(Utf8JsonWriter writer, NullClone value, JsonSerializerOptions options) { writer.WriteStartObject(); writer.WriteEndObject(); }
    }
    [JsonConverter(typeof(UnsupportedCloneConverter))]
    private sealed class UnsupportedClone(int value) { public int Value { get; } = value; }
    private sealed class UnsupportedCloneConverter : JsonConverter<UnsupportedClone>
    {
        public override UnsupportedClone Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, UnsupportedClone value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
    [JsonConverter(typeof(NullDictionaryConverter))]
    private sealed class NullDictionary : Dictionary<string, string>;
    private sealed class NullDictionaryConverter : JsonConverter<NullDictionary>
    {
        public override NullDictionary? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) { reader.Skip(); return null; }
        public override void Write(Utf8JsonWriter writer, NullDictionary value, JsonSerializerOptions options) { writer.WriteStartObject(); writer.WriteEndObject(); }
    }
}
