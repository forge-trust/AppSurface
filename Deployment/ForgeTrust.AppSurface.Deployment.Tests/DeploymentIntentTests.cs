using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.Tests;

public sealed class DeploymentIntentTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("INVALID-NAME")]
    public void SecretBindingRejectsInvalidEnvironmentVariable(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => new SecretBinding(Id("connection"), Id("parameter"), name));
        Assert.Contains("ASDEPLOY108", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretBindingRetainsLogicalReferencesWithoutAValue()
    {
        var binding = new SecretBinding(Id("connection"), Id("connection-parameter"), "ConnectionStrings__app");
        Assert.Equal(Id("connection"), binding.Id);
        Assert.Equal(Id("connection-parameter"), binding.Parameter);
        Assert.Equal("ConnectionStrings__app", binding.EnvironmentVariable);
        Assert.Equal(SecretDeliveryKind.EnvironmentVariable, binding.Delivery);
    }

    [Fact]
    public void BindingsRejectUninitializedLogicalIds()
    {
        Assert.Contains("ASDEPLOY101", Assert.Throws<ArgumentException>(() => new SecretBinding(default, Id("parameter"), "VALUE")).Message, StringComparison.Ordinal);
        Assert.Contains("ASDEPLOY101", Assert.Throws<ArgumentException>(() => new SecretBinding(Id("secret"), default, "VALUE")).Message, StringComparison.Ordinal);
        Assert.Contains("ASDEPLOY101", Assert.Throws<ArgumentException>(() => new DatabaseBinding(default, "DATABASE", Id("secret"))).Message, StringComparison.Ordinal);
        Assert.Contains("ASDEPLOY101", Assert.Throws<ArgumentException>(() => new DatabaseBinding(Id("database"), "DATABASE", default)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DatabaseBindingRejectsBlankConfigurationKey(string key)
    {
        var error = Assert.Throws<ArgumentException>(() => new DatabaseBinding(Id("database"), key, Id("connection")));
        Assert.Contains("ASDEPLOY109", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseBindingRetainsExplicitConstraints()
    {
        var binding = new DatabaseBinding(Id("database"), "ConnectionStrings__app", Id("connection"), false, false);
        Assert.False(binding.RequireUnixSocket);
        Assert.False(binding.MigrationOwner);
    }

    [Fact]
    public void MigrationJobRejectsUnknownPhase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateJob(phase: (DeploymentPhase)42));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MigrationJobRejectsBlankCommand(string command)
    {
        var error = Assert.Throws<ArgumentException>(() => CreateJob(command: command));
        Assert.Contains("ASDEPLOY110", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationJobRejectsMismatchedConnectionBinding()
    {
        var error = Assert.Throws<DeploymentValidationException>(() => CreateJob(databaseSecret: Id("other-secret")));
        Assert.Equal("ASDEPLOY111", error.Diagnostic.Code);
    }

    [Fact]
    public void MigrationJobRequiresExplicitMigrationOwnership()
    {
        var error = Assert.Throws<DeploymentValidationException>(() => CreateJob(migrationOwner: false));
        Assert.Equal("ASDEPLOY112", error.Diagnostic.Code);
    }

    [Fact]
    public void MigrationJobRequiresVersionOneConnectivity()
    {
        Assert.Equal("ASDEPLOY128", Assert.Throws<DeploymentValidationException>(() => CreateJob(requirePrivateNetwork: false)).Diagnostic.Code);
        Assert.Equal("ASDEPLOY128", Assert.Throws<DeploymentValidationException>(() => CreateJob(requireUnixSocket: false)).Diagnostic.Code);
    }

    [Fact]
    public void MigrationJobRejectsNullAggregateInputsBeforeDereferencingThem()
    {
        var valid = CreateJob();
        Assert.Equal("execution", Assert.Throws<ArgumentNullException>(() => new MigrationJobIntent(valid.Id, valid.Phase, valid.Image, valid.Command, valid.Arguments, null!, valid.ConnectionSecret, valid.Database, valid.ServiceIdentity)).ParamName);
        Assert.Equal("connectionSecret", Assert.Throws<ArgumentNullException>(() => new MigrationJobIntent(valid.Id, valid.Phase, valid.Image, valid.Command, valid.Arguments, valid.Execution, null!, valid.Database, valid.ServiceIdentity)).ParamName);
        Assert.Equal("database", Assert.Throws<ArgumentNullException>(() => new MigrationJobIntent(valid.Id, valid.Phase, valid.Image, valid.Command, valid.Arguments, valid.Execution, valid.ConnectionSecret, null!, valid.ServiceIdentity)).ParamName);
    }

    [Fact]
    public void MigrationJobRejectsUninitializedValueObjects()
    {
        var valid = CreateJob();
        Assert.Contains("ASDEPLOY101", Assert.Throws<ArgumentException>(() => new MigrationJobIntent(default, valid.Phase, valid.Image, valid.Command, valid.Arguments, valid.Execution, valid.ConnectionSecret, valid.Database, valid.ServiceIdentity)).Message, StringComparison.Ordinal);
        Assert.Contains("ASDEPLOY102", Assert.Throws<ArgumentException>(() => new MigrationJobIntent(valid.Id, valid.Phase, default, valid.Command, valid.Arguments, valid.Execution, valid.ConnectionSecret, valid.Database, valid.ServiceIdentity)).Message, StringComparison.Ordinal);
        Assert.Contains("ASDEPLOY101", Assert.Throws<ArgumentException>(() => new MigrationJobIntent(valid.Id, valid.Phase, valid.Image, valid.Command, valid.Arguments, valid.Execution, valid.ConnectionSecret, valid.Database, default)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationJobRejectsNullArgument()
    {
        var error = Assert.Throws<ArgumentException>(() => CreateJob(arguments: ["first", null!]));
        Assert.Contains("ASDEPLOY113", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationJobRejectsBlankEnvironmentName()
    {
        var error = Assert.Throws<ArgumentException>(() => CreateJob(environment: new Dictionary<string, string> { [" "] = "value" }));
        Assert.Contains("ASDEPLOY114", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationJobRejectsNullEnvironmentValue()
    {
        var error = Assert.Throws<ArgumentException>(() => CreateJob(environment: new Dictionary<string, string> { ["SETTING"] = null! }));
        Assert.Contains("ASDEPLOY114", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DATABASE_PASSWORD")]
    [InlineData("apiSecretReference")]
    public void MigrationJobRejectsSecretShapedEnvironment(string name)
    {
        var error = Assert.Throws<DeploymentValidationException>(() => CreateJob(environment: new Dictionary<string, string> { [name] = "redacted" }));
        Assert.Equal("ASDEPLOY115", error.Diagnostic.Code);
        Assert.DoesNotContain("redacted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationJobRejectsPlaintextCollisionWithConnectionSecret()
    {
        var error = Assert.Throws<DeploymentValidationException>(() => CreateJob(environment: new Dictionary<string, string> { ["ConnectionStrings__app"] = "do-not-expose" }));
        Assert.Equal("ASDEPLOY129", error.Diagnostic.Code);
        Assert.DoesNotContain("do-not-expose", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationJobDefensivelyCopiesArgumentsAndEnvironmentAndSortsEnvironment()
    {
        var arguments = new[] { "one" };
        var environment = new Dictionary<string, string> { ["ZED"] = "z", ["ALPHA"] = "a" };
        var job = CreateJob(arguments: arguments, environment: environment);
        arguments[0] = "changed";
        environment["ALPHA"] = "changed";
        Assert.Equal(["one"], job.Arguments);
        Assert.Equal(["ALPHA", "ZED"], job.Environment.Keys);
        Assert.Equal("a", job.Environment["ALPHA"]);
        Assert.Equal(
            [DeploymentCapability.PrivateNetwork, DeploymentCapability.RelationalConnection, DeploymentCapability.RunToCompletionJob],
            job.RequiredCapabilities);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DeploymentIntentRejectsBlankEnvironment(string environment)
    {
        Assert.Contains("ASDEPLOY116", Assert.Throws<ArgumentException>(() => new DeploymentIntent(environment, Revision(), [CreateJob()])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentIntentRejectsNullJobs()
    {
        Assert.Throws<ArgumentNullException>(() => new DeploymentIntent("Staging", Revision(), null!));
    }

    [Fact]
    public void DeploymentIntentRejectsUninitializedSourceRevision()
    {
        Assert.Contains("ASDEPLOY103", Assert.Throws<ArgumentException>(() => new DeploymentIntent("Staging", default, [CreateJob()])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentIntentRejectsDuplicateLogicalIds()
    {
        var error = Assert.Throws<DeploymentValidationException>(() => new DeploymentIntent("Staging", Revision(), [CreateJob(), CreateJob()]));
        Assert.Equal("ASDEPLOY117", error.Diagnostic.Code);
        Assert.Contains("migration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentIntentRejectsEmptyJobSet()
    {
        var error = Assert.Throws<DeploymentValidationException>(() => new DeploymentIntent("Staging", Revision(), []));
        Assert.Equal("ASDEPLOY118", error.Diagnostic.Code);
    }

    [Fact]
    public void DeploymentIntentSortsAndCopiesJobs()
    {
        var jobs = new[] { CreateJob("z-job"), CreateJob("a-job") };
        var intent = new DeploymentIntent("Staging", Revision(), jobs);
        jobs[0] = CreateJob("replacement");
        Assert.Equal(["a-job", "z-job"], intent.MigrationJobs.Select(job => job.Id.Value));
        Assert.Equal("1.0", intent.SchemaVersion);
        Assert.EndsWith("deployment-intent.v1.json", intent.Schema, StringComparison.Ordinal);
    }

    internal static MigrationJobIntent CreateJob(
        string id = "migration",
        DeploymentPhase phase = DeploymentPhase.CandidatePreparation,
        string command = "dotnet",
        IEnumerable<string>? arguments = null,
        DeploymentLogicalId? databaseSecret = null,
        bool migrationOwner = true,
        bool requireUnixSocket = true,
        bool requirePrivateNetwork = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var secret = new SecretBinding(Id("connection"), Id("connection-parameter"), "ConnectionStrings__app");
        var database = new DatabaseBinding(Id("database"), "ConnectionStrings__app", databaseSecret ?? secret.Id, requireUnixSocket, migrationOwner);
        return new MigrationJobIntent(
            Id(id),
            phase,
            new ImmutableImageReference($"registry.example/project/migrations@sha256:{new string('a', 64)}"),
            command,
            arguments ?? ["/app/migrations.dll"],
            new DeploymentExecutionPolicy(1, 1, 0, TimeSpan.FromMinutes(10)),
            secret,
            database,
            Id("runtime-identity"),
            requirePrivateNetwork,
            environment);
    }

    internal static DeploymentLogicalId Id(string value) => new(value);

    internal static SourceRevision Revision() => new(new string('b', 40));
}
