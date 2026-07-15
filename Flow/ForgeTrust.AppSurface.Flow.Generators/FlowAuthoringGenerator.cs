using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ForgeTrust.AppSurface.Flow.Generators;

[Generator]
internal sealed class FlowAuthoringGenerator : IIncrementalGenerator
{
    private const string FlowAuthoringAttributeName = "ForgeTrust.AppSurface.Flow.FlowAuthoringAttribute";
    private const string FlowNodeAttributeName = "ForgeTrust.AppSurface.Flow.FlowNodeAttribute";
    private const string FlowStartAttributeName = "ForgeTrust.AppSurface.Flow.FlowStartAttribute";
    private const string FlowOutcomeAttributeName = "ForgeTrust.AppSurface.Flow.FlowOutcomeAttribute";
    private const string FlowGraphMappingAttributeName = "ForgeTrust.AppSurface.Flow.FlowGraphMappingAttribute";
    private const string FlowFaultName = "ForgeTrust.AppSurface.Flow.FlowFault";
    private const string FlowNamespace = "ForgeTrust.AppSurface.Flow";
    private const string GraphBuilderName = "GraphBuilder";
    private const string ConfigureDefaultGraphMethodName = "ConfigureDefaultGraph";
    private const string ConfigureGeneratedGraphParameterName = "configureGeneratedGraph";

    private static readonly DiagnosticDescriptor MissingMapping = new(
        "ASFLOWA001",
        "Generated Flow outcome is not mapped",
        "Outcome '{0}' on node '{1}' is missing generated graph mapping for context '{2}'",
        "AppSurface.Flow.Authoring",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnknownTarget = new(
        "ASFLOWA002",
        "Generated Flow target does not exist",
        "Outcome '{0}' on node '{1}' targets missing context '{2}'",
        "AppSurface.Flow.Authoring",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor IncompatibleTarget = new(
        "ASFLOWA003",
        "Generated Flow target is ambiguous or incompatible",
        "Outcome '{0}' on node '{1}' matches {2} target nodes for context '{3}'",
        "AppSurface.Flow.Authoring",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidStart = new(
        "ASFLOWA004",
        "Generated Flow start node is invalid",
        "Flow '{0}' must declare exactly one [FlowStart] node but declares {1}",
        "AppSurface.Flow.Authoring",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "ASFLOWA005",
        "Generated Flow authoring declaration is invalid",
        "{0}",
        "AppSurface.Flow.Authoring",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor LowLevelMix = new(
        "ASFLOWA006",
        "Generated Flow authoring mixes generated and low-level registration",
        "Flow '{0}' generated authoring should be the definition source for generated nodes; keep low-level FlowGraphBuilder registration separate",
        "AppSurface.Flow.Authoring",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NondeterministicNodeApi = new(
        "ASFLOWA007",
        "Flow node directly reads a nondeterministic value",
        "Flow node '{0}' directly uses nondeterministic API '{1}'; pass the value through persisted context or a durable resume contract",
        "AppSurface.Flow.Determinism",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var flowSpecs = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAuthoringAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (context, _) => CreateFlowSpec(context))
            .Where(static flow => flow is not null)
            .Select(static (flow, _) => flow!);

        var flows = context.CompilationProvider.Combine(flowSpecs.Collect())
            .Select(static (input, _) => new FlowGenerationInput(
                input.Left,
                DistinctFlowSpecs(input.Right)));

        context.RegisterSourceOutput(flows, static (context, input) =>
        {
            foreach (var flow in input.FlowSpecs)
            {
                var hasErrors = ValidateFlow(context, input.Compilation, flow);
                if (hasErrors || flow.Nodes.Count == 0)
                {
                    continue;
                }

                context.AddSource(
                    $"{HintName(flow.Symbol)}.FlowAuthoring.g.cs",
                    SourceText.From(GenerateFlow(flow), Encoding.UTF8));
            }

            foreach (var diagnostic in ReferencedExplicitGraphMappingDiagnostics(input.Compilation, input.FlowSpecs))
            {
                context.ReportDiagnostic(diagnostic);
            }
        });
    }

    private static FlowSpec? CreateFlowSpec(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol ||
            context.TargetNode is not TypeDeclarationSyntax declaration)
        {
            return null;
        }

        var attribute = context.Attributes.FirstOrDefault(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), FlowAuthoringAttributeName, StringComparison.Ordinal));
        return attribute is null ? null : CreateFlowSpec(symbol, declaration, attribute);
    }

    private static ImmutableArray<FlowSpec> DistinctFlowSpecs(ImmutableArray<FlowSpec> flows)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        return flows
            .Where(flow => seen.Add(flow.Symbol))
            .ToImmutableArray();
    }

    private static FlowSpec CreateFlowSpec(
        INamedTypeSymbol symbol,
        TypeDeclarationSyntax declaration,
        AttributeData attribute)
    {
        var flowId = ConstructorValue(attribute, 0) as string ?? string.Empty;
        var versionArgument = attribute.NamedArguments
            .FirstOrDefault(pair => string.Equals(pair.Key, "Version", StringComparison.Ordinal));
        var version = versionArgument.Key is null
            ? "1"
            : versionArgument.Value.Value as string ?? string.Empty;

        var nodes = new List<NodeSpec>();
        foreach (var nested in symbol.GetTypeMembers())
        {
            if (GetAttribute(nested, FlowNodeAttributeName) is not { } nodeAttribute)
            {
                continue;
            }

            var nodeId = ConstructorValue(nodeAttribute, 0) as string ?? string.Empty;
            var inputContext = ConstructorValue(nodeAttribute, 1) as ITypeSymbol;
            var outcomes = nested.GetAttributes()
                .Where(item => string.Equals(item.AttributeClass?.ToDisplayString(), FlowOutcomeAttributeName, StringComparison.Ordinal))
                .Select(CreateOutcomeSpec)
                .ToArray();

            nodes.Add(new NodeSpec(
                nested,
                nodeId,
                inputContext,
                GetAttribute(nested, FlowStartAttributeName) is not null,
                outcomes));
        }

        return new FlowSpec(symbol, declaration, flowId, version, nodes);
    }

    private static OutcomeSpec CreateOutcomeSpec(AttributeData attribute)
    {
        var name = ConstructorValue(attribute, 0) as string ?? string.Empty;
        var kind = ConstructorValue(attribute, 1) is int value
            ? OutcomeKindName(value)
            : null;
        var outputContext = ConstructorValue(attribute, 2) as ITypeSymbol;
        var workType = ConstructorValue(attribute, 3) as ITypeSymbol;
        var resultType = ConstructorValue(attribute, 4) as ITypeSymbol;
        var callsiteId = NamedValue(attribute, "CallsiteId") as string;
        var workContractVersion = NamedValue(attribute, "WorkContractVersion") is int workVersion ? workVersion : 1;
        var resultContractVersion = NamedValue(attribute, "ResultContractVersion") is int resultVersion ? resultVersion : 1;
        return new OutcomeSpec(
            name,
            kind,
            outputContext,
            workType,
            resultType,
            callsiteId,
            workContractVersion,
            resultContractVersion);
    }

    private static bool ValidateFlow(SourceProductionContext context, Compilation compilation, FlowSpec flow)
    {
        var hasErrors = false;

        void ReportError(DiagnosticDescriptor descriptor, Location? location, params object?[] messageArgs)
        {
            hasErrors = true;
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
        }

        if (flow.Declaration is not ClassDeclarationSyntax)
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow authoring type '{flow.Symbol.Name}' must be a partial class. Record flow authoring types are not supported.");
        }

        if (!IsPartial(flow.Declaration))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow authoring type '{flow.Symbol.Name}' must be partial.");
        }

        if (flow.Symbol.TypeParameters.Length > 0)
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow authoring type '{flow.Symbol.Name}' must not be generic.");
        }

        if (flow.Symbol.IsStatic)
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow authoring type '{flow.Symbol.Name}' must not be static.");
        }

        if (flow.Symbol.ContainingType is not null)
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow authoring type '{flow.Symbol.Name}' must be a top-level type. Nested flow authoring types are not supported.");
        }

        if (!HasText(flow.FlowId))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow authoring type '{flow.Symbol.Name}' must declare a non-empty flow id.");
        }

        if (!HasText(flow.Version))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{FlowDiagnosticName(flow)}' must declare a non-empty version.");
        }

        var startCount = flow.Nodes.Count(node => node.IsStart);
        if (startCount != 1)
        {
            ReportError(
                InvalidStart,
                flow.Declaration.Identifier.GetLocation(),
                flow.FlowId,
                startCount);
        }

        var flowFaultSymbol = compilation.GetTypeByMetadataName(FlowFaultName);

        foreach (var method in flow.Symbol.GetMembers("BuildDefinition").OfType<IMethodSymbol>())
        {
            if (method.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                LowLevelMix,
                method.Locations.FirstOrDefault(),
                flow.FlowId));
        }

        foreach (var memberName in new[] { "BuildDefinition", "CreateStartContext", ConfigureDefaultGraphMethodName }
            .Where(memberName => flow.Symbol.GetMembers(memberName)
                .Any(member => member.DeclaringSyntaxReferences.Length > 0)))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' already declares member '{memberName}', which conflicts with generated authoring helpers.");
        }

        foreach (var typeName in GeneratedNestedTypeNames(flow)
            .Where(typeName => flow.Symbol.GetTypeMembers(typeName).Any()))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' already declares nested type '{typeName}', which conflicts with generated authoring output.");
        }

        foreach (var duplicate in flow.Nodes.GroupBy(node => node.NodeId, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' declares node id '{duplicate.Key}' more than once.");
        }

        foreach (var duplicate in ContextSlots(flow).GroupBy(slot => slot.PropertyName, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' contains multiple context types that generate the envelope property '{duplicate.Key}'. Use unique context type names.");
        }

        foreach (var duplicate in GeneratedEnvelopeMemberNames(flow).GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' generates multiple envelope members named '{duplicate.Key}'. Rename the node or context type that creates the collision.");
        }

        foreach (var duplicate in flow.Nodes.Select(node => Identifier(ParameterName(node.Symbol.Name)))
            .Append(ConfigureGeneratedGraphParameterName)
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' generates multiple BuildDefinition parameters named '{duplicate.Key}'. Rename the colliding node types.");
        }

        foreach (var duplicate in GeneratedGraphBuilderMemberNames(flow).GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' generates multiple graph builder members named '{duplicate.Key}'. Rename the colliding node or outcome.");
        }

        foreach (var duplicate in flow.Nodes
            .SelectMany(node => node.Outcomes
                .Where(outcome => string.Equals(outcome.Kind, "Activity", StringComparison.Ordinal))
                .Select(outcome => ActivityCallsiteId(node, outcome)))
            .GroupBy(callsiteId => callsiteId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            ReportError(
                InvalidDeclaration,
                flow.Declaration.Identifier.GetLocation(),
                $"Flow '{flow.FlowId}' declares activity callsite id '{duplicate.Key}' more than once. Activity callsite ids must be unique within a Flow definition.");
        }

        foreach (var diagnostic in ExplicitGraphMappingDiagnostics(compilation, flow))
        {
            hasErrors = true;
            context.ReportDiagnostic(diagnostic);
        }

        foreach (var node in flow.Nodes)
        {
            foreach (var diagnostic in NondeterministicApiDiagnostics(compilation, node))
            {
                context.ReportDiagnostic(diagnostic);
            }

            if (node.Symbol.TypeParameters.Length > 0)
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' must not be generic.");
            }

            if (!HasText(node.NodeId))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' must declare a non-empty node id.");
            }

            if (node.InputContext is null)
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' must declare a concrete input context type.");
            }
            else if (!IsConcreteContextType(node.InputContext))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' input context '{node.InputContext.ToDisplayString()}' must be a concrete non-void context type.");
            }
            else if (!ImplementsTransformerNode(node))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' must implement IFlowTransformerNode<{node.InputContext.ToDisplayString()}, {OutcomeTypeName(node)}>.");
            }

            if (!IsExposableFromFlow(node.Symbol, flow.Symbol))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' must be at least as accessible as generated helpers for flow '{flow.FlowId}'.");
            }

            if (node.InputContext is not null && !IsExposableFromFlow(node.InputContext, flow.Symbol))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' input context '{node.InputContext.ToDisplayString()}' must be at least as accessible as generated helpers for flow '{flow.FlowId}'.");
            }

            foreach (var outcome in node.Outcomes.Where(outcome =>
                outcome.OutputContext is not null && !IsExposableFromFlow(outcome.OutputContext, flow.Symbol)))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Outcome '{outcome.Name}' on node '{node.NodeId}' carries context '{outcome.OutputContext!.ToDisplayString()}', which is less accessible than generated helpers for flow '{flow.FlowId}'.");
            }

            foreach (var outcome in node.Outcomes.Where(outcome =>
                outcome.WorkType is not null && !IsExposableFromFlow(outcome.WorkType, flow.Symbol)))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Activity outcome '{outcome.Name}' on node '{node.NodeId}' carries work contract '{outcome.WorkType!.ToDisplayString()}', which is less accessible than generated helpers for flow '{flow.FlowId}'.");
            }

            foreach (var outcome in node.Outcomes.Where(outcome =>
                outcome.ResultType is not null && !IsExposableFromFlow(outcome.ResultType, flow.Symbol)))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Activity outcome '{outcome.Name}' on node '{node.NodeId}' carries result contract '{outcome.ResultType!.ToDisplayString()}', which is less accessible than generated helpers for flow '{flow.FlowId}'.");
            }

            if (node.Outcomes.Count == 0)
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' must declare at least one [FlowOutcome].");
            }

            foreach (var duplicate in GeneratedOutcomeUnionMemberNames(node)
                         .GroupBy(name => name, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Flow node '{node.Symbol.Name}' generates multiple outcome-union members named '{duplicate.Key}'. Use outcome names that remain unique after generated suffixes are applied.");
            }

            foreach (var outcome in node.Outcomes)
            {
                if (!HasText(outcome.Name))
                {
                    ReportError(
                        InvalidDeclaration,
                        node.Symbol.Locations.FirstOrDefault(),
                        $"Flow node '{node.Symbol.Name}' must declare non-empty outcome names.");
                }

                if (outcome.Kind is null)
                {
                    ReportError(
                        InvalidDeclaration,
                        node.Symbol.Locations.FirstOrDefault(),
                        $"Outcome '{OutcomeDiagnosticName(outcome)}' on node '{node.NodeId}' must declare a valid FlowOutcomeKind value.");
                }

                if (outcome.OutputContext is null)
                {
                    ReportError(
                        InvalidDeclaration,
                        node.Symbol.Locations.FirstOrDefault(),
                        $"Outcome '{OutcomeDiagnosticName(outcome)}' on node '{node.NodeId}' must declare a concrete output context type.");
                }
                else if (!IsConcreteContextType(outcome.OutputContext))
                {
                    ReportError(
                        InvalidDeclaration,
                        node.Symbol.Locations.FirstOrDefault(),
                        $"Outcome '{OutcomeDiagnosticName(outcome)}' on node '{node.NodeId}' carries context '{outcome.OutputContext.ToDisplayString()}', which must be a concrete non-void context type.");
                }

                if (string.Equals(outcome.Kind, "Activity", StringComparison.Ordinal))
                {
                    if (outcome.WorkType is null || !IsConcreteContextType(outcome.WorkType))
                    {
                        ReportError(
                            InvalidDeclaration,
                            node.Symbol.Locations.FirstOrDefault(),
                            $"Activity outcome '{OutcomeDiagnosticName(outcome)}' on node '{node.NodeId}' must declare a concrete non-void work contract type with the five-argument FlowOutcome constructor.");
                    }

                    if (outcome.ResultType is null || !IsConcreteContextType(outcome.ResultType))
                    {
                        ReportError(
                            InvalidDeclaration,
                            node.Symbol.Locations.FirstOrDefault(),
                            $"Activity outcome '{OutcomeDiagnosticName(outcome)}' on node '{node.NodeId}' must declare a concrete non-void result contract type with the five-argument FlowOutcome constructor.");
                    }

                    if (node.InputContext is not null &&
                        !SymbolEqualityComparer.Default.Equals(node.InputContext, outcome.OutputContext))
                    {
                        ReportError(
                            InvalidDeclaration,
                            node.Symbol.Locations.FirstOrDefault(),
                            $"Activity outcome '{outcome.Name}' on node '{node.NodeId}' must carry the node input context '{node.InputContext.ToDisplayString()}' because the activity result resumes the same node.");
                    }

                    if (outcome.CallsiteId is not null && !HasText(outcome.CallsiteId))
                    {
                        ReportError(
                            InvalidDeclaration,
                            node.Symbol.Locations.FirstOrDefault(),
                            $"Activity outcome '{outcome.Name}' on node '{node.NodeId}' must declare a non-empty CallsiteId when overriding the generated id.");
                    }

                    if (outcome.WorkContractVersion < 1 || outcome.ResultContractVersion < 1)
                    {
                        ReportError(
                            InvalidDeclaration,
                            node.Symbol.Locations.FirstOrDefault(),
                            $"Activity outcome '{outcome.Name}' on node '{node.NodeId}' must use work and result contract versions of at least 1.");
                    }
                }
                else if (outcome.WorkType is not null ||
                    outcome.ResultType is not null ||
                    outcome.CallsiteId is not null ||
                    outcome.WorkContractVersion != 1 ||
                    outcome.ResultContractVersion != 1)
                {
                    ReportError(
                        InvalidDeclaration,
                        node.Symbol.Locations.FirstOrDefault(),
                        $"Outcome '{OutcomeDiagnosticName(outcome)}' on node '{node.NodeId}' declares activity metadata but its kind is '{outcome.Kind ?? "unknown"}'. Use FlowOutcomeKind.Activity with the five-argument constructor, or remove the activity metadata.");
                }
            }

            foreach (var outcome in node.Outcomes.Where(outcome => string.Equals(outcome.Kind, "Next", StringComparison.Ordinal)))
            {
                var matches = flow.Nodes
                    .Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.InputContext, outcome.OutputContext))
                    .ToArray();
                if (matches.Length == 0)
                {
                    ReportError(
                        UnknownTarget,
                        node.Symbol.Locations.FirstOrDefault(),
                        outcome.Name,
                        node.NodeId,
                        outcome.OutputContext?.ToDisplayString() ?? "<unknown>");
                    ReportError(
                        MissingMapping,
                        node.Symbol.Locations.FirstOrDefault(),
                        outcome.Name,
                        node.NodeId,
                        outcome.OutputContext?.ToDisplayString() ?? "<unknown>");
                }
                else if (matches.Length > 1)
                {
                    ReportError(
                        IncompatibleTarget,
                        node.Symbol.Locations.FirstOrDefault(),
                        outcome.Name,
                        node.NodeId,
                        matches.Length,
                        outcome.OutputContext?.ToDisplayString() ?? "<unknown>");
                }
            }

            foreach (var outcome in node.Outcomes.Where(outcome =>
                (string.Equals(outcome.Kind, "Wait", StringComparison.Ordinal) ||
                string.Equals(outcome.Kind, "TimedOut", StringComparison.Ordinal)) &&
                node.InputContext is not null &&
                !SymbolEqualityComparer.Default.Equals(node.InputContext, outcome.OutputContext)))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Outcome '{outcome.Name}' on node '{node.NodeId}' must carry the node input context '{node.InputContext!.ToDisplayString()}' because {outcome.Kind} resumes the same node.");
            }

            foreach (var outcome in node.Outcomes.Where(outcome =>
                string.Equals(outcome.Kind, "Fault", StringComparison.Ordinal) &&
                (flowFaultSymbol is null ||
                    !SymbolEqualityComparer.Default.Equals(outcome.OutputContext, flowFaultSymbol))))
            {
                ReportError(
                    InvalidDeclaration,
                    node.Symbol.Locations.FirstOrDefault(),
                    $"Outcome '{outcome.Name}' on node '{node.NodeId}' must carry FlowFault because the low-level runtime fault contract stores code and message.");
            }
        }

        return hasErrors;
    }

    private static IEnumerable<Diagnostic> NondeterministicApiDiagnostics(
        Compilation compilation,
        NodeSpec node)
    {
        foreach (var syntaxReference in node.Symbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not TypeDeclarationSyntax declaration)
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var memberAccess in declaration.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                         .Where(static memberAccess =>
                             IsNondeterministicMemberCandidate(memberAccess.Name.Identifier.ValueText)))
            {
                if (IsWithinNameOf(memberAccess))
                {
                    continue;
                }

                if (TryDescribeNondeterministicMember(
                        semanticModel.GetSymbolInfo(memberAccess).Symbol,
                        out var api))
                {
                    yield return Diagnostic.Create(
                        NondeterministicNodeApi,
                        memberAccess.GetLocation(),
                        node.NodeId,
                        api);
                }
            }

            foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>()
                         .Where(static identifier =>
                             identifier.Parent is not MemberAccessExpressionSyntax &&
                             IsNondeterministicMemberCandidate(identifier.Identifier.ValueText)))
            {
                if (IsWithinNameOf(identifier))
                {
                    continue;
                }

                if (TryDescribeNondeterministicMember(
                        semanticModel.GetSymbolInfo(identifier).Symbol,
                        out var api))
                {
                    yield return Diagnostic.Create(
                        NondeterministicNodeApi,
                        identifier.GetLocation(),
                        node.NodeId,
                        api);
                }
            }

            foreach (var objectCreation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (IsSystemRandom(semanticModel.GetTypeInfo(objectCreation).Type))
                {
                    yield return Diagnostic.Create(
                        NondeterministicNodeApi,
                        objectCreation.GetLocation(),
                        node.NodeId,
                        "System.Random constructor");
                }
            }

            foreach (var objectCreation in declaration.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                if (IsSystemRandom(semanticModel.GetTypeInfo(objectCreation).Type))
                {
                    yield return Diagnostic.Create(
                        NondeterministicNodeApi,
                        objectCreation.GetLocation(),
                        node.NodeId,
                        "System.Random constructor");
                }
            }
        }
    }

    private static bool TryDescribeNondeterministicMember(ISymbol? symbol, out string api)
    {
        if (symbol is IPropertySymbol property)
        {
            var containingType = property.ContainingType.ToDisplayString();
            if ((string.Equals(containingType, "System.DateTime", StringComparison.Ordinal) ||
                    string.Equals(containingType, "System.DateTimeOffset", StringComparison.Ordinal)) &&
                (string.Equals(property.Name, "Now", StringComparison.Ordinal) ||
                    string.Equals(property.Name, "UtcNow", StringComparison.Ordinal)))
            {
                api = containingType + "." + property.Name;
                return true;
            }

            if (string.Equals(containingType, "System.DateTime", StringComparison.Ordinal) &&
                string.Equals(property.Name, "Today", StringComparison.Ordinal))
            {
                api = "System.DateTime.Today";
                return true;
            }

            if (string.Equals(containingType, "System.Random", StringComparison.Ordinal) &&
                string.Equals(property.Name, "Shared", StringComparison.Ordinal))
            {
                api = "System.Random.Shared";
                return true;
            }

            if (string.Equals(containingType, "System.TimeProvider", StringComparison.Ordinal) &&
                string.Equals(property.Name, "System", StringComparison.Ordinal))
            {
                api = "System.TimeProvider.System";
                return true;
            }
        }

        if (symbol is IMethodSymbol method)
        {
            var containingType = method.ContainingType.ToDisplayString();
            if (string.Equals(containingType, "System.Guid", StringComparison.Ordinal) &&
                string.Equals(method.Name, "NewGuid", StringComparison.Ordinal))
            {
                api = "System.Guid.NewGuid";
                return true;
            }

            if (string.Equals(containingType, "System.Diagnostics.Stopwatch", StringComparison.Ordinal) &&
                string.Equals(method.Name, "GetTimestamp", StringComparison.Ordinal))
            {
                api = "System.Diagnostics.Stopwatch.GetTimestamp";
                return true;
            }
        }

        api = string.Empty;
        return false;
    }

    private static bool IsNondeterministicMemberCandidate(string name) =>
        name is "Now" or "UtcNow" or "Today" or "Shared" or "System" or "NewGuid" or "GetTimestamp";

    private static bool IsWithinNameOf(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any(invocation =>
            invocation.Expression is IdentifierNameSyntax identifier &&
            string.Equals(identifier.Identifier.Text, "nameof", StringComparison.Ordinal));

    private static bool IsSystemRandom(ITypeSymbol? symbol) =>
        symbol is not null && string.Equals(symbol.ToDisplayString(), "System.Random", StringComparison.Ordinal);

    private static IEnumerable<Diagnostic> ExplicitGraphMappingDiagnostics(Compilation compilation, FlowSpec flow)
    {
        var expectedMappings = ExpectedGraphMappingInfos(flow).ToArray();
        if (expectedMappings.Length == 0)
        {
            yield break;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsExplicitBuildDefinitionInvocation(semanticModel, invocation, flow.Symbol))
                {
                    continue;
                }

                var configurationArgument = ExplicitGraphConfigurationArgument(invocation);
                var configurationExpression = configurationArgument?.Expression;
                AnonymousFunctionExpressionSyntax? lambda = configurationExpression switch
                {
                    ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized,
                    SimpleLambdaExpressionSyntax simple => simple,
                    _ => null,
                };

                if (lambda is null)
                {
                    if (configurationArgument is not null &&
                        IsExplicitConfigurationArgument(semanticModel, configurationArgument, flow))
                    {
                        yield return Diagnostic.Create(
                            InvalidDeclaration,
                            configurationExpression?.GetLocation() ?? invocation.GetLocation(),
                            $"Flow '{flow.FlowId}' explicit generated graph configuration must be an inline lambda so generated mapping coverage can be validated.");
                    }

                    continue;
                }

                var graphParameterName = GraphParameterName(lambda);
                if (graphParameterName is null)
                {
                    yield return Diagnostic.Create(
                        InvalidDeclaration,
                        lambda.GetLocation(),
                        $"Flow '{flow.FlowId}' explicit generated graph configuration must declare exactly one graph builder parameter.");
                    continue;
                }

                foreach (var unsupportedMapping in UnsupportedMappingInvocationNames(lambda.Body, graphParameterName, expectedMappings))
                {
                    yield return Diagnostic.Create(
                        InvalidDeclaration,
                        unsupportedMapping.Location,
                        $"Flow '{flow.FlowId}' explicitly configures generated graph mapping '{unsupportedMapping.Name}' outside a direct graph mapping expression.");
                }

                var invokedMappingNames = MappingInvocationNames(lambda.Body, graphParameterName)
                    .Where(name => expectedMappings.Any(mapping => string.Equals(mapping.MethodName, name, StringComparison.Ordinal)))
                    .ToArray();
                var mappedNames = new HashSet<string>(invokedMappingNames, StringComparer.Ordinal);

                foreach (var mapping in expectedMappings.Where(mapping => !mappedNames.Contains(mapping.MethodName)))
                {
                    yield return Diagnostic.Create(
                        MissingMapping,
                        lambda.GetLocation(),
                        mapping.OutcomeName,
                        mapping.NodeId,
                        mapping.OutputContextName);
                }

                foreach (var duplicate in invokedMappingNames.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                {
                    yield return Diagnostic.Create(
                        InvalidDeclaration,
                        lambda.GetLocation(),
                        $"Flow '{flow.FlowId}' explicitly configures generated graph mapping '{duplicate.Key}' more than once.");
                }
            }
        }
    }

    private static IEnumerable<Diagnostic> ReferencedExplicitGraphMappingDiagnostics(
        Compilation compilation,
        ImmutableArray<FlowSpec> localFlows)
    {
        var localFlowSymbols = new HashSet<INamedTypeSymbol>(localFlows.Select(flow => flow.Symbol), SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (ReferencedBuildDefinitionMethod(semanticModel, invocation, localFlowSymbols) is not { } method ||
                    GraphBuilderType(method.Parameters[0].Type) is not { } graphBuilderType)
                {
                    continue;
                }

                var expectedMappings = ExpectedGraphMappingInfos(graphBuilderType).ToArray();
                if (expectedMappings.Length == 0)
                {
                    continue;
                }

                foreach (var diagnostic in ExplicitGraphMappingDiagnosticsForInvocation(
                    semanticModel,
                    invocation,
                    method.ContainingType.Name,
                    expectedMappings))
                {
                    yield return diagnostic;
                }
            }
        }
    }

    private static IMethodSymbol? ReferencedBuildDefinitionMethod(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        HashSet<INamedTypeSymbol> localFlowSymbols)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var candidates = symbolInfo.Symbol is IMethodSymbol symbol
            ? new[] { symbol }
            : symbolInfo.CandidateSymbols.OfType<IMethodSymbol>();

        return candidates.FirstOrDefault(method =>
            string.Equals(method.Name, "BuildDefinition", StringComparison.Ordinal) &&
            method.Parameters.Length > 0 &&
            string.Equals(method.Parameters[0].Name, ConfigureGeneratedGraphParameterName, StringComparison.Ordinal) &&
            method.ContainingType is not null &&
            !localFlowSymbols.Contains(method.ContainingType));
    }

    private static IEnumerable<Diagnostic> ExplicitGraphMappingDiagnosticsForInvocation(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        string flowName,
        IReadOnlyList<ExpectedGraphMappingInfo> expectedMappings)
    {
        var configurationArgument = ExplicitGraphConfigurationArgument(invocation);
        var configurationExpression = configurationArgument?.Expression;
        AnonymousFunctionExpressionSyntax? lambda = configurationExpression switch
        {
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized,
            SimpleLambdaExpressionSyntax simple => simple,
            _ => null,
        };

        if (lambda is null)
        {
            if (configurationArgument is not null &&
                IsExplicitConfigurationArgument(semanticModel, configurationArgument, expectedMappings))
            {
                yield return Diagnostic.Create(
                    InvalidDeclaration,
                    configurationExpression?.GetLocation() ?? invocation.GetLocation(),
                    $"Flow '{flowName}' explicit generated graph configuration must be an inline lambda so generated mapping coverage can be validated.");
            }

            yield break;
        }

        var graphParameterName = GraphParameterName(lambda);
        if (graphParameterName is null)
        {
            yield return Diagnostic.Create(
                InvalidDeclaration,
                lambda.GetLocation(),
                $"Flow '{flowName}' explicit generated graph configuration must declare exactly one graph builder parameter.");
            yield break;
        }

        foreach (var unsupportedMapping in UnsupportedMappingInvocationNames(lambda.Body, graphParameterName, expectedMappings))
        {
            yield return Diagnostic.Create(
                InvalidDeclaration,
                unsupportedMapping.Location,
                $"Flow '{flowName}' explicitly configures generated graph mapping '{unsupportedMapping.Name}' outside a direct graph mapping expression.");
        }

        var invokedMappingNames = MappingInvocationNames(lambda.Body, graphParameterName)
            .Where(name => expectedMappings.Any(mapping => string.Equals(mapping.MethodName, name, StringComparison.Ordinal)))
            .ToArray();
        var mappedNames = new HashSet<string>(invokedMappingNames, StringComparer.Ordinal);

        foreach (var mapping in expectedMappings.Where(mapping => !mappedNames.Contains(mapping.MethodName)))
        {
            yield return Diagnostic.Create(
                MissingMapping,
                lambda.GetLocation(),
                mapping.OutcomeName,
                mapping.NodeId,
                mapping.OutputContextName);
        }

        foreach (var duplicate in invokedMappingNames.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            yield return Diagnostic.Create(
                InvalidDeclaration,
                lambda.GetLocation(),
                $"Flow '{flowName}' explicitly configures generated graph mapping '{duplicate.Key}' more than once.");
        }
    }

    private static ArgumentSyntax? ExplicitGraphConfigurationArgument(InvocationExpressionSyntax invocation)
    {
        var namedConfiguration = invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
            string.Equals(argument.NameColon?.Name.Identifier.ValueText, ConfigureGeneratedGraphParameterName, StringComparison.Ordinal));
        if (namedConfiguration is not null)
        {
            return namedConfiguration;
        }

        var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault();
        return firstArgument?.NameColon is null ? firstArgument : null;
    }

    private static bool IsExplicitConfigurationArgument(SemanticModel semanticModel, ArgumentSyntax argument, FlowSpec flow)
    {
        if (string.Equals(argument.NameColon?.Name.Identifier.ValueText, ConfigureGeneratedGraphParameterName, StringComparison.Ordinal))
        {
            return true;
        }

        return !IsNodeArgument(semanticModel, argument.Expression, flow);
    }

    private static bool IsExplicitConfigurationArgument(
        SemanticModel semanticModel,
        ArgumentSyntax argument,
        IReadOnlyList<ExpectedGraphMappingInfo> expectedMappings)
    {
        if (string.Equals(argument.NameColon?.Name.Identifier.ValueText, ConfigureGeneratedGraphParameterName, StringComparison.Ordinal))
        {
            return true;
        }

        var argumentType = semanticModel.GetTypeInfo(argument.Expression).Type;
        return argumentType is null ||
            expectedMappings.All(mapping => !SymbolEqualityComparer.Default.Equals(argumentType, mapping.NodeType));
    }

    private static bool IsNodeArgument(SemanticModel semanticModel, ExpressionSyntax expression, FlowSpec flow)
    {
        var argumentType = semanticModel.GetTypeInfo(expression).Type;
        return argumentType is not null &&
            flow.Nodes.Any(node => SymbolEqualityComparer.Default.Equals(argumentType, node.Symbol));
    }

    private static string? GraphParameterName(AnonymousFunctionExpressionSyntax lambda)
    {
        return lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized =>
                parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
            _ => null,
        };
    }

    private static IEnumerable<string> MappingInvocationNames(CSharpSyntaxNode body, string graphParameterName)
    {
        if (body is ExpressionSyntax expression)
        {
            foreach (var name in MappingInvocationNamesFromExpression(expression, graphParameterName))
            {
                yield return name;
            }

            yield break;
        }

        if (body is not BlockSyntax block)
        {
            yield break;
        }

        foreach (var statement in block.Statements.OfType<ExpressionStatementSyntax>())
        {
            foreach (var name in MappingInvocationNamesFromExpression(statement.Expression, graphParameterName))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> MappingInvocationNamesFromExpression(ExpressionSyntax expression, string graphParameterName)
    {
        foreach (var invocation in GraphBuilderChainInvocations(expression, graphParameterName))
        {
            yield return invocation.Name;
        }
    }

    private static IEnumerable<GraphMappingInvocation> UnsupportedMappingInvocationNames(
        CSharpSyntaxNode body,
        string graphParameterName,
        IReadOnlyList<ExpectedGraphMappingInfo> expectedMappings)
    {
        var allowed = DirectMappingInvocationLocations(body, graphParameterName).ToImmutableHashSet();
        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (allowed.Contains(invocation.SpanStart))
            {
                continue;
            }

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                expectedMappings.Any(mapping => string.Equals(mapping.MethodName, memberAccess.Name.Identifier.ValueText, StringComparison.Ordinal)) &&
                IsGraphBuilderChain(memberAccess.Expression, graphParameterName))
            {
                yield return new GraphMappingInvocation(memberAccess.Name.Identifier.ValueText, invocation.GetLocation());
            }
        }
    }

    private static IEnumerable<int> DirectMappingInvocationLocations(CSharpSyntaxNode body, string graphParameterName)
    {
        if (body is ExpressionSyntax expression)
        {
            foreach (var invocation in GraphBuilderChainInvocations(expression, graphParameterName))
            {
                yield return invocation.SpanStart;
            }

            yield break;
        }

        if (body is not BlockSyntax block)
        {
            yield break;
        }

        foreach (var statement in block.Statements.OfType<ExpressionStatementSyntax>())
        {
            foreach (var invocation in GraphBuilderChainInvocations(statement.Expression, graphParameterName))
            {
                yield return invocation.SpanStart;
            }
        }
    }

    private static IEnumerable<GraphMappingInvocation> GraphBuilderChainInvocations(ExpressionSyntax expression, string graphParameterName)
    {
        var invocations = new List<GraphMappingInvocation>();
        return TryCollectGraphBuilderChainInvocations(expression, graphParameterName, invocations)
            ? invocations
            : Enumerable.Empty<GraphMappingInvocation>();
    }

    private static bool TryCollectGraphBuilderChainInvocations(
        ExpressionSyntax expression,
        string graphParameterName,
        ICollection<GraphMappingInvocation> invocations)
    {
        switch (expression)
        {
            case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                if (!TryCollectGraphBuilderChainInvocations(memberAccess.Expression, graphParameterName, invocations))
                {
                    return false;
                }

                invocations.Add(new GraphMappingInvocation(memberAccess.Name.Identifier.ValueText, invocation.GetLocation(), invocation.SpanStart));
                return true;
            case ParenthesizedExpressionSyntax parenthesized:
                return TryCollectGraphBuilderChainInvocations(parenthesized.Expression, graphParameterName, invocations);
            case IdentifierNameSyntax identifier:
                return string.Equals(identifier.Identifier.ValueText, graphParameterName, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private static bool IsGraphBuilderChain(SyntaxNode expression, string graphParameterName)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => string.Equals(identifier.Identifier.ValueText, graphParameterName, StringComparison.Ordinal),
            InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax memberAccess =>
                IsGraphBuilderChain(memberAccess.Expression, graphParameterName),
            ParenthesizedExpressionSyntax parenthesized => IsGraphBuilderChain(parenthesized.Expression, graphParameterName),
            _ => false,
        };
    }

    private static bool IsExplicitBuildDefinitionInvocation(SemanticModel semanticModel, InvocationExpressionSyntax invocation, INamedTypeSymbol flow)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return string.Equals(memberAccess.Name.Identifier.ValueText, "BuildDefinition", StringComparison.Ordinal) &&
                IsFlowTypeExpression(semanticModel, memberAccess.Expression, flow);
        }

        if (invocation.Expression is IdentifierNameSyntax identifier &&
            string.Equals(identifier.Identifier.ValueText, "BuildDefinition", StringComparison.Ordinal))
        {
            if (IsInsideFlowType(semanticModel, invocation, flow))
            {
                return true;
            }

            return HasStaticUsingForFlow(semanticModel, invocation, flow) &&
                !BindsToDifferentMethod(semanticModel, invocation, flow);
        }

        return false;
    }

    private static bool HasStaticUsingForFlow(SemanticModel semanticModel, SyntaxNode node, INamedTypeSymbol flow)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            SyntaxList<UsingDirectiveSyntax> usings = current switch
            {
                CompilationUnitSyntax compilationUnit => compilationUnit.Usings,
                BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Usings,
                _ => default,
            };

            foreach (var usingDirective in usings)
            {
                if (!usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ||
                    usingDirective.Name is null)
                {
                    continue;
                }

                if (semanticModel.GetSymbolInfo(usingDirective.Name).Symbol is INamedTypeSymbol staticType &&
                    SymbolEqualityComparer.Default.Equals(staticType, flow))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool BindsToDifferentMethod(SemanticModel semanticModel, InvocationExpressionSyntax invocation, INamedTypeSymbol flow)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        return symbolInfo.Symbol is IMethodSymbol method &&
            !SymbolEqualityComparer.Default.Equals(method.ContainingType, flow);
    }

    private static bool IsInsideFlowType(SemanticModel semanticModel, SyntaxNode node, INamedTypeSymbol flow)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is TypeDeclarationSyntax declaration &&
                semanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol enclosingType)
            {
                for (var type = enclosingType; type is not null; type = type.ContainingType)
                {
                    if (SymbolEqualityComparer.Default.Equals(type, flow))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    private static bool IsFlowTypeExpression(SemanticModel semanticModel, SyntaxNode expression, INamedTypeSymbol flow)
    {
        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        return symbol is INamedTypeSymbol named &&
            SymbolEqualityComparer.Default.Equals(named, flow);
    }

    private static string GenerateFlow(FlowSpec flow)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        source.AppendLine();

        var namespaceName = flow.Symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : NamespaceName(flow.Symbol.ContainingNamespace);
        if (namespaceName is not null)
        {
            source.Append("namespace ").Append(namespaceName).AppendLine(";");
            source.AppendLine();
        }

        source.Append("partial class ").Append(Identifier(flow.Symbol.Name)).AppendLine();
        source.AppendLine("{");
        GenerateEnvelope(source, flow);
        foreach (var node in flow.Nodes)
        {
            GenerateOutcomeUnion(source, node);
            GenerateAdapter(source, flow, node);
        }

        GenerateGraphBuilder(source, flow);
        GenerateBuildDefinition(source, flow);
        source.AppendLine("}");
        return source.ToString();
    }

    private static void GenerateEnvelope(StringBuilder source, FlowSpec flow)
    {
        var envelopeName = EnvelopeName(flow);
        source.Append("    ").Append(AccessibilityKeyword(flow.Symbol.DeclaredAccessibility)).Append(" sealed record ").Append(envelopeName).AppendLine();
        source.AppendLine("    {");
        source.Append("        public ").Append(envelopeName).AppendLine("()");
        source.AppendLine("        {");
        source.AppendLine("            NodeId = string.Empty;");
        source.AppendLine("            StateKind = string.Empty;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        private ").Append(envelopeName).AppendLine("(string nodeId, string stateKind)");
        source.AppendLine("        {");
        source.AppendLine("            if (global::System.String.IsNullOrWhiteSpace(nodeId))");
        source.AppendLine("            {");
        source.AppendLine("                throw new global::System.ArgumentException(\"Value must not be empty.\", nameof(nodeId));");
        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine("            if (global::System.String.IsNullOrWhiteSpace(stateKind))");
        source.AppendLine("            {");
        source.AppendLine("                throw new global::System.ArgumentException(\"Value must not be empty.\", nameof(stateKind));");
        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine("            NodeId = nodeId;");
        source.AppendLine("            StateKind = stateKind;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        public string NodeId { get; init; }");
        source.AppendLine("        public string StateKind { get; init; }");
        foreach (var slot in ContextSlots(flow))
        {
            source.Append("        public ").Append(TypeName(slot.Context)).Append("? ").Append(slot.PropertyName).AppendLine(" { get; init; }");
        }

        source.AppendLine();
        foreach (var node in flow.Nodes)
        {
            source.Append("        public static ").Append(envelopeName).Append(" For").Append(node.Symbol.Name).Append("(")
                .Append(TypeName(node.InputContext!)).Append(" state) => new(\"").Append(Escape(node.NodeId)).Append("\", \"")
                .Append(node.Symbol.Name).Append("\") { ").Append(SlotPropertyName(node.InputContext!)).Append(" = state };").AppendLine();
            source.Append("        public ").Append(TypeName(node.InputContext!)).Append(" As").Append(node.Symbol.Name).AppendLine("() =>");
            source.Append("            ").Append(SlotPropertyName(node.InputContext!)).Append(" ?? throw new global::ForgeTrust.AppSurface.Flow.FlowDefinitionException(\"Generated flow context does not carry state for node '")
                .Append(Escape(node.NodeId)).AppendLine("'.\");");
        }

        foreach (var slot in ContextSlots(flow))
        {
            source.Append("        public static ").Append(envelopeName).Append(" For").Append(slot.PropertyName).Append("(")
                .Append(TypeName(slot.Context)).Append(" state, string nodeId) => new(nodeId, \"").Append(slot.PropertyName)
                .Append("\") { ").Append(slot.PropertyName).AppendLine(" = state };");
        }

        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void GenerateOutcomeUnion(StringBuilder source, NodeSpec node)
    {
        var outcomeType = OutcomeTypeName(node);
        source.Append("    public abstract partial record ").Append(outcomeType).AppendLine();
        source.AppendLine("    {");
        source.Append("        private ").Append(outcomeType).AppendLine("() { }");
        source.AppendLine("        private static TContext RequireContext<TContext>(TContext context)");
        source.AppendLine("        {");
        source.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(context);");
        source.AppendLine("            return context;");
        source.AppendLine("        }");
        source.AppendLine();
        foreach (var outcome in node.Outcomes)
        {
            var caseName = CaseName(outcome);
            if (string.Equals(outcome.Kind, "Activity", StringComparison.Ordinal))
            {
                source.Append("        public static global::ForgeTrust.AppSurface.Flow.FlowActivityCallsite<")
                    .Append(TypeName(outcome.WorkType!)).Append(", ").Append(TypeName(outcome.ResultType!)).Append("> ")
                    .Append(caseName).Append("Callsite { get; } = new(\"").Append(Escape(ActivityCallsiteId(node, outcome)))
                    .Append("\", ").Append(outcome.WorkContractVersion.ToString(global::System.Globalization.CultureInfo.InvariantCulture))
                    .Append(", ").Append(outcome.ResultContractVersion.ToString(global::System.Globalization.CultureInfo.InvariantCulture))
                    .AppendLine(");");
                source.Append("        public sealed record ").Append(caseName).Append("Outcome(")
                    .Append(TypeName(outcome.WorkType!)).Append(" Work, ").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context) : ").Append(outcomeType).AppendLine();
                source.AppendLine("        {");
                source.Append("            public ").Append(TypeName(outcome.WorkType!))
                    .Append(" Work { get; } = RequireContext(Work);").AppendLine();
                source.Append("            public ").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context { get; } = RequireContext(Context);").AppendLine();
                source.AppendLine("        }");
                source.Append("        public static ").Append(caseName).Append("Outcome ").Append(caseName).Append("(")
                    .Append(TypeName(outcome.WorkType!)).Append(" work, ").Append(TypeName(outcome.OutputContext!))
                    .Append(" context) => new(RequireContext(work), RequireContext(context));").AppendLine();
            }
            else if (string.Equals(outcome.Kind, "Fault", StringComparison.Ordinal))
            {
                source.Append("        public sealed record ").Append(caseName).Append("Outcome(").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context) : ").Append(outcomeType).AppendLine();
                source.AppendLine("        {");
                source.Append("            public ").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context { get; } = RequireContext(Context);")
                    .AppendLine();
                source.AppendLine("        }");
                source.Append("        public static ").Append(caseName).Append("Outcome ").Append(caseName)
                    .Append("(").Append(TypeName(outcome.OutputContext!)).Append(" context) => new(RequireContext(context));")
                    .AppendLine();
            }
            else if (string.Equals(outcome.Kind, "Wait", StringComparison.Ordinal))
            {
                source.Append("        public sealed record ").Append(caseName).Append("Outcome(").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context, global::ForgeTrust.AppSurface.Flow.FlowTimeout? Timeout = null) : ").Append(outcomeType).AppendLine();
                source.AppendLine("        {");
                source.Append("            public ").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context { get; } = RequireContext(Context);")
                    .AppendLine();
                source.AppendLine("        }");
                source.Append("        public static ").Append(caseName).Append("Outcome ").Append(caseName).Append("(")
                    .Append(TypeName(outcome.OutputContext!))
                    .Append(" context, global::ForgeTrust.AppSurface.Flow.FlowTimeout? timeout = null) => new(RequireContext(context), timeout);")
                    .AppendLine();
            }
            else
            {
                source.Append("        public sealed record ").Append(caseName).Append("Outcome(").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context) : ").Append(outcomeType).AppendLine();
                source.AppendLine("        {");
                source.Append("            public ").Append(TypeName(outcome.OutputContext!))
                    .Append(" Context { get; } = RequireContext(Context);")
                    .AppendLine();
                source.AppendLine("        }");
                source.Append("        public static ").Append(caseName).Append("Outcome ").Append(caseName).Append("(")
                    .Append(TypeName(outcome.OutputContext!)).Append(" context) => new(RequireContext(context));")
                    .AppendLine();
            }

            source.AppendLine();
        }

        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void GenerateAdapter(StringBuilder source, FlowSpec flow, NodeSpec node)
    {
        var envelopeName = EnvelopeName(flow);
        var adapterName = AdapterName(node);
        var outcomeType = OutcomeTypeName(node);
        source.Append("    private sealed class ").Append(adapterName).Append(" : global::ForgeTrust.AppSurface.Flow.IFlowNode<").Append(envelopeName).AppendLine(">");
        source.AppendLine("    {");
        source.Append("        private readonly ").Append(TypeName(node.Symbol)).AppendLine(" _node;");
        source.Append("        internal ").Append(adapterName).Append("(").Append(TypeName(node.Symbol)).AppendLine(" node)");
        source.AppendLine("        {");
        source.AppendLine("            _node = node ?? throw new global::System.ArgumentNullException(nameof(node));");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        public async global::System.Threading.Tasks.ValueTask<global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
            .Append(envelopeName).AppendLine(">> ExecuteAsync(");
        source.Append("            global::ForgeTrust.AppSurface.Flow.FlowExecutionContext<").Append(envelopeName).AppendLine("> context,");
        source.AppendLine("            global::System.Threading.CancellationToken cancellationToken = default)");
        source.AppendLine("        {");
        source.Append("            var typedState = context.State.As").Append(node.Symbol.Name).AppendLine("();");
        source.AppendLine("            var transformerContext = new global::ForgeTrust.AppSurface.Flow.FlowTransformerContext<" + TypeName(node.InputContext!) + ">(");
        source.AppendLine("                context.FlowId,");
        source.AppendLine("                context.Version,");
        source.AppendLine("                context.NodeId,");
        source.AppendLine("                typedState,");
        source.AppendLine("                context.ResumeEvent)");
        source.AppendLine("            {");
        source.AppendLine("                ActivityResult = context.ActivityResult,");
        source.AppendLine("            };");
        source.AppendLine("            var outcome = await _node.ExecuteAsync(transformerContext, cancellationToken).ConfigureAwait(false);");
        source.AppendLine("            if (outcome is null)");
        source.AppendLine("            {");
        source.Append("                throw new global::ForgeTrust.AppSurface.Flow.FlowDefinitionException(\"Generated flow node '")
            .Append(Escape(node.NodeId)).AppendLine("' returned null.\");");
        source.AppendLine("            }");

        source.AppendLine("            return outcome switch");
        source.AppendLine("            {");
        foreach (var outcome in node.Outcomes)
        {
            GenerateOutcomeLowering(source, flow, node, outcome, outcomeType, envelopeName);
        }

        source.Append("                _ => throw new global::ForgeTrust.AppSurface.Flow.FlowDefinitionException(\"Generated flow node '")
            .Append(Escape(node.NodeId)).AppendLine("' returned an unknown outcome.\")");
        source.AppendLine("            };");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void GenerateOutcomeLowering(
        StringBuilder source,
        FlowSpec flow,
        NodeSpec node,
        OutcomeSpec outcome,
        string outcomeType,
        string envelopeName)
    {
        var caseType = outcomeType + "." + CaseName(outcome) + "Outcome";
        if (string.Equals(outcome.Kind, "Next", StringComparison.Ordinal))
        {
            var target = ResolveNextTarget(flow, outcome)!;

            source.Append("                ").Append(caseType).Append(" typed => global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
                .Append(envelopeName).Append(">.Next(\"").Append(Escape(target.NodeId)).Append("\", ")
                .Append(envelopeName).Append(".For").Append(target.Symbol.Name).Append("(typed.Context)),").AppendLine();
        }
        else if (string.Equals(outcome.Kind, "Wait", StringComparison.Ordinal))
        {
            source.Append("                ").Append(caseType).Append(" typed => global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
                .Append(envelopeName).Append(">.Wait(\"").Append(Escape(outcome.Name)).Append("\", ")
                .Append(envelopeName).Append(".For").Append(node.Symbol.Name).Append("(typed.Context), typed.Timeout),").AppendLine();
        }
        else if (string.Equals(outcome.Kind, "TimedOut", StringComparison.Ordinal))
        {
            source.Append("                ").Append(caseType).Append(" typed => global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
                .Append(envelopeName).Append(">.TimedOut(\"").Append(Escape(outcome.Name)).Append("\", ")
                .Append(envelopeName).Append(".For").Append(node.Symbol.Name).Append("(typed.Context)),").AppendLine();
        }
        else if (string.Equals(outcome.Kind, "Complete", StringComparison.Ordinal))
        {
            source.Append("                ").Append(caseType).Append(" typed => global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
                .Append(envelopeName).Append(">.Complete(").Append(envelopeName).Append(".For")
                .Append(SlotPropertyName(outcome.OutputContext!)).Append("(typed.Context, context.NodeId)),").AppendLine();
        }
        else if (string.Equals(outcome.Kind, "Fault", StringComparison.Ordinal))
        {
            source.Append("                ").Append(caseType).Append(" typed => global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
                .Append(envelopeName).Append(">.Fault(typed.Context.Code, typed.Context.Message),").AppendLine();
        }
        else if (string.Equals(outcome.Kind, "Activity", StringComparison.Ordinal))
        {
            source.Append("                ").Append(caseType).Append(" typed => global::ForgeTrust.AppSurface.Flow.FlowNodeOutcome<")
                .Append(envelopeName).Append(">.Activity(").Append(outcomeType).Append(".").Append(CaseName(outcome))
                .Append("Callsite, typed.Work, ").Append(envelopeName).Append(".For").Append(node.Symbol.Name)
                .Append("(typed.Context)),").AppendLine();
        }
    }

    private static void GenerateGraphBuilder(StringBuilder source, FlowSpec flow)
    {
        source.Append("    public sealed class ").Append(GraphBuilderName).AppendLine();
        source.AppendLine("    {");
        source.AppendLine("        private readonly global::System.Collections.Generic.HashSet<string> _mappedOutcomes = new(global::System.StringComparer.Ordinal);");
        source.AppendLine("        private readonly global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<string>> _nextTargets = new(global::System.StringComparer.Ordinal);");
        source.AppendLine();
        source.Append("        private ").Append(GraphBuilderName).AppendLine("() { }");
        source.AppendLine();
        source.Append("        internal static ").Append(GraphBuilderName).AppendLine(" Create() => new();");
        source.AppendLine();

        foreach (var node in flow.Nodes)
        {
            foreach (var outcome in node.Outcomes)
            {
                if (string.Equals(outcome.Kind, "Next", StringComparison.Ordinal))
                {
                    var target = ResolveNextTarget(flow, outcome);
                    if (target is null)
                    {
                        continue;
                    }

                    source.Append("        [global::ForgeTrust.AppSurface.Flow.FlowGraphMapping(\"")
                        .Append(Escape(node.NodeId)).Append("\", \"")
                        .Append(Escape(outcome.Name)).Append("\", typeof(")
                        .Append(TypeName(outcome.OutputContext!)).AppendLine("))]");
                    source.Append("        public ").Append(GraphBuilderName).Append(" ")
                        .Append(GraphBuilderNextMethodName(node, outcome, target)).AppendLine("()");
                    source.AppendLine("        {");
                    source.Append("            MarkOutcome(\"").Append(Escape(OutcomeKey(node, outcome))).Append("\", \"")
                        .Append(Escape(outcome.Name)).Append("\", \"").Append(Escape(node.NodeId)).AppendLine("\");");
                    source.Append("            AddNextTarget(\"").Append(Escape(node.NodeId)).Append("\", \"")
                        .Append(Escape(target.NodeId)).AppendLine("\");");
                    source.AppendLine("            return this;");
                    source.AppendLine("        }");
                    source.AppendLine();
                }
                else
                {
                    source.Append("        [global::ForgeTrust.AppSurface.Flow.FlowGraphMapping(\"")
                        .Append(Escape(node.NodeId)).Append("\", \"")
                        .Append(Escape(outcome.Name)).Append("\", typeof(")
                        .Append(TypeName(outcome.OutputContext!)).AppendLine("))]");
                    source.Append("        public ").Append(GraphBuilderName).Append(" ")
                        .Append(GraphBuilderTerminalMethodName(node, outcome)).AppendLine("()");
                    source.AppendLine("        {");
                    source.Append("            MarkOutcome(\"").Append(Escape(OutcomeKey(node, outcome))).Append("\", \"")
                        .Append(Escape(outcome.Name)).Append("\", \"").Append(Escape(node.NodeId)).AppendLine("\");");
                    source.AppendLine("            return this;");
                    source.AppendLine("        }");
                    source.AppendLine();
                }
            }
        }

        source.AppendLine("        internal string[] NextTargetsFor(string nodeId)");
        source.AppendLine("        {");
        source.AppendLine("            return _nextTargets.TryGetValue(nodeId, out var targets)");
        source.AppendLine("                ? targets.ToArray()");
        source.AppendLine("                : global::System.Array.Empty<string>();");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        internal void ThrowIfIncomplete()");
        source.AppendLine("        {");
        foreach (var node in flow.Nodes)
        {
            foreach (var outcome in node.Outcomes)
            {
                source.Append("            if (!_mappedOutcomes.Contains(\"").Append(Escape(OutcomeKey(node, outcome))).AppendLine("\"))");
                source.AppendLine("            {");
                source.Append("                throw new global::ForgeTrust.AppSurface.Flow.FlowDefinitionException(\"Generated flow outcome '")
                    .Append(Escape(outcome.Name)).Append("' on node '").Append(Escape(node.NodeId))
                    .AppendLine("' must be mapped or marked terminal before building the definition.\");");
                source.AppendLine("            }");
                source.AppendLine();
            }
        }

        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void MarkOutcome(string key, string outcomeName, string nodeId)");
        source.AppendLine("        {");
        source.AppendLine("            if (!_mappedOutcomes.Add(key))");
        source.AppendLine("            {");
        source.AppendLine("                throw new global::ForgeTrust.AppSurface.Flow.FlowDefinitionException(");
        source.AppendLine("                    \"Generated flow outcome '\" + outcomeName + \"' on node '\" + nodeId + \"' was configured more than once.\");");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private void AddNextTarget(string nodeId, string targetNodeId)");
        source.AppendLine("        {");
        source.AppendLine("            if (!_nextTargets.TryGetValue(nodeId, out var targets))");
        source.AppendLine("            {");
        source.AppendLine("                targets = new global::System.Collections.Generic.List<string>();");
        source.AppendLine("                _nextTargets.Add(nodeId, targets);");
        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine("            if (!targets.Contains(targetNodeId))");
        source.AppendLine("            {");
        source.AppendLine("                targets.Add(targetNodeId);");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void GenerateBuildDefinition(StringBuilder source, FlowSpec flow)
    {
        var envelopeName = EnvelopeName(flow);
        var nodeParameters = string.Join(", ", flow.Nodes.Select(node => TypeName(node.Symbol) + " " + Identifier(ParameterName(node.Symbol.Name))));
        source.Append("    public static global::ForgeTrust.AppSurface.Flow.FlowDefinition<").Append(envelopeName).Append("> BuildDefinition(");
        source.Append(nodeParameters);
        source.AppendLine(") =>");
        source.Append("        BuildDefinition(").Append(ConfigureDefaultGraphMethodName);
        foreach (var node in flow.Nodes)
        {
            source.Append(", ").Append(Identifier(ParameterName(node.Symbol.Name)));
        }

        source.AppendLine(");");
        source.AppendLine();
        source.Append("    public static global::ForgeTrust.AppSurface.Flow.FlowDefinition<").Append(envelopeName)
            .Append("> BuildDefinition(global::System.Action<").Append(GraphBuilderName).Append("> ")
            .Append(ConfigureGeneratedGraphParameterName);
        if (nodeParameters.Length > 0)
        {
            source.Append(", ").Append(nodeParameters);
        }

        source.AppendLine(")");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(configureGeneratedGraph);");
        source.Append("        var generatedGraph = ").Append(GraphBuilderName).AppendLine(".Create();");
        source.AppendLine("        configureGeneratedGraph(generatedGraph);");
        source.AppendLine("        generatedGraph.ThrowIfIncomplete();");
        source.AppendLine();
        source.Append("        var builder = global::ForgeTrust.AppSurface.Flow.FlowGraphBuilder<").Append(envelopeName)
            .Append(">.Create(\"").Append(Escape(flow.FlowId)).Append("\", \"").Append(Escape(flow.Version)).AppendLine("\");");
        foreach (var node in flow.Nodes)
        {
            source.Append("        builder.AddNode(\"").Append(Escape(node.NodeId)).Append("\", new ").Append(AdapterName(node)).Append("(")
                .Append(Identifier(ParameterName(node.Symbol.Name))).Append("), generatedGraph.NextTargetsFor(\"")
                .Append(Escape(node.NodeId)).AppendLine("\"));");
        }

        var start = flow.Nodes.First(node => node.IsStart);
        source.Append("        return builder.StartAt(\"").Append(Escape(start.NodeId)).AppendLine("\").Build();");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    private static void ").Append(ConfigureDefaultGraphMethodName).Append("(").Append(GraphBuilderName).AppendLine(" graph)");
        source.AppendLine("    {");
        foreach (var node in flow.Nodes)
        {
            foreach (var outcome in node.Outcomes)
            {
                if (string.Equals(outcome.Kind, "Next", StringComparison.Ordinal))
                {
                    var target = ResolveNextTarget(flow, outcome)!;

                    source.Append("        graph.").Append(GraphBuilderNextMethodName(node, outcome, target)).AppendLine("();");
                }
                else
                {
                    source.Append("        graph.").Append(GraphBuilderTerminalMethodName(node, outcome)).AppendLine("();");
                }
            }
        }

        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    public static ").Append(envelopeName).Append(" CreateStartContext(")
            .Append(TypeName(start.InputContext!)).Append(" state) => ").Append(envelopeName).Append(".For").Append(start.Symbol.Name).AppendLine("(state);");
    }

    private static NodeSpec? ResolveNextTarget(FlowSpec flow, OutcomeSpec outcome)
    {
        var matches = flow.Nodes
            .Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.InputContext, outcome.OutputContext))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static ImmutableArray<ContextSlot> ContextSlots(FlowSpec flow)
    {
        var slots = new List<ContextSlot>();
        foreach (var context in flow.Nodes.Select(node => node.InputContext).Concat(flow.Nodes.SelectMany(node => node.Outcomes.Select(outcome => outcome.OutputContext))))
        {
            if (context is null || slots.Any(slot => SymbolEqualityComparer.Default.Equals(slot.Context, context)))
            {
                continue;
            }

            slots.Add(new ContextSlot(context, SlotPropertyName(context)));
        }

        return slots.ToImmutableArray();
    }

    private static IEnumerable<string> GeneratedNestedTypeNames(FlowSpec flow)
    {
        yield return EnvelopeName(flow);
        yield return GraphBuilderName;
        foreach (var node in flow.Nodes)
        {
            yield return OutcomeTypeName(node);
            yield return AdapterName(node);
        }
    }

    private static IEnumerable<string> GeneratedEnvelopeMemberNames(FlowSpec flow)
    {
        yield return "NodeId";
        yield return "StateKind";
        foreach (var slot in ContextSlots(flow))
        {
            yield return slot.PropertyName;
            yield return "For" + slot.PropertyName;
        }

        foreach (var node in flow.Nodes)
        {
            yield return "For" + node.Symbol.Name;
            yield return "As" + node.Symbol.Name;
        }
    }

    private static IEnumerable<string> GeneratedGraphBuilderMemberNames(FlowSpec flow)
    {
        yield return "Create";
        yield return "NextTargetsFor";
        yield return "ThrowIfIncomplete";
        yield return "MarkOutcome";
        yield return "AddNextTarget";

        foreach (var node in flow.Nodes)
        {
            foreach (var outcome in node.Outcomes)
            {
                if (string.Equals(outcome.Kind, "Next", StringComparison.Ordinal))
                {
                    var target = ResolveNextTarget(flow, outcome);
                    if (target is not null)
                    {
                        yield return GraphBuilderNextMethodName(node, outcome, target);
                    }
                }
                else
                {
                    yield return GraphBuilderTerminalMethodName(node, outcome);
                }
            }
        }
    }

    private static IEnumerable<ExpectedGraphMapping> ExpectedGraphMappings(FlowSpec flow)
    {
        foreach (var node in flow.Nodes)
        {
            foreach (var outcome in node.Outcomes)
            {
                if (string.Equals(outcome.Kind, "Next", StringComparison.Ordinal))
                {
                    var target = ResolveNextTarget(flow, outcome);
                    if (target is not null)
                    {
                        yield return new ExpectedGraphMapping(node, outcome, GraphBuilderNextMethodName(node, outcome, target));
                    }
                }
                else
                {
                    yield return new ExpectedGraphMapping(node, outcome, GraphBuilderTerminalMethodName(node, outcome));
                }
            }
        }
    }

    private static IEnumerable<ExpectedGraphMappingInfo> ExpectedGraphMappingInfos(FlowSpec flow)
    {
        foreach (var mapping in ExpectedGraphMappings(flow))
        {
            yield return new ExpectedGraphMappingInfo(
                mapping.MethodName,
                mapping.Node.NodeId,
                mapping.Outcome.Name,
                mapping.Outcome.OutputContext?.ToDisplayString() ?? "<unknown>",
                mapping.Node.Symbol);
        }
    }

    private static IEnumerable<ExpectedGraphMappingInfo> ExpectedGraphMappingInfos(INamedTypeSymbol graphBuilderType)
    {
        foreach (var method in graphBuilderType.GetMembers().OfType<IMethodSymbol>())
        {
            if (GetAttribute(method, FlowGraphMappingAttributeName) is not { } attribute)
            {
                continue;
            }

            yield return new ExpectedGraphMappingInfo(
                method.Name,
                ConstructorValue(attribute, 0) as string ?? string.Empty,
                ConstructorValue(attribute, 1) as string ?? string.Empty,
                ConstructorValue(attribute, 2) is ITypeSymbol outputContext
                    ? outputContext.ToDisplayString()
                    : "<unknown>",
                null);
        }
    }

    private static INamedTypeSymbol? GraphBuilderType(ITypeSymbol parameterType)
    {
        return parameterType is INamedTypeSymbol named &&
            string.Equals(named.Name, "Action", StringComparison.Ordinal) &&
            named.ContainingNamespace.ToDisplayString() == "System" &&
            named.TypeArguments.Length == 1 &&
            named.TypeArguments[0] is INamedTypeSymbol graphBuilderType
            ? graphBuilderType
            : null;
    }

    private static string EnvelopeName(FlowSpec flow) => flow.Symbol.Name + "Context";

    private static string OutcomeTypeName(NodeSpec node) => node.Symbol.Name + "Outcomes";

    private static string AdapterName(NodeSpec node) => "__" + node.Symbol.Name + "Adapter";

    private static string CaseName(OutcomeSpec outcome)
    {
        var identifier = ToPascalIdentifier(outcome.Name);
        return identifier[0] == '_' ? "Outcome" + identifier.Substring(1) : identifier;
    }

    private static IEnumerable<string> GeneratedOutcomeUnionMemberNames(NodeSpec node)
    {
        yield return "RequireContext";
        yield return "Clone";
        yield return "EqualityContract";
        foreach (var outcome in node.Outcomes)
        {
            var caseName = CaseName(outcome);
            yield return caseName;
            yield return caseName + "Outcome";
            if (string.Equals(outcome.Kind, "Activity", StringComparison.Ordinal))
            {
                yield return caseName + "Callsite";
            }

            if (OutcomeFactoryHasSingleContextParameter(outcome) &&
                string.Equals(caseName, "Equals", StringComparison.Ordinal) &&
                outcome.OutputContext?.SpecialType == SpecialType.System_Object)
            {
                yield return "Equals";
            }

            if (OutcomeFactoryHasSingleContextParameter(outcome) &&
                string.Equals(caseName, "PrintMembers", StringComparison.Ordinal) &&
                string.Equals(
                    outcome.OutputContext?.ToDisplayString(),
                    "System.Text.StringBuilder",
                    StringComparison.Ordinal))
            {
                yield return "PrintMembers";
            }
        }
    }

    private static bool OutcomeFactoryHasSingleContextParameter(OutcomeSpec outcome) =>
        !string.Equals(outcome.Kind, "Activity", StringComparison.Ordinal) &&
        !string.Equals(outcome.Kind, "Wait", StringComparison.Ordinal);

    private static string GraphBuilderNextMethodName(NodeSpec node, OutcomeSpec outcome, NodeSpec target) =>
        "Map" + node.Symbol.Name + CaseName(outcome) + "To" + target.Symbol.Name;

    private static string GraphBuilderTerminalMethodName(NodeSpec node, OutcomeSpec outcome) =>
        "Mark" + node.Symbol.Name + CaseName(outcome) + "Terminal";

    private static string OutcomeKey(NodeSpec node, OutcomeSpec outcome) =>
        node.NodeId.Length.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ":" + node.NodeId +
        "|" + outcome.Name.Length.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ":" + outcome.Name;

    private static string ActivityCallsiteId(NodeSpec node, OutcomeSpec outcome) =>
        outcome.CallsiteId ?? node.NodeId + "." + outcome.Name;

    private static string? OutcomeKindName(int value) =>
        value switch
        {
            0 => "Next",
            1 => "Wait",
            2 => "TimedOut",
            3 => "Complete",
            4 => "Fault",
            5 => "Activity",
            _ => null,
        };

    private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

    private static string FlowDiagnosticName(FlowSpec flow) => HasText(flow.FlowId) ? flow.FlowId : flow.Symbol.Name;

    private static string OutcomeDiagnosticName(OutcomeSpec outcome) => HasText(outcome.Name) ? outcome.Name : "<empty>";

    private static object? ConstructorValue(AttributeData attribute, int index) =>
        index < attribute.ConstructorArguments.Length
            ? attribute.ConstructorArguments[index].Value
            : null;

    private static object? NamedValue(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value.Value;

    private static string SlotPropertyName(ITypeSymbol context) =>
        context switch
        {
            IArrayTypeSymbol array => SlotPropertyName(array.ElementType) + "Array",
            INamedTypeSymbol named => ToPascalIdentifier(named.Name),
            _ => ToPascalIdentifier(context.Name),
        };

    private static string ParameterName(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static string ToPascalIdentifier(string value)
    {
        var builder = new StringBuilder();
        var nextUpper = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                nextUpper = true;
                continue;
            }

            if (builder.Length == 0 && char.IsDigit(character))
            {
                builder.Append('_');
            }

            builder.Append(nextUpper ? char.ToUpperInvariant(character) : character);
            nextUpper = false;
        }

        return builder.Length == 0 ? "Outcome" : builder.ToString();
    }

    private static string TypeName(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\0':
                    builder.Append(@"\0");
                    break;
                case '\a':
                    builder.Append(@"\a");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\v':
                    builder.Append(@"\v");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append(@"\u").Append(((int)character).ToString("x4", global::System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static string HintName(INamedTypeSymbol symbol)
    {
        var displayName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return ToPascalIdentifier(displayName) + "_" + StableHash(displayName);
    }

    private static string StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash.ToString("x8", global::System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Identifier(string value) =>
        SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None &&
        SyntaxFacts.GetContextualKeywordKind(value) == SyntaxKind.None
            ? value
            : "@" + value;

    private static string NamespaceName(INamespaceSymbol symbol)
    {
        var segments = new Stack<string>();
        for (var current = symbol; current is { IsGlobalNamespace: false }; current = current.ContainingNamespace)
        {
            segments.Push(Identifier(current.Name));
        }

        return string.Join(".", segments);
    }

    private static string AccessibilityKeyword(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Public => "public",
            _ => "internal",
        };

    private static bool IsExposableFromFlow(ITypeSymbol symbol, INamedTypeSymbol flow)
    {
        var flowAccessibility = EffectiveAccessibility(flow);
        return IsExposable(symbol, flowAccessibility == Accessibility.Public);
    }

    private static bool IsExposable(ITypeSymbol symbol, bool requiresPublic)
    {
        switch (symbol)
        {
            case IArrayTypeSymbol array:
                return IsExposable(array.ElementType, requiresPublic);
            case INamedTypeSymbol named:
                var symbolAccessibility = EffectiveAccessibility(named);
                var isAccessible = requiresPublic
                    ? symbolAccessibility == Accessibility.Public
                    : symbolAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

                return isAccessible && named.TypeArguments.All(argument => IsExposable(argument, requiresPublic));
            default:
                return false;
        }
    }

    private static bool IsConcreteContextType(ITypeSymbol symbol)
    {
        switch (symbol)
        {
            case IArrayTypeSymbol array:
                return IsConcreteContextType(array.ElementType);
            case INamedTypeSymbol named:
                return named.SpecialType != SpecialType.System_Void &&
                    named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T &&
                    !named.IsUnboundGenericType &&
                    named.TypeArguments.All(IsConcreteContextType);
            default:
                return false;
        }
    }

    private static Accessibility EffectiveAccessibility(ISymbol symbol)
    {
        var sawInternal = false;
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Private:
                    return Accessibility.Private;
                case Accessibility.Protected:
                    return Accessibility.Protected;
                case Accessibility.ProtectedAndInternal:
                    return Accessibility.ProtectedAndInternal;
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                    sawInternal = true;
                    break;
            }
        }

        return sawInternal ? Accessibility.Internal : Accessibility.Public;
    }

    private static bool ImplementsTransformerNode(NodeSpec node)
    {
        return node.Symbol.AllInterfaces.Any(candidate =>
            string.Equals(candidate.ConstructedFrom.Name, "IFlowTransformerNode", StringComparison.Ordinal) &&
            string.Equals(candidate.ConstructedFrom.ContainingNamespace.ToDisplayString(), FlowNamespace, StringComparison.Ordinal) &&
            candidate.TypeArguments.Length == 2 &&
            SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], node.InputContext) &&
            candidate.TypeArguments[1] is INamedTypeSymbol outcomeType &&
            string.Equals(outcomeType.Name, OutcomeTypeName(node), StringComparison.Ordinal) &&
            (outcomeType.TypeKind == TypeKind.Error ||
                SymbolEqualityComparer.Default.Equals(outcomeType.ContainingType, node.Symbol.ContainingType)));
    }

    private static bool IsPartial(TypeDeclarationSyntax declaration) =>
        declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));

    private static AttributeData? GetAttribute(INamedTypeSymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));

    private static AttributeData? GetAttribute(IMethodSymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));

    private sealed record FlowSpec(
        INamedTypeSymbol Symbol,
        TypeDeclarationSyntax Declaration,
        string FlowId,
        string Version,
        IReadOnlyList<NodeSpec> Nodes);

    private sealed record NodeSpec(
        INamedTypeSymbol Symbol,
        string NodeId,
        ITypeSymbol? InputContext,
        bool IsStart,
        IReadOnlyList<OutcomeSpec> Outcomes);

    private sealed record OutcomeSpec(
        string Name,
        string? Kind,
        ITypeSymbol? OutputContext,
        ITypeSymbol? WorkType,
        ITypeSymbol? ResultType,
        string? CallsiteId,
        int WorkContractVersion,
        int ResultContractVersion);

    private sealed record ContextSlot(
        ITypeSymbol Context,
        string PropertyName);

    private sealed record ExpectedGraphMapping(
        NodeSpec Node,
        OutcomeSpec Outcome,
        string MethodName);

    private sealed record ExpectedGraphMappingInfo(
        string MethodName,
        string NodeId,
        string OutcomeName,
        string OutputContextName,
        ITypeSymbol? NodeType);

    private sealed record GraphMappingInvocation(
        string Name,
        Location Location,
        int SpanStart = -1);

    private sealed record FlowGenerationInput(
        Compilation Compilation,
        ImmutableArray<FlowSpec> FlowSpecs);
}
