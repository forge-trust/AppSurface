#if FLOW
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

namespace AppSurfaceBenchmarks.Flow;

/// <summary>
/// Benchmarks the overhead of running a small mutable state machine through
/// <see cref="InMemoryFlowRunner{TState}"/> compared with an equivalent direct loop.
/// </summary>
/// <remarks>
/// Use this benchmark when evaluating whether AppSurface.Flow is appropriate for
/// tight state-transition workloads. It intentionally keeps node work minimal so
/// the reported time and allocations primarily reflect runner orchestration
/// rather than business logic. For realistic application flows, add a benchmark
/// whose node body matches the production workload before drawing product-level
/// conclusions.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Arguments)]
[BenchmarkCategory("Flow")]
public class FlowRunnerBenchmarks
{
    private const string CounterNodeId = "counter";

    private FlowDefinition<CounterFlowState> _definition = default!;
    private InMemoryFlowRunner<CounterFlowState> _runner = default!;

    /// <summary>
    /// Gets or sets the number of state transitions to execute in each benchmark operation.
    /// </summary>
    /// <remarks>
    /// BenchmarkDotNet supplies the supported values: 10, 100, and 1000. Values
    /// must be non-negative because both benchmark implementations use this count
    /// as remaining work; the configured values are intentionally small enough to
    /// expose per-step runner overhead without making the benchmark suite slow.
    /// </remarks>
    [Params(10, 100, 1000)]
    public int StepCount { get; set; }

    /// <summary>
    /// Creates the single-node Flow definition and in-memory runner used by the Flow benchmark.
    /// </summary>
    /// <remarks>
    /// The setup gives the runner a <see cref="AppSurfaceFlowOptions.MaxStepsPerRun"/>
    /// value of <c>StepCount + 2</c> so the benchmark has room for every transition
    /// plus the terminal completion pass. Keep that margin when changing the
    /// benchmark shape; otherwise the measurement can become a max-step failure
    /// instead of runner-overhead data.
    /// </remarks>
    [GlobalSetup]
    public void Setup()
    {
        _definition = new FlowDefinition<CounterFlowState>(
            "benchmark.counter",
            "1",
            CounterNodeId,
            new Dictionary<string, FlowNodeDescriptor<CounterFlowState>>(StringComparer.Ordinal)
            {
                [CounterNodeId] = new(
                    CounterNodeId,
                    new CounterNode(),
                    new HashSet<string>(StringComparer.Ordinal) { CounterNodeId }),
            });
        _runner = new InMemoryFlowRunner<CounterFlowState>(
            Options.Create(new AppSurfaceFlowOptions { MaxStepsPerRun = StepCount + 2 }));
    }

    /// <summary>
    /// Runs the counter state machine directly as the baseline for Flow runner overhead.
    /// </summary>
    /// <returns>The number of completed transitions.</returns>
    /// <remarks>
    /// This method uses the same mutable state type and transition semantics as
    /// <see cref="FlowInMemoryRunner"/> without Flow orchestration. Treat it as
    /// the lower-bound cost for this synthetic workload, not as guidance that
    /// direct loops are a replacement for flows that need routing, limits, or
    /// execution results.
    /// </remarks>
    [Benchmark(Baseline = true, Description = "Direct_State_Machine")]
    public int DirectStateMachine()
    {
        var state = new CounterFlowState(StepCount);
        while (state.Remaining > 0)
        {
            state.Remaining--;
            state.CompletedSteps++;
        }

        return state.CompletedSteps;
    }

    /// <summary>
    /// Runs the counter state machine through <see cref="InMemoryFlowRunner{TState}"/>.
    /// </summary>
    /// <returns>The number of completed transitions reported by the completed flow context.</returns>
    /// <remarks>
    /// The benchmark fails fast if the runner does not complete successfully.
    /// Returning zero for faulted or incomplete runs would hide invalid benchmark
    /// data, so status and context are checked before reading the result.
    /// </remarks>
    [Benchmark(Description = "Flow_InMemory_Runner")]
    public async ValueTask<int> FlowInMemoryRunner()
    {
        var result = await _runner.RunAsync(_definition, new CounterFlowState(StepCount));
        if (result.Status != FlowRunStatus.Completed || result.Context is null)
        {
            throw new InvalidOperationException(
                $"Unexpected flow run status for benchmark: {result.Status}.");
        }

        return result.Context.CompletedSteps;
    }

    private sealed class CounterNode : IFlowNode<CounterFlowState>
    {
        public ValueTask<FlowNodeOutcome<CounterFlowState>> ExecuteAsync(
            FlowExecutionContext<CounterFlowState> context,
            CancellationToken cancellationToken = default)
        {
            var state = context.State;
            if (state.Remaining <= 0)
            {
                return ValueTask.FromResult<FlowNodeOutcome<CounterFlowState>>(
                    FlowNodeOutcome<CounterFlowState>.Complete(state));
            }

            state.Remaining--;
            state.CompletedSteps++;
            return ValueTask.FromResult<FlowNodeOutcome<CounterFlowState>>(
                FlowNodeOutcome<CounterFlowState>.Next(CounterNodeId, state));
        }
    }

    private sealed class CounterFlowState
    {
        public CounterFlowState(int remaining)
        {
            Remaining = remaining;
        }

        public int Remaining { get; set; }

        public int CompletedSteps { get; set; }
    }
}

/// <summary>
/// Splits the in-memory runner benchmark into node and graph shapes so remaining overhead can be attributed more clearly.
/// </summary>
/// <remarks>
/// These benchmarks keep the same tiny mutable counter workload as <see cref="FlowRunnerBenchmarks"/> but vary one
/// dimension at a time: synchronous node completion, an async method that still completes synchronously, and two-node
/// routing. Use them before changing runtime APIs so performance work is tied to a measured shape rather than a single
/// self-loop.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Arguments)]
[BenchmarkCategory("Flow")]
public class FlowRunnerShapeBenchmarks
{
    private const string CounterNodeId = "counter";
    private const string EvenNodeId = "even";
    private const string OddNodeId = "odd";

    private FlowDefinition<ShapeCounterState> _singleNodeSyncDefinition = default!;
    private FlowDefinition<ShapeCounterState> _singleNodeAsyncCompletedDefinition = default!;
    private FlowDefinition<ShapeCounterState> _twoNodeDefinition = default!;
    private InMemoryFlowRunner<ShapeCounterState> _runner = default!;

    /// <summary>
    /// Gets or sets the number of state transitions to execute in each benchmark operation.
    /// </summary>
    [Params(10, 100, 1000)]
    public int StepCount { get; set; }

    /// <summary>
    /// Creates definitions for each measured runner shape.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _singleNodeSyncDefinition = CreateSingleNodeDefinition(new SyncCounterNode());
        _singleNodeAsyncCompletedDefinition = CreateSingleNodeDefinition(new AsyncCompletedCounterNode());
        _twoNodeDefinition = new FlowDefinition<ShapeCounterState>(
            "benchmark.shape.two-node",
            "1",
            EvenNodeId,
            new Dictionary<string, FlowNodeDescriptor<ShapeCounterState>>(StringComparer.Ordinal)
            {
                [EvenNodeId] = new(
                    EvenNodeId,
                    new AlternatingCounterNode(OddNodeId),
                    new HashSet<string>(StringComparer.Ordinal) { OddNodeId }),
                [OddNodeId] = new(
                    OddNodeId,
                    new AlternatingCounterNode(EvenNodeId),
                    new HashSet<string>(StringComparer.Ordinal) { EvenNodeId }),
            });
        _runner = new InMemoryFlowRunner<ShapeCounterState>(
            Options.Create(new AppSurfaceFlowOptions { MaxStepsPerRun = StepCount + 2 }));
    }

    /// <summary>
    /// Runs the low-level single-node flow with a node that returns an already-completed <see cref="ValueTask{TResult}"/>.
    /// </summary>
    /// <returns>The number of completed transitions.</returns>
    [Benchmark(Baseline = true, Description = "LowLevel_SingleNode_Sync")]
    public async ValueTask<int> LowLevelSingleNodeSync()
    {
        var result = await _runner.RunAsync(_singleNodeSyncDefinition, new ShapeCounterState(StepCount));
        return CompletedSteps(result);
    }

    /// <summary>
    /// Runs the same low-level graph with an async node method that awaits an already-completed task.
    /// </summary>
    /// <returns>The number of completed transitions.</returns>
    [Benchmark(Description = "LowLevel_SingleNode_AsyncCompleted")]
    public async ValueTask<int> LowLevelSingleNodeAsyncCompleted()
    {
        var result = await _runner.RunAsync(_singleNodeAsyncCompletedDefinition, new ShapeCounterState(StepCount));
        return CompletedSteps(result);
    }

    /// <summary>
    /// Runs a two-node graph that alternates between nodes on every transition.
    /// </summary>
    /// <returns>The number of completed transitions.</returns>
    [Benchmark(Description = "LowLevel_TwoNode_Sync")]
    public async ValueTask<int> LowLevelTwoNodeSync()
    {
        var result = await _runner.RunAsync(_twoNodeDefinition, new ShapeCounterState(StepCount));
        return CompletedSteps(result);
    }

    private static FlowDefinition<ShapeCounterState> CreateSingleNodeDefinition(IFlowNode<ShapeCounterState> node) =>
        new(
            "benchmark.shape.single-node",
            "1",
            CounterNodeId,
            new Dictionary<string, FlowNodeDescriptor<ShapeCounterState>>(StringComparer.Ordinal)
            {
                [CounterNodeId] = new(
                    CounterNodeId,
                    node,
                    new HashSet<string>(StringComparer.Ordinal) { CounterNodeId }),
            });

    private static int CompletedSteps(FlowRunResult<ShapeCounterState> result)
    {
        if (result.Status != FlowRunStatus.Completed || result.Context is null)
        {
            throw new InvalidOperationException(
                $"Unexpected flow run status for benchmark: {result.Status}.");
        }

        return result.Context.CompletedSteps;
    }

    private sealed class SyncCounterNode : IFlowNode<ShapeCounterState>
    {
        public ValueTask<FlowNodeOutcome<ShapeCounterState>> ExecuteAsync(
            FlowExecutionContext<ShapeCounterState> context,
            CancellationToken cancellationToken = default)
        {
            var state = context.State;
            if (state.Remaining <= 0)
            {
                return ValueTask.FromResult<FlowNodeOutcome<ShapeCounterState>>(
                    FlowNodeOutcome<ShapeCounterState>.Complete(state));
            }

            state.Remaining--;
            state.CompletedSteps++;
            return ValueTask.FromResult<FlowNodeOutcome<ShapeCounterState>>(
                FlowNodeOutcome<ShapeCounterState>.Next(CounterNodeId, state));
        }
    }

    private sealed class AsyncCompletedCounterNode : IFlowNode<ShapeCounterState>
    {
        public async ValueTask<FlowNodeOutcome<ShapeCounterState>> ExecuteAsync(
            FlowExecutionContext<ShapeCounterState> context,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            var state = context.State;
            if (state.Remaining <= 0)
            {
                return FlowNodeOutcome<ShapeCounterState>.Complete(state);
            }

            state.Remaining--;
            state.CompletedSteps++;
            return FlowNodeOutcome<ShapeCounterState>.Next(CounterNodeId, state);
        }
    }

    private sealed class AlternatingCounterNode : IFlowNode<ShapeCounterState>
    {
        private readonly string _nextNodeId;

        public AlternatingCounterNode(string nextNodeId)
        {
            _nextNodeId = nextNodeId;
        }

        public ValueTask<FlowNodeOutcome<ShapeCounterState>> ExecuteAsync(
            FlowExecutionContext<ShapeCounterState> context,
            CancellationToken cancellationToken = default)
        {
            var state = context.State;
            if (state.Remaining <= 0)
            {
                return ValueTask.FromResult<FlowNodeOutcome<ShapeCounterState>>(
                    FlowNodeOutcome<ShapeCounterState>.Complete(state));
            }

            state.Remaining--;
            state.CompletedSteps++;
            return ValueTask.FromResult<FlowNodeOutcome<ShapeCounterState>>(
                FlowNodeOutcome<ShapeCounterState>.Next(_nextNodeId, state));
        }
    }

    private sealed class ShapeCounterState
    {
        public ShapeCounterState(int remaining)
        {
            Remaining = remaining;
        }

        public int Remaining { get; set; }

        public int CompletedSteps { get; set; }
    }
}

/// <summary>
/// Measures generated-authoring adapter overhead against the same tiny counter workload.
/// </summary>
/// <remarks>
/// Generated authoring wraps typed node ports in a generated envelope and adapter. This benchmark shows whether the
/// remaining in-memory runner cost is specific to the low-level API or also present in the package-first authoring path.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Arguments)]
[BenchmarkCategory("Flow")]
public class FlowGeneratedAuthoringBenchmarks
{
    private FlowDefinition<GeneratedCounterFlow.GeneratedCounterFlowContext> _definition = default!;
    private InMemoryFlowRunner<GeneratedCounterFlow.GeneratedCounterFlowContext> _runner = default!;

    /// <summary>
    /// Gets or sets the number of generated-authoring state transitions to execute.
    /// </summary>
    [Params(10, 100, 1000)]
    public int StepCount { get; set; }

    /// <summary>
    /// Creates the generated counter definition and in-memory runner.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _definition = GeneratedCounterFlow.BuildDefinition(new GeneratedCounterFlow.CounterNode());
        _runner = new InMemoryFlowRunner<GeneratedCounterFlow.GeneratedCounterFlowContext>(
            Options.Create(new AppSurfaceFlowOptions { MaxStepsPerRun = StepCount + 2 }));
    }

    /// <summary>
    /// Runs the generated-authoring counter through the in-memory runner.
    /// </summary>
    /// <returns>The number of completed transitions.</returns>
    [Benchmark(Description = "GeneratedAuthoring_SingleNode")]
    public async ValueTask<int> GeneratedAuthoringSingleNode()
    {
        var result = await _runner.RunAsync(
            _definition,
            GeneratedCounterFlow.CreateStartContext(new GeneratedCounterState(StepCount)));
        if (result.Status != FlowRunStatus.Completed || result.Context?.GeneratedCounterState is null)
        {
            throw new InvalidOperationException(
                $"Unexpected flow run status for benchmark: {result.Status}.");
        }

        return result.Context.GeneratedCounterState.CompletedSteps;
    }
}

/// <summary>
/// Measures the allocation floor of public Flow outcome factories outside the runner.
/// </summary>
/// <remarks>
/// The runner benchmark's remaining allocation slope should be compared with this factory-only benchmark before
/// proposing larger outcome-shape changes.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Arguments)]
[BenchmarkCategory("Flow")]
public class FlowOutcomeAllocationBenchmarks
{
    private const string CounterNodeId = "counter";
    private static FlowNodeOutcome<OutcomeCounterState>? s_outcomeSink;

    /// <summary>
    /// Gets or sets the number of next outcomes to create.
    /// </summary>
    [Params(10, 100, 1000)]
    public int StepCount { get; set; }

    /// <summary>
    /// Creates the same next-outcome objects a tight runner loop consumes.
    /// </summary>
    /// <returns>A checksum that keeps the created outcomes observable.</returns>
    [Benchmark(Description = "Create_Next_Outcomes")]
    public int CreateNextOutcomes()
    {
        var state = new OutcomeCounterState(StepCount);
        var observed = 0;
        for (var step = 0; step < StepCount; step++)
        {
            var outcome = FlowNodeOutcome<OutcomeCounterState>.Next(CounterNodeId, state);
            s_outcomeSink = outcome;
            observed += outcome.NodeId.Length;
            state.CompletedSteps++;
        }

        return observed + state.CompletedSteps;
    }

    /// <summary>
    /// Creates next outcomes plus the terminal complete outcome used by successful runner operations.
    /// </summary>
    /// <returns>A checksum that keeps the created outcomes observable.</returns>
    [Benchmark(Description = "Create_Next_And_Complete_Outcomes")]
    public int CreateNextAndCompleteOutcomes()
    {
        var state = new OutcomeCounterState(StepCount);
        var observed = 0;
        for (var step = 0; step < StepCount; step++)
        {
            var outcome = FlowNodeOutcome<OutcomeCounterState>.Next(CounterNodeId, state);
            s_outcomeSink = outcome;
            observed += outcome.NodeId.Length;
            state.CompletedSteps++;
        }

        var complete = FlowNodeOutcome<OutcomeCounterState>.Complete(state);
        s_outcomeSink = complete;
        return observed + complete.Context.CompletedSteps;
    }

    private sealed class OutcomeCounterState
    {
        public OutcomeCounterState(int remaining)
        {
            Remaining = remaining;
        }

        public int Remaining { get; }

        public int CompletedSteps { get; set; }
    }
}

/// <summary>
/// Measures generated-authoring outcome and envelope factory allocation outside the runner.
/// </summary>
/// <remarks>
/// Generated authoring creates typed outcome cases and envelope contexts before lowering to the low-level
/// <see cref="FlowNodeOutcome{TContext}"/> API. Use this benchmark to decide whether generated outcome cases or
/// envelope wrapping are responsible for the gap between low-level and generated-authoring runner measurements.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[HideColumns(Column.Arguments)]
[BenchmarkCategory("Flow")]
public class FlowGeneratedFactoryAllocationBenchmarks
{
    private static GeneratedCounterFlow.CounterNodeOutcomes? s_outcomeSink;
    private static GeneratedCounterFlow.GeneratedCounterFlowContext? s_envelopeSink;

    /// <summary>
    /// Gets or sets the number of generated factory calls to execute.
    /// </summary>
    [Params(10, 100, 1000)]
    public int StepCount { get; set; }

    /// <summary>
    /// Creates generated next-outcome cases without invoking the runner.
    /// </summary>
    /// <returns>A checksum that keeps the created outcomes observable.</returns>
    [Benchmark(Description = "Create_Generated_Next_Outcomes")]
    public int CreateGeneratedNextOutcomes()
    {
        var state = new GeneratedCounterState(StepCount);
        var observed = 0;
        for (var step = 0; step < StepCount; step++)
        {
            var outcome = GeneratedCounterFlow.CounterNodeOutcomes.Next(state);
            s_outcomeSink = outcome;
            observed += outcome.Context.Remaining;
            state.CompletedSteps++;
        }

        return observed + state.CompletedSteps;
    }

    /// <summary>
    /// Creates generated envelopes for the counter node without invoking the runner.
    /// </summary>
    /// <returns>A checksum that keeps the created envelopes observable.</returns>
    [Benchmark(Description = "Create_Generated_Envelopes")]
    public int CreateGeneratedEnvelopes()
    {
        var state = new GeneratedCounterState(StepCount);
        var observed = 0;
        for (var step = 0; step < StepCount; step++)
        {
            var envelope = GeneratedCounterFlow.GeneratedCounterFlowContext.ForCounterNode(state);
            s_envelopeSink = envelope;
            observed += envelope.GeneratedCounterState?.Remaining ?? 0;
            state.CompletedSteps++;
        }

        return observed + state.CompletedSteps;
    }
}

/// <summary>
/// Generated-authoring benchmark flow for a single-node counter.
/// </summary>
[FlowAuthoring("benchmark.generated-counter")]
internal partial class GeneratedCounterFlow
{
    /// <summary>
    /// Counter node that loops until the generated state has no remaining transitions.
    /// </summary>
    [FlowStart]
    [FlowNode("counter", typeof(GeneratedCounterState))]
    [FlowOutcome("next", FlowOutcomeKind.Next, typeof(GeneratedCounterState))]
    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(GeneratedCounterState))]
    internal partial class CounterNode : IFlowTransformerNode<GeneratedCounterState, CounterNodeOutcomes>
    {
        public ValueTask<CounterNodeOutcomes> ExecuteAsync(
            FlowTransformerContext<GeneratedCounterState> context,
            CancellationToken cancellationToken = default)
        {
            var state = context.State;
            if (state.Remaining <= 0)
            {
                return ValueTask.FromResult<CounterNodeOutcomes>(
                    CounterNodeOutcomes.Done(state));
            }

            state.Remaining--;
            state.CompletedSteps++;
            return ValueTask.FromResult<CounterNodeOutcomes>(
                CounterNodeOutcomes.Next(state));
        }
    }
}

/// <summary>
/// Mutable generated-authoring counter state used to keep benchmark node work minimal.
/// </summary>
internal sealed class GeneratedCounterState
{
    public GeneratedCounterState(int remaining)
    {
        Remaining = remaining;
    }

    public int Remaining { get; set; }

    public int CompletedSteps { get; set; }
}
#endif
