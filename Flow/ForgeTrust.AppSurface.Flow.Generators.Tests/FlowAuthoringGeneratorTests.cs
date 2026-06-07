using ForgeTrust.AppSurface.Flow.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ForgeTrust.AppSurface.Flow.Generators.Tests;

public sealed class FlowAuthoringGeneratorTests
{
    [Fact]
    public void Generator_WithValidGeneratedAuthoring_EmitsEnvelopeOutcomesAndBuilder()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestApp;

            [FlowAuthoring("approval", Version = "2026-06-03")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("public sealed record ApprovalFlowContext", generated, StringComparison.Ordinal);
        Assert.Contains("public abstract partial record StartNodeOutcomes", generated, StringComparison.Ordinal);
        Assert.Contains("FlowGraphMapping(\"start\", \"review\", typeof(global::TestApp.ReviewContext))", generated, StringComparison.Ordinal);
        Assert.Contains("public static global::ForgeTrust.AppSurface.Flow.FlowDefinition<ApprovalFlowContext> BuildDefinition", generated, StringComparison.Ordinal);
        Assert.Contains("public sealed class GraphBuilder", generated, StringComparison.Ordinal);
        Assert.Contains("MapStartNodeReviewToReviewNode", generated, StringComparison.Ordinal);
        Assert.Contains("MarkReviewNodeDoneTerminal", generated, StringComparison.Ordinal);
        Assert.Contains("BuildDefinition(global::System.Action<GraphBuilder> configureGeneratedGraph", generated, StringComparison.Ordinal);
        Assert.Contains("FlowNodeOutcome<ApprovalFlowContext>.Next(\"review\"", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithAllOutcomeKinds_EmitsTypedCasesAndLowering()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestApp;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                [FlowOutcome("submitted", FlowOutcomeKind.Wait, typeof(StartContext))]
                [FlowOutcome("expired", FlowOutcomeKind.TimedOut, typeof(StartContext))]
                [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(FlowFault))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Submitted(context.State));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("FlowNodeOutcome<ApprovalFlowContext>.Next(\"review\"", generated, StringComparison.Ordinal);
        Assert.Contains("FlowNodeOutcome<ApprovalFlowContext>.Wait(\"submitted\"", generated, StringComparison.Ordinal);
        Assert.Contains("FlowNodeOutcome<ApprovalFlowContext>.TimedOut(\"expired\"", generated, StringComparison.Ordinal);
        Assert.Contains("FlowNodeOutcome<ApprovalFlowContext>.Fault(typed.Context.Code, typed.Context.Message)", generated, StringComparison.Ordinal);
        Assert.Contains("FlowTimeout? Timeout = null", generated, StringComparison.Ordinal);
        Assert.Contains("MarkStartNodeSubmittedTerminal", generated, StringComparison.Ordinal);
        Assert.Contains("MarkStartNodeExpiredTerminal", generated, StringComparison.Ordinal);
        Assert.Contains("MarkStartNodeDeniedTerminal", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithExplicitGraphConfigurationMissingOutcome_ReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph => graph.MapStartNodeReviewToReviewNode(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithMappingNameOnHelperObject_StillReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create()
                {
                    var helper = new Helper();
                    return ApprovalFlow.BuildDefinition(
                        graph =>
                        {
                            graph.MapStartNodeReviewToReviewNode();
                            helper.MarkReviewNodeDoneTerminal();
                        },
                        new ApprovalFlow.StartNode(),
                        new ApprovalFlow.ReviewNode());
                }
            }

            public sealed class Helper
            {
                public void MarkReviewNodeDoneTerminal() { }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithUnqualifiedExplicitBuildDefinitionInFlow_ReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }

                public static object Create() => BuildDefinition(
                    graph => graph.MapStartNodeReviewToReviewNode(),
                    new StartNode(),
                    new ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithStaticImportedExplicitBuildDefinition_ReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using static ApprovalFlow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => BuildDefinition(
                    graph => graph.MapStartNodeReviewToReviewNode(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithNamedExplicitGraphConfiguration_ReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    startNode: new ApprovalFlow.StartNode(),
                    reviewNode: new ApprovalFlow.ReviewNode(),
                    configureGeneratedGraph: graph => graph.MapStartNodeReviewToReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithNamedCompactNodeArguments_DoesNotReportExplicitMappingDiagnostics()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create()
                {
                    var start = new ApprovalFlow.StartNode();
                    var review = new ApprovalFlow.ReviewNode();
                    return ApprovalFlow.BuildDefinition(startNode: start, reviewNode: review);
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (output, diagnostics, _) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generator_WithCompactNodeExpressions_DoesNotReportExplicitMappingDiagnostics()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create(bool chooseFirst) => ApprovalFlow.BuildDefinition(
                    (ApprovalFlow.StartNode)CreateStart(),
                    chooseFirst ? new ApprovalFlow.ReviewNode() : new ApprovalFlow.ReviewNode());

                private static object CreateStart() => new ApprovalFlow.StartNode();
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (output, diagnostics, _) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generator_WithDeferredMappingCall_ReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph =>
                    {
                        void Later() => graph.MarkReviewNodeDoneTerminal();
                        graph.MapStartNodeReviewToReviewNode();
                    },
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithConditionalMappingCall_ReportsMissingMapping()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph =>
                    {
                        graph.MapStartNodeReviewToReviewNode();
                        if (false)
                        {
                            graph.MarkReviewNodeDoneTerminal();
                        }
                    },
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithConditionalDuplicateMappingCall_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph =>
                    {
                        graph.MapStartNodeReviewToReviewNode();
                        graph.MarkReviewNodeDoneTerminal();
                        if (true)
                        {
                            graph.MarkReviewNodeDoneTerminal();
                        }
                    },
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithDuplicateDirectMappingCall_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph => graph
                        .MapStartNodeReviewToReviewNode()
                        .MarkReviewNodeDoneTerminal()
                        .MarkReviewNodeDoneTerminal(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithParenthesizedGraphBuilderChain_DoesNotReportExplicitMappingDiagnostics()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph => (graph)
                        .MapStartNodeReviewToReviewNode()
                        .MarkReviewNodeDoneTerminal(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (output, diagnostics, _) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generator_WithMethodGroupGraphConfiguration_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    ConfigureGraph,
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());

                private static void ConfigureGraph(object graph)
                {
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithMultipleParameterGraphConfiguration_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    (first, second) => first.MapStartNodeReviewToReviewNode().MarkReviewNodeDoneTerminal(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithDelegateVariableGraphConfiguration_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public static class FlowFactory
            {
                public static object Create()
                {
                    Action<ApprovalFlow.GraphBuilder> configure = graph => graph.MapStartNodeReviewToReviewNode();
                    return ApprovalFlow.BuildDefinition(
                        configure,
                        new ApprovalFlow.StartNode(),
                        new ApprovalFlow.ReviewNode());
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithReferencedFlowExplicitGraphConfigurationMissingOutcome_ReportsMissingMapping()
    {
        var librarySource = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Library;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (libraryOutput, libraryDiagnostics, _) = RunGenerator(librarySource);
        Assert.DoesNotContain(libraryDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var libraryReference = EmitReference(libraryOutput);
        var hostSource = """
            using Library;

            public static class HostFlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph => graph.MapStartNodeReviewToReviewNode(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }
            """;

        var (_, hostDiagnostics, _) = RunGenerator(hostSource, libraryReference);

        Assert.Contains(hostDiagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
    }

    [Fact]
    public void Generator_WithDigitStartingOutcomeName_EmitsDiscoverableCaseName()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("2fa", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Outcome2fa(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Outcome2faOutcome", generated, StringComparison.Ordinal);
        Assert.Contains("MarkStartNodeOutcome2faTerminal", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithEscapedDurableIdentifiers_EmitsCompilableSource()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval\nrequest")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start\nnode", typeof(StartContext))]
                [FlowOutcome("done\noutcome", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.DoneOutcome(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("approval\\nrequest", generated, StringComparison.Ordinal);
        Assert.Contains("start\\nnode", generated, StringComparison.Ordinal);
        Assert.Contains("done\\noutcome", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithSpecialCharacterDurableIdentifiers_EmitsEscapedStringLiterals()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval\\quote\"nul\0alert\aback\bform\fnewline\nreturn\rtab\tvertical\vunit\u001f")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start\\quote\"nul\0alert\aback\bform\fnewline\nreturn\rtab\tvertical\vunit\u001f", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(@"approval\\quote\""nul\0alert\aback\bform\fnewline\nreturn\rtab\tvertical\vunit\u001f", generated, StringComparison.Ordinal);
        Assert.Contains(@"start\\quote\""nul\0alert\aback\bform\fnewline\nreturn\rtab\tvertical\vunit\u001f", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithOutcomeKeyPunctuationCollisions_EmitsDistinctMappingKeys()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("a.b", typeof(FirstContext))]
                [FlowOutcome("c", FlowOutcomeKind.Complete, typeof(FirstContext))]
                public partial class FirstNode : IFlowTransformerNode<FirstContext, FirstNodeOutcomes>
                {
                    public ValueTask<FirstNodeOutcomes> ExecuteAsync(FlowTransformerContext<FirstContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<FirstNodeOutcomes>(FirstNodeOutcomes.C(context.State));
                }

                [FlowNode("a", typeof(SecondContext))]
                [FlowOutcome("b.c", FlowOutcomeKind.Complete, typeof(SecondContext))]
                public partial class SecondNode : IFlowTransformerNode<SecondContext, SecondNodeOutcomes>
                {
                    public ValueTask<SecondNodeOutcomes> ExecuteAsync(FlowTransformerContext<SecondContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<SecondNodeOutcomes>(SecondNodeOutcomes.BC(context.State));
                }
            }

            public sealed record FirstContext;
            public sealed record SecondContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("\"3:a.b|1:c\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"1:a|3:b.c\"", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithMissingNextTarget_ReportsMappingDiagnostics()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("missing", FlowOutcomeKind.Next, typeof(MissingContext))]
                public partial class StartNode
                {
                }
            }

            public sealed record StartContext;
            public sealed record MissingContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA002");
    }

    [Fact]
    public void Generator_WithDuplicateStartNodes_ReportsInvalidStart()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("one", typeof(OneContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(OneContext))]
                public partial class OneNode
                {
                }

                [FlowStart]
                [FlowNode("two", typeof(TwoContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(TwoContext))]
                public partial class TwoNode
                {
                }
            }

            public sealed record OneContext;
            public sealed record TwoContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA004");
    }

    [Fact]
    public void Generator_WithHandWrittenBuildDefinition_ReportsLowLevelMixWarning()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                public static object BuildDefinition() => new();

                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode
                {
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "ASFLOWA006" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Generator_WithSameFlowNameInDifferentNamespaces_EmitsBothFlows()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace One
            {
                [FlowAuthoring("one")]
                public partial class ApprovalFlow
                {
                    [FlowStart]
                    [FlowNode("start", typeof(StartContext))]
                    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                    public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                    {
                        public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                            ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                    }
                }

                public sealed record StartContext;
            }

            namespace Two
            {
                [FlowAuthoring("two")]
                public partial class ApprovalFlow
                {
                    [FlowStart]
                    [FlowNode("start", typeof(StartContext))]
                    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                    public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                    {
                        public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                            ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                    }
                }

                public sealed record StartContext;
            }
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("namespace One;", generated, StringComparison.Ordinal);
        Assert.Contains("namespace Two;", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithNormalizedHintNameCollision_EmitsBothFlows()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace A.B
            {
                [FlowAuthoring("one")]
                public partial class ApprovalFlow
                {
                    [FlowStart]
                    [FlowNode("start", typeof(StartContext))]
                    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                    public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                    {
                        public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                            ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                    }
                }

                public sealed record StartContext;
            }

            namespace AB
            {
                [FlowAuthoring("two")]
                public partial class ApprovalFlow
                {
                    [FlowStart]
                    [FlowNode("start", typeof(StartContext))]
                    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                    public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                    {
                        public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                            ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                    }
                }

                public sealed record StartContext;
            }
            """;

        var (output, diagnostics, _) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, output.SyntaxTrees.Count(tree => tree.FilePath.EndsWith(".FlowAuthoring.g.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void Generator_WithSplitPartialFlowDeclaration_EmitsOneFlow()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
            }

            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Single(output.SyntaxTrees, tree => tree.FilePath.EndsWith(".FlowAuthoring.g.cs", StringComparison.Ordinal));
        Assert.Contains("public sealed record ApprovalFlowContext", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithInternalFlow_EmitsInternalEnvelope()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            internal partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("internal sealed record ApprovalFlowContext", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithNestedFlowAuthoringType_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            public partial class Container
            {
                [FlowAuthoring("approval")]
                public partial class ApprovalFlow
                {
                    [FlowStart]
                    [FlowNode("start", typeof(StartContext))]
                    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                    public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                    {
                        public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                            ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                    }
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithGenericFlowAuthoringType_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow<T>
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithNonPartialFlowAuthoringType_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithStaticFlowAuthoringType_ReportsInvalidDeclarationWithoutGeneratedCompilerError()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public static partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode
                {
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_WithRecordFlowAuthoringType_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;

            [FlowAuthoring("approval")]
            public partial record ApprovalFlow
            {
            }
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithDuplicateNodeIds_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("same", typeof(StartContext))]
                [FlowOutcome("next", FlowOutcomeKind.Next, typeof(NextContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Next(new NextContext()));
                }

                [FlowNode("same", typeof(NextContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(NextContext))]
                public partial class NextNode : IFlowTransformerNode<NextContext, NextNodeOutcomes>
                {
                    public ValueTask<NextNodeOutcomes> ExecuteAsync(FlowTransformerContext<NextContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<NextNodeOutcomes>(NextNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            public sealed record NextContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithPublicFlowAndInternalNode_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                internal partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithPublicFlowAndInternalContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            internal sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithPublicFlowAndInternalGenericContextArgument_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(Box<InternalState>))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(Box<InternalState>))]
                public partial class StartNode : IFlowTransformerNode<Box<InternalState>, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<Box<InternalState>> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record Box<T>(T Value);
            internal sealed record InternalState;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithPublicFlowAndInternalArrayContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(InternalState[]))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(InternalState[]))]
                public partial class StartNode : IFlowTransformerNode<InternalState[], StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<InternalState[]> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            internal sealed record InternalState;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithExistingGeneratedHelperName_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                public static object CreateStartContext() => new();

                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithExistingGeneratedNestedTypeName_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                public sealed record ApprovalFlowContext;

                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithKeywordFlowName_EmitsEscapedIdentifiers()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace @event
            {
                [FlowAuthoring("approval")]
                public partial class @class
                {
                    [FlowStart]
                    [FlowNode("start", typeof(StartContext))]
                    [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                    public partial class @params : IFlowTransformerNode<StartContext, paramsOutcomes>
                    {
                        public ValueTask<paramsOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                            ValueTask.FromResult<paramsOutcomes>(paramsOutcomes.Done(context.State));
                    }
                }

                public sealed record StartContext;
            }
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("namespace @event;", generated, StringComparison.Ordinal);
        Assert.Contains("partial class @class", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithWaitContextMismatch_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("submitted", FlowOutcomeKind.Wait, typeof(OtherContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Submitted(new OtherContext()));
                }
            }

            public sealed record StartContext;
            public sealed record OtherContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithCustomFaultContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(CustomFault))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Denied(new CustomFault()));
                }
            }

            public sealed record StartContext;
            public sealed record CustomFault;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithAliasedFlowFaultContext_DoesNotReportInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using Failure = ForgeTrust.AppSurface.Flow.FlowFault;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("denied", FlowOutcomeKind.Fault, typeof(Failure))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Denied(new Failure("denied", "Denied.")));
                }
            }

            public sealed record StartContext;
            """;

        var (output, diagnostics, generated) = RunGenerator(source);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("FlowNodeOutcome<ApprovalFlowContext>.Fault(typed.Context.Code, typed.Context.Message)", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_WithNodeWithoutOutcomes_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(null!);
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithAmbiguousNextTarget_ReportsIncompatibleTarget()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review-one", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(ReviewContext))]
                public partial class FirstReviewNode : IFlowTransformerNode<ReviewContext, FirstReviewNodeOutcomes>
                {
                    public ValueTask<FirstReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<FirstReviewNodeOutcomes>(FirstReviewNodeOutcomes.Done(context.State));
                }

                [FlowNode("review-two", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(ReviewContext))]
                public partial class SecondReviewNode : IFlowTransformerNode<ReviewContext, SecondReviewNodeOutcomes>
                {
                    public ValueTask<SecondReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<SecondReviewNodeOutcomes>(SecondReviewNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA003");
    }

    [Fact]
    public void Generator_WithDuplicateOutcomeCaseNames_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done-now", FlowOutcomeKind.Complete, typeof(StartContext))]
                [FlowOutcome("done_now", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.DoneNow(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithReferencedFlowDelegateVariableGraphConfiguration_ReportsInvalidDeclaration()
    {
        var librarySource = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Library;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (libraryOutput, libraryDiagnostics, _) = RunGenerator(librarySource);
        Assert.DoesNotContain(libraryDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var libraryReference = EmitReference(libraryOutput);
        var hostSource = """
            using Library;
            using System;

            public static class HostFlowFactory
            {
                public static object Create()
                {
                    Action<ApprovalFlow.GraphBuilder> configure = graph => graph
                        .MapStartNodeReviewToReviewNode()
                        .MarkReviewNodeDoneTerminal();

                    return ApprovalFlow.BuildDefinition(
                        configure,
                        new ApprovalFlow.StartNode(),
                        new ApprovalFlow.ReviewNode());
                }
            }
            """;

        var (_, hostDiagnostics, _) = RunGenerator(hostSource, libraryReference);

        Assert.Contains(hostDiagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithReferencedFlowConditionalMapping_ReportsInvalidDeclaration()
    {
        var librarySource = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Library;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (libraryOutput, libraryDiagnostics, _) = RunGenerator(librarySource);
        Assert.DoesNotContain(libraryDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var libraryReference = EmitReference(libraryOutput);
        var hostSource = """
            using Library;

            public static class HostFlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph =>
                    {
                        graph.MapStartNodeReviewToReviewNode();
                        if (true)
                        {
                            graph.MarkReviewNodeDoneTerminal();
                        }
                    },
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }
            """;

        var (_, hostDiagnostics, _) = RunGenerator(hostSource, libraryReference);

        Assert.Contains(hostDiagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithReferencedFlowDuplicateMappingCall_ReportsInvalidDeclaration()
    {
        var librarySource = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Library;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("review", FlowOutcomeKind.Next, typeof(ReviewContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Review(new ReviewContext()));
                }

                [FlowNode("review", typeof(ReviewContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(DoneContext))]
                public partial class ReviewNode : IFlowTransformerNode<ReviewContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<ReviewContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Done(new DoneContext()));
                }
            }

            public sealed record StartContext;
            public sealed record ReviewContext;
            public sealed record DoneContext;
            """;

        var (libraryOutput, libraryDiagnostics, _) = RunGenerator(librarySource);
        Assert.DoesNotContain(libraryDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var libraryReference = EmitReference(libraryOutput);
        var hostSource = """
            using Library;

            public static class HostFlowFactory
            {
                public static object Create() => ApprovalFlow.BuildDefinition(
                    graph => graph
                        .MapStartNodeReviewToReviewNode()
                        .MarkReviewNodeDoneTerminal()
                        .MarkReviewNodeDoneTerminal(),
                    new ApprovalFlow.StartNode(),
                    new ApprovalFlow.ReviewNode());
            }
            """;

        var (_, hostDiagnostics, _) = RunGenerator(hostSource, libraryReference);

        Assert.Contains(hostDiagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithDuplicateContextSlotNames_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(One.State))]
                [FlowOutcome("next", FlowOutcomeKind.Next, typeof(Two.State))]
                public partial class StartNode : IFlowTransformerNode<One.State, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<One.State> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Next(new Two.State()));
                }

                [FlowNode("next", typeof(Two.State))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(Two.State))]
                public partial class NextNode : IFlowTransformerNode<Two.State, NextNodeOutcomes>
                {
                    public ValueTask<NextNodeOutcomes> ExecuteAsync(FlowTransformerContext<Two.State> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<NextNodeOutcomes>(NextNodeOutcomes.Done(context.State));
                }
            }

            namespace One
            {
                public sealed record State;
            }

            namespace Two
            {
                public sealed record State;
            }
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithDuplicateGeneratedBuildDefinitionParameterNames_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("next", FlowOutcomeKind.Next, typeof(NextContext))]
                public partial class ReviewNode : IFlowTransformerNode<StartContext, ReviewNodeOutcomes>
                {
                    public ValueTask<ReviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<ReviewNodeOutcomes>(ReviewNodeOutcomes.Next(new NextContext()));
                }

                [FlowNode("next", typeof(NextContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(NextContext))]
                public partial class reviewNode : IFlowTransformerNode<NextContext, reviewNodeOutcomes>
                {
                    public ValueTask<reviewNodeOutcomes> ExecuteAsync(FlowTransformerContext<NextContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<reviewNodeOutcomes>(reviewNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            public sealed record NextContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithSameNamedExternalOutcomeType_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            namespace External
            {
                public abstract record StartNodeOutcomes;
            }

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, External.StartNodeOutcomes>
                {
                    public ValueTask<External.StartNodeOutcomes> ExecuteAsync(
                        FlowTransformerContext<StartContext> context,
                        CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<External.StartNodeOutcomes>(new Done());

                    private sealed record Done : External.StartNodeOutcomes;
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithMissingTransformerInterface_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode
                {
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithInvalidOutcomeKind_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", (FlowOutcomeKind)999, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithInvalidDurableIdentifiers_ReportsInvalidDeclarations()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring(null, Version = "")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("", typeof(StartContext))]
                [FlowOutcome("", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.True(diagnostics.Count(diagnostic => diagnostic.Id == "ASFLOWA005") >= 4);
    }

    [Fact]
    public void Generator_WithContextNameCollidingWithEnvelopeFixedMember_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(NodeId))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(NodeId))]
                public partial class StartNode : IFlowTransformerNode<NodeId, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<NodeId> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record NodeId;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithContextNameCollidingWithEnvelopeHelper_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(AsStartNode))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(AsStartNode))]
                public partial class StartNode : IFlowTransformerNode<AsStartNode, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<AsStartNode> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record AsStartNode;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithMalformedAuthoringAttributes_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;

            [FlowAuthoring]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start")]
                [FlowOutcome("done", FlowOutcomeKind.Complete)]
                public partial class StartNode
                {
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithGenericNodeAuthoringType_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode<T> : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithVoidInputContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(void))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(StartContext))]
                public partial class StartNode : IFlowTransformerNode<void, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<void> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(new StartContext()));
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithVoidOutcomeContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(StartContext))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(void))]
                public partial class StartNode : IFlowTransformerNode<StartContext, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<StartContext> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done());
                }
            }

            public sealed record StartContext;
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithNullableValueTypeContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(int?))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(int?))]
                public partial class StartNode : IFlowTransformerNode<int?, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<int?> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    [Fact]
    public void Generator_WithOpenGenericContext_ReportsInvalidDeclaration()
    {
        var source = """
            using ForgeTrust.AppSurface.Flow;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;

            [FlowAuthoring("approval")]
            public partial class ApprovalFlow
            {
                [FlowStart]
                [FlowNode("start", typeof(List<>))]
                [FlowOutcome("done", FlowOutcomeKind.Complete, typeof(List<>))]
                public partial class StartNode : IFlowTransformerNode<List<object>, StartNodeOutcomes>
                {
                    public ValueTask<StartNodeOutcomes> ExecuteAsync(FlowTransformerContext<List<object>> context, CancellationToken cancellationToken = default) =>
                        ValueTask.FromResult<StartNodeOutcomes>(StartNodeOutcomes.Done(context.State));
                }
            }
            """;

        var (_, diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "ASFLOWA005");
    }

    private static (Compilation Output, IReadOnlyList<Diagnostic> Diagnostics, string Generated) RunGenerator(
        string source,
        params MetadataReference[] additionalReferences)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratedFlowTests",
            [CSharpSyntaxTree.ParseText(source)],
            TrustedPlatformReferences()
                .Append(MetadataReference.CreateFromFile(typeof(FlowAuthoringAttribute).Assembly.Location))
                .Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new FlowAuthoringGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var result = driver.GetRunResult();
        var generated = string.Concat(result
            .GeneratedTrees
            .Select(tree => tree.GetText().ToString()));

        return (output, diagnostics.Concat(result.Diagnostics).ToArray(), generated);
    }

    private static MetadataReference EmitReference(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static IEnumerable<MetadataReference> TrustedPlatformReferences()
    {
        var assemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        return assemblies.Select(path => MetadataReference.CreateFromFile(path));
    }
}
