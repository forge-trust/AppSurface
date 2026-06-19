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
#endif
