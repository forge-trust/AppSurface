#if FLOW
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

namespace AppSurfaceBenchmarks.Flow;

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

    [Params(10, 100, 1000)]
    public int StepCount { get; set; }

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

    [Benchmark(Description = "Flow_InMemory_Runner")]
    public async ValueTask<int> FlowInMemoryRunner()
    {
        var result = await _runner.RunAsync(_definition, new CounterFlowState(StepCount));
        return result.Context?.CompletedSteps ?? 0;
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
