using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using ForgeTrust.AppSurface.Deployment;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Aspire;

#pragma warning disable ASPIREPIPELINES001 // Aspire 13.4.4 pipeline API is intentionally isolated in this adapter.
#pragma warning disable ASPIREPIPELINES004 // Aspire 13.4.4 output service is intentionally isolated in this adapter.

/// <summary>Represents one artifact-only deployment target in the evaluated Aspire resource graph.</summary>
/// <remarks>The resource has no runtime lifetime or endpoint. It carries provider inputs and a per-pipeline render cache.</remarks>
internal sealed class AppSurfaceDeploymentTargetResource : Resource, IComputeEnvironmentResource, IResourceWithoutLifetime
{
    /// <summary>Initializes an artifact-only deployment target resource.</summary>
    /// <param name="name">Unique Aspire resource name.</param>
    /// <param name="target">Aspire-independent deployment compiler and verifier.</param>
    /// <param name="bindingProfile">Non-secret parameter containing the provider binding-profile path.</param>
    /// <param name="sourceRevision">Non-secret parameter containing the full source revision.</param>
    /// <param name="appHostDirectory">Trusted AppHost directory used to confine profile resolution.</param>
    /// <param name="environmentName">Aspire-selected environment captured for intent traceability.</param>
    public AppSurfaceDeploymentTargetResource(
        string name,
        IDeploymentTarget target,
        ParameterResource bindingProfile,
        ParameterResource sourceRevision,
        string appHostDirectory,
        string environmentName)
        : base(name)
    {
        Target = target;
        BindingProfile = bindingProfile;
        SourceRevision = sourceRevision;
        AppHostDirectory = appHostDirectory;
        EnvironmentName = environmentName;
    }

    /// <summary>Gets the provider target.</summary>
    public IDeploymentTarget Target { get; }

    /// <summary>Gets the non-secret binding-profile parameter.</summary>
    public ParameterResource BindingProfile { get; }

    /// <summary>Gets the non-secret source-revision parameter.</summary>
    public ParameterResource SourceRevision { get; }

    /// <summary>Gets the trusted AppHost directory.</summary>
    public string AppHostDirectory { get; }

    /// <summary>Gets the Aspire-selected environment name.</summary>
    public string EnvironmentName { get; }

    /// <summary>Gets or sets the render result produced by the publish dependency in the current pipeline process.</summary>
    /// <remarks>Verification reuses this value only after its declared publish dependency completes; a new AppHost process starts without cached evidence.</remarks>
    public DeploymentRenderResult? LastRenderResult { get; set; }

    /// <summary>Rejects endpoint projection because artifact-only targets do not expose deployed addresses.</summary>
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) =>
        throw new NotSupportedException("ASDEPLOY208: the artifact-only AppSurface target does not expose deployed endpoint addresses.");
}

/// <summary>Immutable Aspire annotation projected into one provider-neutral migration-job intent.</summary>
/// <param name="ResourceName">Canonical logical resource name.</param>
/// <param name="Phase">Explicit deployment phase.</param>
/// <param name="Image">Non-secret immutable image parameter.</param>
/// <param name="Command">Executable without shell parsing.</param>
/// <param name="Arguments">Ordered executable arguments.</param>
/// <param name="Execution">Bounded run-to-completion policy.</param>
/// <param name="ConnectionSecret">Secret-classified parameter retained only by logical name.</param>
/// <param name="ConfigurationKey">Application configuration key populated by the provider secret reference.</param>
/// <param name="RequirePrivateNetwork">Whether the required v1 private-network declaration was made.</param>
internal sealed record AppSurfaceMigrationJobAnnotation(
    string ResourceName,
    DeploymentPhase Phase,
    ParameterResource Image,
    string Command,
    IReadOnlyList<string> Arguments,
    DeploymentExecutionPolicy Execution,
    ParameterResource ConnectionSecret,
    string ConfigurationKey,
    bool RequirePrivateNetwork) : IResourceAnnotation;

/// <summary>Isolates the repository-pinned Aspire 13.4.4 experimental deployment-pipeline API.</summary>
internal static class AspireDeploymentPipelineAdapter
{
    /// <summary>Gets the artifact-only step required by Aspire publish.</summary>
    internal const string PublishStepName = "appsurface-gcp-publish";
    /// <summary>Gets the named read-only parity step, which depends on artifact publication.</summary>
    internal const string VerifyStepName = "appsurface-gcp-verify";
    /// <summary>Gets the Aspire aggregation step that requires AppSurface artifact publication.</summary>
    internal const string PublishRequiredByStepName = WellKnownPipelineSteps.Publish;
    /// <summary>Gets the artifact step required before named verification.</summary>
    internal const string VerifyDependsOnStepName = PublishStepName;
    /// <summary>Gets the user-facing pure-publish description.</summary>
    internal const string PublishStepDescription = "Render deterministic AppSurface deployment artifacts without cloud calls or infrastructure changes.";
    /// <summary>Gets the user-facing read-only verification description.</summary>
    internal const string VerifyStepDescription = "Regenerate artifacts and perform read-only GCP deployment parity verification.";

    /// <summary>Registers publish and verification factories with their native Aspire dependency relationships.</summary>
    /// <param name="builder">Target resource builder receiving pipeline annotations.</param>
    /// <param name="resource">Concrete target resource captured by the step actions.</param>
    internal static void Register(
        IResourceBuilder<IComputeEnvironmentResource> builder,
        AppSurfaceDeploymentTargetResource resource)
    {
        builder.WithPipelineStepFactory(
            PublishStepName,
            context => PublishAsync(resource, context),
            dependsOn: [],
            requiredBy: [PublishRequiredByStepName],
            tags: [],
            description: PublishStepDescription);

        builder.WithPipelineStepFactory(
            VerifyStepName,
            context => VerifyAsync(resource, context),
            dependsOn: [VerifyDependsOnStepName],
            requiredBy: [],
            tags: [],
            description: VerifyStepDescription);
    }

    /// <summary>Projects explicitly annotated resources assigned to one target into canonical neutral intent.</summary>
    /// <param name="targetResource">Target whose assigned annotations are selected.</param>
    /// <param name="model">Evaluated Aspire model.</param>
    /// <param name="cancellationToken">Cancellation observed during non-secret parameter resolution.</param>
    /// <returns>Validated, sorted deployment intent.</returns>
    internal static async Task<DeploymentIntent> BuildIntentAsync(
        AppSurfaceDeploymentTargetResource targetResource,
        DistributedApplicationModel model,
        CancellationToken cancellationToken)
    {
        var assignedAnnotations = new List<AppSurfaceMigrationJobAnnotation>();

        foreach (var resource in model.Resources.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (!resource.TryGetLastAnnotation<AppSurfaceMigrationJobAnnotation>(out var annotation) ||
                annotation is null)
            {
                continue;
            }

            var computeEnvironment = resource.GetComputeEnvironment();
            if (computeEnvironment is null)
            {
                throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                    "ASDEPLOY217",
                    "Migration job has no deployment target.",
                    $"Annotated resource '{resource.Name}' is not assigned to a compute environment.",
                    "Call WithComputeEnvironment with one explicit AppSurface deployment target."));
            }

            if (!ReferenceEquals(computeEnvironment, targetResource)) continue;

            if (!annotation.ConnectionSecret.Secret)
            {
                throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                    "ASDEPLOY205",
                    "Connection parameter is not secret.",
                    $"Parameter '{annotation.ConnectionSecret.Name}' was declared as non-secret.",
                    "Declare the Aspire parameter with secret: true; AppSurface records only its logical name."));
            }

            var missingCapability = new[]
                {
                    DeploymentCapability.PrivateNetwork,
                    DeploymentCapability.RelationalConnection,
                    DeploymentCapability.RunToCompletionJob,
                }
                .Where(capability => !targetResource.Target.Capabilities.Contains(capability))
                .Select(capability => (DeploymentCapability?)capability)
                .FirstOrDefault();
            if (missingCapability.HasValue)
            {
                throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                    "ASDEPLOY209",
                    "Deployment target lacks a required capability.",
                    $"Target '{targetResource.Target.Name}' does not support '{missingCapability.Value}'.",
                    "Choose a compatible target or remove the unsupported requirement."));
            }

            assignedAnnotations.Add(annotation);
        }

        if (assignedAnnotations.Count == 0)
        {
            throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                "ASDEPLOY118",
                "No migration jobs were assigned.",
                "The target found no explicit migration annotations.",
                "Annotate and explicitly assign at least one project resource."));
        }

        var sourceValue = await ResolveNonSecretAsync(targetResource.SourceRevision, "source revision", cancellationToken);
        var jobs = new List<MigrationJobIntent>(assignedAnnotations.Count);
        foreach (var annotation in assignedAnnotations)
        {
            var imageValue = await ResolveNonSecretAsync(annotation.Image, "immutable image", cancellationToken);
            var jobId = new DeploymentLogicalId(annotation.ResourceName);
            var secretId = new DeploymentLogicalId(annotation.ConnectionSecret.Name);
            var secret = new SecretBinding(secretId, secretId, annotation.ConfigurationKey);
            var database = new DatabaseBinding(jobId, annotation.ConfigurationKey, secretId);
            jobs.Add(new MigrationJobIntent(
                jobId,
                annotation.Phase,
                new ImmutableImageReference(imageValue),
                annotation.Command,
                annotation.Arguments,
                annotation.Execution,
                secret,
                database,
                jobId,
                annotation.RequirePrivateNetwork));
        }

        return new DeploymentIntent(targetResource.EnvironmentName, new SourceRevision(sourceValue), jobs);
    }

    private static async Task PublishAsync(AppSurfaceDeploymentTargetResource resource, PipelineStepContext context)
    {
        var result = await RenderAndWriteAsync(resource, context.Model, context.Services, context.CancellationToken);
        resource.LastRenderResult = result;
        await CompletePublishAsync(resource, result, context.Summary.Add, context.ReportingStep, context.CancellationToken);
    }

    private static async Task VerifyAsync(AppSurfaceDeploymentTargetResource resource, PipelineStepContext context)
    {
        var renderResult = resource.LastRenderResult ?? await RenderAndWriteAsync(resource, context.Model, context.Services, context.CancellationToken);
        await VerifyRenderedAsync(resource, renderResult, context.Summary.Add, context.ReportingStep, context.CancellationToken);
    }

    /// <summary>Reports publish evidence and the no-mutation assurance through Aspire.</summary>
    internal static async Task CompletePublishAsync(
        AppSurfaceDeploymentTargetResource resource,
        DeploymentRenderResult result,
        Action<string, string> addSummary,
        IReportingStep reportingStep,
        CancellationToken cancellationToken)
    {
        addSummary("AppSurface target", resource.Target.Name);
        addSummary("AppSurface artifacts", string.Join(", ", result.Artifacts.Select(artifact => artifact.FileName)));
        await reportingStep.SucceedAsync(
            "No cloud calls were made. No infrastructure was changed.",
            cancellationToken);
    }

    /// <summary>Runs shadow parity verification and reports a read-only success or typed failure.</summary>
    internal static async Task VerifyRenderedAsync(
        AppSurfaceDeploymentTargetResource resource,
        DeploymentRenderResult renderResult,
        Action<string, string> addSummary,
        IReportingStep reportingStep,
        CancellationToken cancellationToken)
    {
        var result = await resource.Target.VerifyAsync(
            new DeploymentVerifyRequest(renderResult, DeploymentParityMode.Shadow),
            cancellationToken);

        addSummary("AppSurface parity fields", result.ComparedFields.ToString(System.Globalization.CultureInfo.InvariantCulture));
        addSummary("AppSurface authorization", result.AuthorizationStatus);
        if (!result.IsMatch)
        {
            var message = result.Diagnostics.Count == 0
                ? "ASDEPLOY210: deployed configuration does not match the rendered plan."
                : string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Problem}"));
            await reportingStep.FailAsync(message, cancellationToken);
            throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                "ASDEPLOY210",
                "Deployed configuration does not match.",
                message,
                "Review the normalized parity diagnostics before changing the authoritative writer."));
        }

        await reportingStep.SucceedAsync(
            "Read-only verification completed. No job was executed.",
            cancellationToken);
    }

    /// <summary>Builds intent, confines the profile, invokes the pure provider renderer, and atomically writes one complete bundle.</summary>
    /// <returns>The provider result plus any adapter-owned neutral-intent artifact.</returns>
    internal static async Task<DeploymentRenderResult> RenderAndWriteAsync(
        AppSurfaceDeploymentTargetResource resource,
        DistributedApplicationModel model,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var intent = await BuildIntentAsync(resource, model, cancellationToken);
        var bindingValue = await ResolveNonSecretAsync(resource.BindingProfile, "binding profile", cancellationToken);
        var bindingPath = ResolveBindingProfilePath(resource.AppHostDirectory, bindingValue);
        var outputService = services.GetRequiredService<IPipelineOutputService>();
        var outputDirectory = outputService.GetOutputDirectory(resource);
        var generatorVersion = typeof(AspireDeploymentPipelineAdapter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var providerResult = await resource.Target.RenderAsync(
            new DeploymentRenderRequest(intent, bindingPath, outputDirectory, generatorVersion, resource.AppHostDirectory),
            cancellationToken);
        if (!string.Equals(providerResult.Target, resource.Target.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ASDEPLOY220: provider result target '{providerResult.Target}' does not match configured target '{resource.Target.Name}'.");
        }

        if (providerResult.Artifacts is null || providerResult.Artifacts.Any(artifact => artifact is null))
        {
            throw new InvalidOperationException("ASDEPLOY211: target returned a null deployment artifact set or item.");
        }

        var canonicalIntent = DeploymentArtifact.Create("deployment-intent.v1.json", DeploymentCanonicalJson.Serialize(intent));
        var artifacts = providerResult.Artifacts.OrderBy(artifact => artifact.FileName, StringComparer.Ordinal).ToList();
        var providerIntent = artifacts.FirstOrDefault(artifact => string.Equals(artifact.FileName, canonicalIntent.FileName, StringComparison.Ordinal));
        if (providerIntent is null)
        {
            artifacts.Insert(0, canonicalIntent);
        }
        else if (!providerIntent.Content.AsSpan().SequenceEqual(canonicalIntent.Content))
        {
            throw new InvalidOperationException("ASDEPLOY221: target returned deployment intent that does not match the evaluated Aspire graph.");
        }

        artifacts.Sort((left, right) => StringComparer.Ordinal.Compare(left.FileName, right.FileName));

        var duplicate = artifacts.GroupBy(artifact => artifact.FileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"ASDEPLOY211: target returned duplicate artifact name '{duplicate.Key}'.");
        }

        await DeploymentArtifactBundleWriter.WriteAsync(
            outputDirectory,
            resource.Name,
            artifacts,
            cancellationToken);
        return new DeploymentRenderResult(providerResult.Target, artifacts.AsReadOnly());
    }

    private static async Task<string> ResolveNonSecretAsync(
        ParameterResource parameter,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (parameter.Secret)
        {
            throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                "ASDEPLOY212",
                $"{purpose} parameter is secret.",
                $"Parameter '{parameter.Name}' cannot be resolved into deployment evidence.",
                "Declare only non-secret deployment metadata on this input."));
        }

        var value = await parameter.GetValueAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                "ASDEPLOY213",
                $"{purpose} parameter is missing.",
                $"Parameter '{parameter.Name}' did not resolve to a value.",
                "Supply the parameter through the standard Aspire parameter mechanism."));
        }

        return value;
    }

    /// <summary>Resolves a relative profile path lexically beneath the trusted AppHost directory.</summary>
    /// <remarks>The provider loader performs the filesystem-level symbolic-link check before reading.</remarks>
    internal static string ResolveBindingProfilePath(string appHostDirectory, string bindingProfilePath)
    {
        if (Path.IsPathFullyQualified(bindingProfilePath))
        {
            throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                "ASDEPLOY218",
                "Binding-profile path must be relative.",
                "An absolute binding-profile path would make the AppHost non-portable.",
                "Use a path relative to the AppHost directory."));
        }

        var root = Path.GetFullPath(appHostDirectory);
        var resolved = Path.GetFullPath(bindingProfilePath, root);
        var relative = Path.GetRelativePath(root, resolved);
        if (Path.IsPathFullyQualified(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new DeploymentValidationException(DeploymentDiagnostic.Create(
                "ASDEPLOY219",
                "Binding-profile path escapes the AppHost directory.",
                $"'{bindingProfilePath}' resolves outside the AppHost boundary.",
                "Keep the checked-in non-secret binding profile beneath the AppHost directory."));
        }

        return resolved;
    }

}

#pragma warning restore ASPIREPIPELINES004
#pragma warning restore ASPIREPIPELINES001
