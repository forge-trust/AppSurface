using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

const string packageVersion = "0.2.0-preview.8";
const string packageSha256 = "62a48f6b7ec299ad608f3714a49a39f18c5cb1fe45293d53915e5549422d311e";
const string repositoryCommit = "b34970c87489a63b132531657c043e075b092e5e";
const string workerId = "compatibility-v020-preview8-worker";

if (args.Length != 3
    || !Guid.TryParse(args[0], out var runtimeEpoch)
    || !Guid.TryParse(args[1], out var storeId)
    || string.IsNullOrWhiteSpace(args[2]))
{
    throw new ArgumentException("Expected runtime epoch, store id, and exact runtime role arguments.");
}

var runtimeRole = args[2];
var dispatcherConnectionString = RequireEnvironment("APPSURFACE_POSTGRES_DISPATCHER_CONNECTION");
var runtimeConnectionString = RequireEnvironment("APPSURFACE_POSTGRES_RUNTIME_CONNECTION");
var packagePath = RequireEnvironment("APPSURFACE_DURABLE_V020_PACKAGE_PATH");
VerifyPackageArtifact(packagePath, packageVersion, packageSha256, repositoryCommit);

var providerAssembly = typeof(PostgreSqlDurableRuntimeSchemaManager).Assembly;
var informationalVersion = providerAssembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion;
if (informationalVersion is null
    || !informationalVersion.StartsWith(packageVersion, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"Loaded PostgreSQL provider '{informationalVersion ?? "<missing>"}' is not {packageVersion}.");
}

await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnectionString);
await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
var registration = new V2WorkRegistration();
var services = new ServiceCollection();
services.AddSingleton<DurableWorkRegistration>(registration);
services.AddAppSurfaceDurablePostgreSql(
    dispatcherDataSource,
    runtimeDataSource,
    new PostgreSqlDurableWorkOptions(runtimeEpoch, storeId),
    new PostgreSqlDurableScheduleOptions(runtimeRole),
    options =>
    {
        options.WorkerId = workerId;
        options.HostedSurfaces = DurableRuntimeSurface.Work;
        options.SendWakeNotifications = false;
        options.IdlePollingInterval = TimeSpan.FromMilliseconds(100);
        options.HeartbeatStaleAfter = TimeSpan.FromSeconds(2);
    });

await using var provider = services.BuildServiceProvider();
var schema = provider.GetRequiredService<IDurableRuntimeSchemaManager>();
var schemaStatus = await schema.GetStatusAsync();
Require(
    schemaStatus.Compatibility == DurableRuntimeSchemaCompatibility.Compatible
        && schemaStatus.InstalledVersion == 10
        && schemaStatus.RequiredVersion == 9,
    $"Expected old package schema 9 to be compatible with installed schema 10; observed " +
    $"{schemaStatus.Compatibility} ({schemaStatus.RequiredVersion}/{schemaStatus.InstalledVersion}).");
await schema.ValidateAsync();

var health = provider.GetRequiredService<IDurableRuntimeHealth>();
var before = await health.GetAsync();
Require(before.SchemaCompatible, "Legacy health did not report schema compatibility.");
Require(before.EpochCompatible, "Legacy health did not report epoch compatibility.");
Require(before.State == DurableRuntimeHealthState.NotStarted, $"Expected NotStarted health, observed {before.State}.");
Require(before.LastHeartbeatAtUtc is null, "A never-started old worker unexpectedly had a heartbeat.");

var scope = new DurableScopeId("v020-preview8-release-proof");
var acceptedResult = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
    scope,
    new DurableCommandId("v020-preview8-command"),
    "v020-preview8-idempotency",
    registration.WorkName,
    registration.WorkVersion,
    registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("release-proof")),
    registration.ProviderSafety));
var accepted = acceptedResult.Value
    ?? throw new InvalidOperationException(acceptedResult.Problem?.Problem ?? "Old-package Work acceptance failed.");

var pump = provider.GetRequiredService<IDurableRuntimePump>();
var firstPass = await pump.RunOnceAsync(new DurableRuntimePumpRequest(
    maximumItems: 1,
    timeBudget: TimeSpan.FromSeconds(5),
    surfaces: DurableRuntimeSurface.Work));
Require(
    firstPass.Discovered == 1
        && firstPass.Claimed == 1
        && firstPass.Processed == 1
        && firstPass.Deferred == 0
        && firstPass.Failed == 0,
    $"Old-package bounded Work pass returned unexpected counts: {JsonSerializer.Serialize(firstPass)}");
Require(registration.InvocationCount == 1, "The old package did not invoke the registered Work implementation exactly once.");

var terminal = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
    new DurableWorkGetRequest(scope, accepted.WorkId));
Require(terminal.Value?.State == DurableWorkState.Succeeded, "The old-package Work pass did not persist success.");

var afterFirstPass = await health.GetAsync();
Require(afterFirstPass.State == DurableRuntimeHealthState.Healthy, $"Expected Healthy, observed {afterFirstPass.State}.");
Require(afterFirstPass.LastHeartbeatAtUtc is not null, "The old package did not persist a runtime heartbeat.");
Require(afterFirstPass.LastSuccessfulSweepAtUtc is not null, "The old package did not persist a successful sweep.");
Require(afterFirstPass.WorkerInstanceId is not null, "The old package did not persist its worker instance.");

await Task.Delay(TimeSpan.FromMilliseconds(25));
var maintenancePass = await pump.RunOnceAsync(new DurableRuntimePumpRequest(
    maximumItems: 1,
    timeBudget: TimeSpan.FromSeconds(5),
    surfaces: DurableRuntimeSurface.Work));
Require(
    maintenancePass.Discovered == 0
        && maintenancePass.Claimed == 0
        && maintenancePass.Processed == 0
        && maintenancePass.Failed == 0,
    $"Old-package heartbeat maintenance pass returned unexpected counts: {JsonSerializer.Serialize(maintenancePass)}");
var afterMaintenance = await health.GetAsync();
Require(afterMaintenance.State == DurableRuntimeHealthState.Healthy, "Legacy health was not healthy after maintenance.");
Require(
    afterMaintenance.WorkerInstanceId == afterFirstPass.WorkerInstanceId,
    "Heartbeat maintenance changed the old worker instance.");
Require(
    afterMaintenance.LastHeartbeatAtUtc > afterFirstPass.LastHeartbeatAtUtc,
    "A second bounded pass did not advance the old worker heartbeat.");
Require(
    afterMaintenance.LastSuccessfulSweepAtUtc > afterFirstPass.LastSuccessfulSweepAtUtc,
    "A second bounded pass did not advance the old worker successful-sweep timestamp.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    Phase = "v0.2.0-preview.8-operational",
    PackageVersion = packageVersion,
    PackageSha256 = packageSha256,
    RepositoryCommit = repositoryCommit,
    ProviderInformationalVersion = informationalVersion,
    Schema = new
    {
        schemaStatus.InstalledVersion,
        schemaStatus.RequiredVersion,
        Compatibility = schemaStatus.Compatibility.ToString(),
    },
    StartupValidated = true,
    LegacyHealthObserved = true,
    InitialHealthState = before.State.ToString(),
    HealthyAfterWork = afterFirstPass.State.ToString(),
    HeartbeatMaintained = true,
    Work = new
    {
        ScopeId = scope.Value,
        WorkId = accepted.WorkId.Value,
        State = terminal.Value!.State.ToString(),
        registration.InvocationCount,
        firstPass.Discovered,
        firstPass.Claimed,
        firstPass.Processed,
        firstPass.Deferred,
        firstPass.Failed,
    },
}));

static string RequireEnvironment(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{name} is required.")
        : value;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void VerifyPackageArtifact(
    string packagePath,
    string expectedVersion,
    string expectedSha256,
    string expectedRepositoryCommit)
{
    if (!File.Exists(packagePath))
    {
        throw new FileNotFoundException("The exact old-package artifact is unavailable.", packagePath);
    }

    using (var stream = File.OpenRead(packagePath))
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(actualHash, expectedSha256))
        {
            throw new InvalidDataException(
                $"The old-package artifact SHA-256 '{actualHash}' does not match '{expectedSha256}'.");
        }
    }

    using var archive = ZipFile.OpenRead(packagePath);
    var nuspec = archive.Entries.Single(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    using var nuspecStream = nuspec.Open();
    var document = XDocument.Load(nuspecStream);
    var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata")
        ?? throw new InvalidDataException("The old-package artifact has no NuGet metadata.");
    var id = metadata.Elements().Single(element => element.Name.LocalName == "id").Value;
    var version = metadata.Elements().Single(element => element.Name.LocalName == "version").Value;
    var repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
    var commit = repository.Attribute("commit")?.Value;
    if (!StringComparer.Ordinal.Equals(id, "ForgeTrust.AppSurface.Durable.PostgreSql")
        || !StringComparer.Ordinal.Equals(version, expectedVersion)
        || !StringComparer.Ordinal.Equals(commit, expectedRepositoryCommit))
    {
        throw new InvalidDataException(
            $"Unexpected old-package identity '{id}' '{version}' '{commit ?? "<missing>"}'.");
    }
}

internal sealed class V2WorkRegistration() : DurableWorkRegistration(
    "compatibility.v020-preview8-work",
    "v1",
    DurableProviderSafety.Idempotent,
    new V2Codec("compatibility.v020-preview8-work"),
    new V2Codec("compatibility.v020-preview8-result"))
{
    private int _invocationCount;

    internal int InvocationCount => Volatile.Read(ref _invocationCount);

    internal IDurablePayloadCodec InputCodec => WorkCodec;

    public override bool CanReconcile => false;

    public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
    {
        _ = WorkCodec.DecodeObject(work.Payload);
        return new V2PreparedWork(
            ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("v020-preview8-result")),
            () => Interlocked.Increment(ref _invocationCount));
    }

    public override ValueTask<DurableEncodedPayload> InvokeAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        Prepare(services, work).InvokeAsync(cancellationToken);

    public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
        IServiceProvider services,
        DurableWorkExecutionContext work,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Idempotent compatibility Work does not reconcile.");
}

internal sealed class V2PreparedWork(
    DurableEncodedPayload result,
    Action onInvoke) : DurablePreparedWork
{
    public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        onInvoke();
        return ValueTask.FromResult(result);
    }
}

internal sealed class V2Codec(string contractName) : IDurablePayloadCodec
{
    public Type PayloadType => typeof(byte[]);
    public string ContractName { get; } = contractName;
    public string ContractVersion => "v1";
    public DurableDataClassification Classification => DurableDataClassification.Operational;
    public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

    public DurableEncodedPayload EncodeObject(object value) => new(
        ContractName,
        ContractVersion,
        Classification,
        (byte[])value,
        RetentionPolicyId);

    public object DecodeObject(DurableEncodedPayload payload)
    {
        if (!StringComparer.Ordinal.Equals(payload.ContractName, ContractName)
            || !StringComparer.Ordinal.Equals(payload.ContractVersion, ContractVersion)
            || payload.Classification != Classification
            || !StringComparer.Ordinal.Equals(payload.RetentionPolicyId, RetentionPolicyId))
        {
            throw new InvalidDataException("The compatibility Work payload does not match its registered contract.");
        }

        return payload.Content.ToArray();
    }
}
