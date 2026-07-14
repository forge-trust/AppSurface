using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>Extends an Aspire resource graph with explicit AppSurface deployment intent.</summary>
public static class AppSurfaceDeploymentBuilderExtensions
{
    /// <summary>Adds one artifact-only deployment target to the native Aspire deployment pipeline.</summary>
    /// <param name="builder">The native Aspire application builder.</param>
    /// <param name="name">The unique Aspire compute-environment resource name.</param>
    /// <param name="target">The provider compiler and read-only verifier.</param>
    /// <param name="bindingProfile">A non-secret parameter containing the provider binding-profile path.</param>
    /// <param name="sourceRevision">A non-secret parameter containing a full lowercase source commit.</param>
    /// <returns>The compute environment used with Aspire's <c>WithComputeEnvironment</c> extension.</returns>
    /// <remarks>
    /// The publish step writes artifacts only. It does not call a cloud API, apply infrastructure, resolve secret
    /// values, execute a job, or change traffic. Verification is exposed as the separately named
    /// <c>appsurface-gcp-verify</c> step, performs shadow parity for pre-cutover adoption, and is required to remain
    /// read-only. After writer cutover, the application-owned release workflow may call the provider target directly
    /// with <see cref="DeploymentParityMode.Owned"/>.
    /// </remarks>
    public static IResourceBuilder<IComputeEnvironmentResource> AddAppSurfaceDeploymentTarget(
        this IDistributedApplicationBuilder builder,
        string name,
        IDeploymentTarget target,
        IResourceBuilder<ParameterResource> bindingProfile,
        IResourceBuilder<ParameterResource> sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(bindingProfile);
        ArgumentNullException.ThrowIfNull(sourceRevision);

        if (bindingProfile.ApplicationBuilder != builder || sourceRevision.ApplicationBuilder != builder)
        {
            throw new ArgumentException("ASDEPLOY201: deployment target parameters must belong to the same Aspire application builder.");
        }

        if (bindingProfile.Resource.Secret || sourceRevision.Resource.Secret)
        {
            throw new ArgumentException("ASDEPLOY202: binding-profile and source-revision parameters must be non-secret metadata.");
        }

        var resource = new AppSurfaceDeploymentTargetResource(
            name,
            target,
            bindingProfile.Resource,
            sourceRevision.Resource,
            builder.AppHostDirectory,
            builder.Environment.EnvironmentName);
        var resourceBuilder = builder.AddResource<IComputeEnvironmentResource>(resource);
        AspireDeploymentPipelineAdapter.Register(resourceBuilder, resource);
        return resourceBuilder;
    }

    /// <summary>Annotates an existing Aspire project as one explicit migration-job intent.</summary>
    /// <param name="builder">The existing project resource builder; no second project resource is created.</param>
    /// <param name="configure">Configures immutable image, command, secret reference, and execution bounds.</param>
    /// <returns>The original project builder for chaining.</returns>
    /// <remarks>
    /// This annotation never reads the connection parameter value. Assign the returned project explicitly with
    /// Aspire's <c>WithComputeEnvironment</c>; unassigned annotations are rejected during publish rather than inferred.
    /// Calling this method twice for the same resource is an error.
    /// </remarks>
    public static IResourceBuilder<ProjectResource> WithAppSurfaceMigrationJob(
        this IResourceBuilder<ProjectResource> builder,
        Action<AppSurfaceMigrationJobOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (builder.Resource.HasAnnotationOfType<AppSurfaceMigrationJobAnnotation>())
        {
            throw new InvalidOperationException($"ASDEPLOY203: project resource '{builder.Resource.Name}' already has an AppSurface migration-job annotation.");
        }

        var options = new AppSurfaceMigrationJobOptions(builder.Resource.Name, builder.ApplicationBuilder);
        configure(options);
        builder.WithAnnotation(options.Build());
        return builder;
    }
}

/// <summary>Builds the provider-neutral intent annotation for one existing Aspire project resource.</summary>
public sealed class AppSurfaceMigrationJobOptions
{
    private readonly IDistributedApplicationBuilder _applicationBuilder;
    private readonly string _resourceName;
    private readonly List<string> _arguments = [];
    private ParameterResource? _image;
    private ParameterResource? _connectionSecret;
    private DeploymentPhase _phase = DeploymentPhase.CandidatePreparation;
    private string? _command;
    private string _configurationKey = "ConnectionStrings__database";
    private DeploymentExecutionPolicy? _execution;
    private bool _requirePrivateNetwork;

    internal AppSurfaceMigrationJobOptions(string resourceName, IDistributedApplicationBuilder applicationBuilder)
    {
        _resourceName = resourceName;
        _applicationBuilder = applicationBuilder;
    }

    /// <summary>Uses a non-secret parameter containing a full immutable container image identity.</summary>
    public AppSurfaceMigrationJobOptions WithImage(IResourceBuilder<ParameterResource> image)
    {
        RequireSameBuilder(image, nameof(image));
        if (image.Resource.Secret) throw new ArgumentException("ASDEPLOY204: immutable image identity must be a non-secret parameter.", nameof(image));
        _image = image.Resource;
        return this;
    }

    /// <summary>Sets the deployment phase. Version 1 supports candidate preparation.</summary>
    public AppSurfaceMigrationJobOptions WithPhase(DeploymentPhase phase)
    {
        _phase = phase;
        return this;
    }

    /// <summary>Sets the executable and ordered arguments without parsing a shell command line.</summary>
    public AppSurfaceMigrationJobOptions WithCommand(string command, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        _command = command;
        _arguments.Clear();
        _arguments.AddRange(arguments);
        return this;
    }

    /// <summary>Records a secret parameter by logical name without resolving its value.</summary>
    /// <param name="secret">An Aspire secret parameter used only as a logical reference.</param>
    /// <param name="configurationKey">The application configuration key populated by the provider reference.</param>
    public AppSurfaceMigrationJobOptions WithConnectionSecret(
        IResourceBuilder<ParameterResource> secret,
        string configurationKey)
    {
        RequireSameBuilder(secret, nameof(secret));
        if (!secret.Resource.Secret) throw new ArgumentException("ASDEPLOY205: connection secret must be declared as an Aspire secret parameter.", nameof(secret));
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        _connectionSecret = secret.Resource;
        _configurationKey = configurationKey;
        return this;
    }

    /// <summary>
    /// Requires the provider target to supply private networking. Version 1 supports candidate preparation and
    /// requires every migration-job configuration to call this method; otherwise building the annotation throws
    /// <c>ASDEPLOY207</c>.
    /// </summary>
    public AppSurfaceMigrationJobOptions RequirePrivateNetwork()
    {
        _requirePrivateNetwork = true;
        return this;
    }

    /// <summary>Sets explicit task, parallelism, retry, and timeout bounds.</summary>
    public AppSurfaceMigrationJobOptions WithExecutionPolicy(int tasks, int parallelism, int retries, TimeSpan timeout)
    {
        _execution = new DeploymentExecutionPolicy(tasks, parallelism, retries, timeout);
        return this;
    }

    internal AppSurfaceMigrationJobAnnotation Build()
    {
        if (_image is null) throw Missing("immutable image parameter");
        if (_connectionSecret is null) throw Missing("connection secret reference");
        if (_command is null) throw Missing("migration command");
        if (_execution is null) throw Missing("execution policy");
        if (!_requirePrivateNetwork) throw Missing("private-network requirement");

        return new AppSurfaceMigrationJobAnnotation(
            _resourceName,
            _phase,
            _image,
            _command,
            _arguments.ToArray(),
            _execution,
            _connectionSecret,
            _configurationKey,
            _requirePrivateNetwork);
    }

    private void RequireSameBuilder(IResourceBuilder<ParameterResource> parameter, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameter, parameterName);
        if (parameter.ApplicationBuilder != _applicationBuilder)
        {
            throw new ArgumentException("ASDEPLOY206: migration-job parameters must belong to the same Aspire application builder.", parameterName);
        }
    }

    private InvalidOperationException Missing(string value) =>
        new($"ASDEPLOY207: migration resource '{_resourceName}' is missing its required {value}.");
}
