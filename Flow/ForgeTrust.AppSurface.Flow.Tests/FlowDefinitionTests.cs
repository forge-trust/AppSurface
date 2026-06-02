namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowDefinitionTests
{
    [Fact]
    public void Constructor_CopiesNodeDictionary()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new CompleteNode()),
        };

        var definition = new FlowDefinition<TestState>("approval", "1", "start", nodes);
        nodes["late"] = Descriptor("late", new CompleteNode());

        Assert.DoesNotContain("late", definition.Nodes.Keys);
    }

    [Fact]
    public void Constructor_CopiesDescriptorNextNodeIds()
    {
        var nextNodeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "finish",
        };
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = new("start", new CompleteNode(), nextNodeIds),
            ["finish"] = Descriptor("finish", new CompleteNode()),
        };

        var definition = new FlowDefinition<TestState>("approval", "1", "start", nodes);
        nextNodeIds.Add("late");

        Assert.DoesNotContain("late", definition.Nodes["start"].NextNodeIds);
    }

    [Fact]
    public void Constructor_WithEmptyNodeDictionary_ThrowsFlowDefinitionException()
    {
        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>(
                "approval",
                "1",
                "start",
                new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)));

        Assert.Contains("does not contain any nodes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithNullDescriptor_ThrowsArgumentException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>?>(StringComparer.Ordinal)
        {
            ["start"] = null,
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new FlowDefinition<TestState>("approval", "1", "start", new NullableDescriptorDictionary(nodes)));

        Assert.Equal("nodes", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithDuplicateEnumeratedNodeKey_ThrowsFlowDefinitionException()
    {
        var descriptor = Descriptor("start", new CompleteNode());
        var nodes = new DuplicateKeyDictionary<string, FlowNodeDescriptor<TestState>>("start", descriptor);

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "start", nodes));

        Assert.Contains("declares node 'start' more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReturnsDefinitionThatDoesNotTrackBuilderMutation()
    {
        var builder = FlowGraphBuilder<TestState>
            .Create("approval")
            .AddNode("start", new CompleteNode())
            .StartAt("start");

        var definition = builder.Build();
        builder.AddNode("late", new CompleteNode());

        Assert.DoesNotContain("late", definition.Nodes.Keys);
    }

    [Fact]
    public void Constructor_WithMissingStartNode_ThrowsFlowDefinitionException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new CompleteNode()),
        };

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "missing", nodes));

        Assert.Contains("start node 'missing' does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithMismatchedDescriptorKey_ThrowsFlowDefinitionException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("other", new CompleteNode()),
        };

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "start", nodes));

        Assert.Contains("does not match descriptor node id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithMissingNextTarget_ThrowsFlowDefinitionException()
    {
        var nodes = new Dictionary<string, FlowNodeDescriptor<TestState>>(StringComparer.Ordinal)
        {
            ["start"] = Descriptor("start", new CompleteNode(), "missing"),
        };

        var exception = Assert.Throws<FlowDefinitionException>(() =>
            new FlowDefinition<TestState>("approval", "1", "start", nodes));

        Assert.Contains("targets missing node 'missing'", exception.Message, StringComparison.Ordinal);
    }

    private static FlowNodeDescriptor<TestState> Descriptor(
        string nodeId,
        IFlowNode<TestState> node,
        params string[] nextNodeIds) =>
        new(nodeId, node, new HashSet<string>(nextNodeIds, StringComparer.Ordinal));

    private sealed class NullableDescriptorDictionary :
        IReadOnlyDictionary<string, FlowNodeDescriptor<TestState>>
    {
        private readonly Dictionary<string, FlowNodeDescriptor<TestState>?> _nodes;

        internal NullableDescriptorDictionary(Dictionary<string, FlowNodeDescriptor<TestState>?> nodes)
        {
            _nodes = nodes;
        }

        public IEnumerable<string> Keys => _nodes.Keys;

        public IEnumerable<FlowNodeDescriptor<TestState>> Values => _nodes.Values!;

        public int Count => _nodes.Count;

        public FlowNodeDescriptor<TestState> this[string key] => _nodes[key]!;

        public bool ContainsKey(string key) => _nodes.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, FlowNodeDescriptor<TestState>>> GetEnumerator() =>
            _nodes.Select(item => new KeyValuePair<string, FlowNodeDescriptor<TestState>>(item.Key, item.Value!)).GetEnumerator();

        public bool TryGetValue(string key, out FlowNodeDescriptor<TestState> value)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                value = node!;
                return true;
            }

            value = null!;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DuplicateKeyDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly TKey _key;
        private readonly TValue _value;

        internal DuplicateKeyDictionary(TKey key, TValue value)
        {
            _key = key;
            _value = value;
        }

        public IEnumerable<TKey> Keys => [_key];

        public IEnumerable<TValue> Values => [_value];

        public int Count => 2;

        public TValue this[TKey key] => EqualityComparer<TKey>.Default.Equals(key, _key)
            ? _value
            : throw new KeyNotFoundException();

        public bool ContainsKey(TKey key) => EqualityComparer<TKey>.Default.Equals(key, _key);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            yield return new KeyValuePair<TKey, TValue>(_key, _value);
            yield return new KeyValuePair<TKey, TValue>(_key, _value);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (ContainsKey(key))
            {
                value = _value;
                return true;
            }

            value = default!;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record TestState(string Value);

    private sealed class CompleteNode : IFlowNode<TestState>
    {
        public ValueTask<FlowNodeOutcome<TestState>> ExecuteAsync(
            FlowExecutionContext<TestState> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<TestState>>(
                FlowNodeOutcome<TestState>.Complete(context.State));
    }
}
