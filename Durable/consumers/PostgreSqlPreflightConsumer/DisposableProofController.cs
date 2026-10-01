using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;

internal static class DisposableProofController
{
    private const long RecipeLock = 4_707_181_168_775_217_740;
    private const string Image = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877";
    private const string AdminPassword = "issue845-disposable-admin-only";
    private const string RetentionRole = "appsurface_durable_retention";

    internal static async Task RunAsync(ConsumerOptions options, JsonElement artifactRoot, string packageVersion,
        string bundleManifestSha256, string cliSha256, string providerSha256, string recipeSha256,
        string fixtureHash, string manifestHash, string owner, Guid storeId, Guid epoch,
        IReadOnlyList<RolePair> pairs, string cliPath)
    {
        var total = Stopwatch.StartNew();
        var scratch = Path.Combine(Path.GetTempPath(), "issue845-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        PostgreSqlContainer? fixtureHost = null;
        var fixtureHostDisposed = false;
        try
        {
            var manifestBytes = await File.ReadAllBytesAsync(options.RolePairsPath);
            var recipeBytes = await File.ReadAllBytesAsync(options.RecipePath);
            var fixtureBytes = await File.ReadAllBytesAsync(options.Schema10FixturePath);
            if (Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != manifestHash
                || Convert.ToHexStringLower(SHA256.HashData(recipeBytes)) != recipeSha256
                || Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)) != fixtureHash)
                throw new InvalidOperationException("A proof input changed after its identity hash was calculated.");
            var frozenManifestPath = Path.Combine(scratch, "complete-role-pairs.json");
            var frozenRecipePath = Path.Combine(scratch, "configure-postgresql-roles.sql");
            var frozenFixturePath = Path.Combine(scratch, "schema10-two-pair.sql");
            await File.WriteAllBytesAsync(frozenManifestPath, manifestBytes);
            await File.WriteAllBytesAsync(frozenRecipePath, recipeBytes);
            await File.WriteAllBytesAsync(frozenFixturePath, fixtureBytes);
            options = options with { RolePairsPath = frozenManifestPath, RecipePath = frozenRecipePath, Schema10FixturePath = frozenFixturePath };

            var onePairFile = Path.Combine(scratch, "one-pair.json");
            var oneBytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, pairs = new[] { new { dispatcher = pairs[0].Dispatcher, runtime = pairs[0].Runtime, dispatcher_profile = "full" } } });
            await File.WriteAllBytesAsync(onePairFile, oneBytes);
            var onePairHash = Convert.ToHexStringLower(SHA256.HashData(oneBytes));
            var pg = new PostgreSqlBuilder(Image).WithDatabase("postgres").WithUsername("postgres")
                .WithPassword(AdminPassword).WithResourceMapping(options.RecipePath, "/tmp")
                .WithResourceMapping(options.Schema10FixturePath, "/tmp").Build();
            fixtureHost = pg;
            using var startDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await pg.StartAsync(startDeadline.Token).WaitAsync(startDeadline.Token);
            var admin = pg.GetConnectionString();
            var ownerPassword = RandomPassword();
            var runtimePasswords = pairs.Select(_ => RandomPassword()).ToArray();
            var dispatchPasswords = pairs.Select(_ => RandomPassword()).ToArray();
            var disposableStoreId = storeId == Guid.Empty ? Guid.NewGuid() : storeId;
            var disposableEpoch = epoch == Guid.Empty ? Guid.NewGuid() : epoch;
            await CreateRolesAsync(admin, owner, ownerPassword, RetentionRole, RandomPassword(), pairs, runtimePasswords, dispatchPasswords);
            await CreateDatabaseAsync(admin, "preflight_one_pair", owner);
            await CreateDatabaseAsync(admin, "preflight_schema10", owner);

            var oneDbOwner = Connection(admin, "preflight_one_pair", owner, ownerPassword, false);
            var upgradeDbOwner = Connection(admin, "preflight_schema10", owner, ownerPassword, false);
            var ownerDiagnostic = Connection(admin, "preflight_one_pair", owner, ownerPassword, false);
            var onePairServices = BuildPairConnections(admin, "preflight_one_pair", pairs, runtimePasswords, dispatchPasswords);
            var allResults = new List<PreflightReceipt>();
            var scenarioResults = new List<ScenarioProof>();
            var durations = new Dictionary<string, double>(StringComparer.Ordinal);
            var timer = Stopwatch.StartNew();
            await ApplyWithExactCliAsync(cliPath, oneDbOwner);
            await SetStoreIdAsync(oneDbOwner, disposableStoreId);
            await InitializeEpochAsync(oneDbOwner, disposableEpoch);
            await ApplyPackagedRecipeAsync(pg, "preflight_one_pair", owner, onePairFile);
            durations["one_pair_bootstrap_ms"] = timer.Elapsed.TotalMilliseconds;
            scenarioResults.Add(await GuardedScenarioAsync("one-pair", admin, oneDbOwner, ownerDiagnostic, onePairServices,
                cliPath, options with { RolePairsPath = onePairFile }, onePairHash, owner, disposableStoreId, disposableEpoch,
                1, true, true, BuildLanePairs(pairs, dispatchPasswords, runtimePasswords).Take(1).ToArray(), allResults));

            timer.Restart();
            await ApplyPackagedRecipeAsync(pg, "preflight_one_pair", owner, options.RolePairsPath);
            durations["pair_enrollment_recipe_ms"] = timer.Elapsed.TotalMilliseconds;
            scenarioResults.Add(await GuardedScenarioAsync("pair-enrollment", admin, oneDbOwner, ownerDiagnostic, onePairServices,
                cliPath, options, manifestHash, owner, disposableStoreId, disposableEpoch, pairs.Count, true,
                true, BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), allResults));

            timer.Restart();
            var before = await ReadCatalogSnapshotAsync(oneDbOwner);
            await ApplyPackagedRecipeAsync(pg, "preflight_one_pair", owner, options.RolePairsPath);
            var after = await ReadCatalogSnapshotAsync(oneDbOwner);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                throw new InvalidOperationException("Identical packaged role-recipe rerun changed the catalog snapshot.");
            durations["identical_rerun_ms"] = timer.Elapsed.TotalMilliseconds;
            scenarioResults.Add(await GuardedScenarioAsync("identical-rerun-stable-catalog", admin, oneDbOwner, ownerDiagnostic,
                onePairServices, cliPath, options, manifestHash, owner, disposableStoreId, disposableEpoch, pairs.Count, true,
                true, BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), allResults));

            timer.Restart();
            var migrationEvidence = await ApplySchemaTenBaselineAsync(upgradeDbOwner);
            await SetStoreIdAsync(upgradeDbOwner, disposableStoreId);
            await ApplySchema10FixtureAsync(pg, "preflight_schema10", owner, pairs);
            var schema10Snapshot = await ReadCatalogSnapshotAsync(upgradeDbOwner);
            await AssertSchemaTenAsync(upgradeDbOwner);
            var upgradeOwner = Connection(admin, "preflight_schema10", owner, ownerPassword, false);
            var upgradePairs = BuildPairConnections(admin, "preflight_schema10", pairs, runtimePasswords, dispatchPasswords);
            var schema10LanePairs = BuildLanePairs(pairs, dispatchPasswords, runtimePasswords);
            await SetActiveEpochDirectAsync(upgradeDbOwner, disposableEpoch);
            var schema10LaneProof = await RunSchema10BoundaryPreUpgradeProofAsync(upgradeDbOwner,
                schema10LanePairs, disposableEpoch, disposableStoreId);
            await AssertSchemaTenAsync(upgradeDbOwner);
            await ApplyWithExactCliAsync(cliPath, upgradeDbOwner);
            await ApplyPackagedRecipeAsync(pg, "preflight_schema10", owner, options.RolePairsPath);
            // The modeled schema-10 lane already initialized this epoch. A forward
            // migration and role reconciliation must preserve it, not initialize it again.
            var upgradedIdentity = await ReadIdentityAsync(upgradeDbOwner);
            if (upgradedIdentity.Store != disposableStoreId || upgradedIdentity.Epoch != disposableEpoch)
                throw new InvalidOperationException("The schema-10 upgrade changed the established StoreId or runtime epoch.");
            durations["schema10_to_11_upgrade_ms"] = timer.Elapsed.TotalMilliseconds;
            scenarioResults.Add(await GuardedScenarioAsync("schema10-to-11-two-pair", admin, upgradeDbOwner, upgradeOwner,
                upgradePairs, cliPath, options, manifestHash, owner, disposableStoreId, disposableEpoch, pairs.Count, true,
                true, BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), allResults, schema10Snapshot));

            timer.Restart();
            var writerResult = await QueuedWriterAndFreshRerunAsync(admin, oneDbOwner, ownerDiagnostic, onePairServices, cliPath,
                options, manifestHash, owner, disposableStoreId, disposableEpoch, pairs.Count,
                BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), allResults);
            durations["queued_writer_fresh_rerun_ms"] = timer.Elapsed.TotalMilliseconds;

            // Guard-loss cancellation may leave valid pending lane facts. Keep this negative
            // fixture in its own database so those facts cannot select a later positive pass's
            // candidate; exercise the same exact packages, role boundary, and identity.
            timer.Restart();
            await CreateDatabaseAsync(admin, "preflight_guard_loss", owner);
            var guardLossOwner = Connection(admin, "preflight_guard_loss", owner, ownerPassword, false);
            await ApplyWithExactCliAsync(cliPath, guardLossOwner);
            await SetStoreIdAsync(guardLossOwner, disposableStoreId);
            await InitializeEpochAsync(guardLossOwner, disposableEpoch);
            await ApplyPackagedRecipeAsync(pg, "preflight_guard_loss", owner, options.RolePairsPath);
            var guardLossEvidence = await VerifyGuardLossPathsAsync(admin, guardLossOwner,
                BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), disposableStoreId, disposableEpoch,
                options.ReceiptPath);
            durations["guard_loss_negative_proof_ms"] = timer.Elapsed.TotalMilliseconds;

            // Keep the observer-failure regression independent of earlier cancelled lane
            // facts. It runs the same public provider lanes and exact candidate recipe.
            timer.Restart();
            await CreateDatabaseAsync(admin, "preflight_observer_failure", owner);
            var observerFailureOwner = Connection(admin, "preflight_observer_failure", owner, ownerPassword, false);
            await ApplyWithExactCliAsync(cliPath, observerFailureOwner);
            await SetStoreIdAsync(observerFailureOwner, disposableStoreId);
            await InitializeEpochAsync(observerFailureOwner, disposableEpoch);
            await ApplyPackagedRecipeAsync(pg, "preflight_observer_failure", owner, options.RolePairsPath);
            await VerifyLaneObservationFailureCleanupAsync(admin, observerFailureOwner,
                BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), disposableStoreId, disposableEpoch,
                options.ReceiptPath);
            durations["lane_observer_failure_cleanup_ms"] = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            await CreateDatabaseAsync(admin, "preflight_callback_failure", owner);
            var callbackFailureOwner = Connection(admin, "preflight_callback_failure", owner, ownerPassword, false);
            await ApplyWithExactCliAsync(cliPath, callbackFailureOwner);
            await SetStoreIdAsync(callbackFailureOwner, disposableStoreId);
            await InitializeEpochAsync(callbackFailureOwner, disposableEpoch);
            await ApplyPackagedRecipeAsync(pg, "preflight_callback_failure", owner, options.RolePairsPath);
            await VerifyLaneObservationFailureCleanupAsync(admin, callbackFailureOwner,
                BuildLanePairs(pairs, dispatchPasswords, runtimePasswords), disposableStoreId, disposableEpoch,
                options.ReceiptPath, throwingCancellationCallback: true);
            durations["lane_callback_failure_cleanup_ms"] = timer.Elapsed.TotalMilliseconds;

            if (scenarioResults.Count != 4 || allResults.Count != 14
                || allResults.Count(r => r.Caller == "runtime") != 9
                || allResults.Count(r => r.Caller == "owner-diagnostic") != 5)
                throw new InvalidOperationException("The complete four-scenario plus writer-rerun CLI evidence set is incomplete.");
            if (allResults.Where(r => r.Caller == "runtime").Select(r => r.Role).Distinct(StringComparer.Ordinal).Count() != pairs.Count)
                throw new InvalidOperationException("Runtime CLI evidence contains duplicated or missing manifest roles.");

            var receipt = new
            {
                schemaVersion = 1,
                proofKind = "issue-845-exact-package-disposable-postgresql-consumer",
                sourceCommit = options.SourceCommit,
                runId = options.RunId,
                artifactId = options.ArtifactId,
                packageVersion,
                artifactManifestSha256 = bundleManifestSha256,
                packages = artifactRoot.GetProperty("packages").EnumerateObject().Select(p => new
                {
                    packageId = p.Value.GetProperty("packageId").GetString(),
                    version = p.Value.GetProperty("version").GetString(),
                    sha256 = p.Value.GetProperty("sha256").GetString()
                }).ToArray(),
                exactCliPackageSha256 = cliSha256,
                exactProviderPackageSha256 = providerSha256,
                extractedRoleRecipeSha256 = recipeSha256,
                schema10FixtureSha256 = fixtureHash,
                completeRoleManifestSha256 = manifestHash,
                onePairManifestSha256 = onePairHash,
                migrationOwnerRole = owner,
                storeId = disposableStoreId,
                activeEpoch = disposableEpoch,
                postgresImage = Image,
                scenarios = scenarioResults.Select(item => new
                {
                    name = item.Scenario,
                    ownerGuard = "continuous-shared-session-lock",
                    fixtureActivationCallbackCompleted = item.ActivationCompleted,
                    runtimePreflightCount = item.RuntimePreflightCount,
                    guardBackendPid = item.GuardBackendPid,
                    ownerDiagnostic = "separate-and-never-counted",
                    storeId = item.StoreId.ToString("D"),
                    activeEpoch = item.ActiveEpoch.ToString("D"),
                    laneEvidence = item.LaneEvidence,
                    laneEvidenceBefore = FormatLaneEvidence(item.Scenario, item.GuardBackendPid, disposableStoreId,
                        disposableEpoch, pairs[0].Runtime,
                        item.LaneBefore.SourceWorkOnlyDenialsVerified ? pairs[1].Runtime : null, item.LaneBefore),
                    laneEvidenceAfter = FormatLaneEvidence(item.Scenario, item.GuardBackendPid, disposableStoreId,
                        disposableEpoch, pairs[0].Runtime,
                        item.LaneAfter.SourceWorkOnlyDenialsVerified ? pairs[1].Runtime : null, item.LaneAfter),
                    modeledSchema10BaselinePresent = item.ModeledSchema10BaselinePresent
                }).ToArray(),
                cliResults = allResults.Select(result => new
                {
                    Scenario = result.Scenario,
                    Caller = result.Caller,
                    Pair = result.Pair,
                    Role = result.Role,
                    Owner = result.Owner,
                    RuntimeCount = result.RuntimeCount,
                    ManifestSha256 = result.ManifestSha256,
                    StoreId = result.StoreId.ToString("D"),
                    ActiveEpoch = result.ActiveEpoch!.Value.ToString("D"),
                    ElapsedMilliseconds = result.ElapsedMilliseconds
                }).ToArray(),
                migrationChecksums = migrationEvidence.Select(item => new { Version = item.Version, Name = item.Name, sha256 = item.Sha }).ToArray(),
                schema10BaselineSnapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema10Snapshot))),
                schema10PreUpgradeLaneProof = new
                {
                    passed = schema10LaneProof.Passed,
                    proofMode = "schema10-low-level-role-boundary-and-durable-dispatch",
                    runtimeEpoch = disposableEpoch,
                    fullPair = pairs[0].Dispatcher,
                    workOnlyPair = pairs[1].Dispatcher,
                    schema10LaneProof.Work,
                    schema10LaneProof.Flow,
                    schema10LaneProof.Schedule,
                    sourceDenials = schema10LaneProof.SourceDenials
                },
                catalogSnapshotSha256BeforeAndAfterIdenticalRerun = new[]
                {
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(before))),
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(after)))
                },
                queuedWriter = writerResult,
                guardLossNegativeProof = guardLossEvidence,
                laneEvidence = new
                {
                    before = scenarioResults.ToDictionary(item => item.Scenario, item => new { item.LaneBefore.SucceededWorkDelta, item.LaneBefore.CompletedFlowDelta, item.LaneBefore.ActiveScheduleDelta, item.LaneBefore.MaterializedScheduleOccurrenceDelta }, StringComparer.Ordinal),
                    after = scenarioResults.ToDictionary(item => item.Scenario, item => new { item.LaneAfter.SucceededWorkDelta, item.LaneAfter.CompletedFlowDelta, item.LaneAfter.ActiveScheduleDelta, item.LaneAfter.MaterializedScheduleOccurrenceDelta }, StringComparer.Ordinal)
                },
                guardWindow = new { intact = scenarioResults.All(item => item.GuardIntact), backendPids = scenarioResults.Select(item => item.GuardBackendPid).ToArray() },
                activationCallbackCompleted = scenarioResults.All(item => item.ActivationCompleted),
                stageDurationsMilliseconds = durations,
                totalDurationMilliseconds = total.Elapsed.TotalMilliseconds
            };
            var output = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true });
            await pg.DisposeAsync();
            fixtureHostDisposed = true;
            Directory.Delete(scratch, recursive: true);
            if (Directory.Exists(scratch))
                throw new IOException("Consumer scratch directory remained after checked cleanup.");
            await PublishReceiptAtomicallyAsync(options.ReceiptPath, output);
            Console.WriteLine("issue-845-proof=passed");
        }
        finally
        {
            if (fixtureHost is not null && !fixtureHostDisposed)
            {
                await fixtureHost.DisposeAsync();
            }
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
                if (Directory.Exists(scratch))
                    throw new IOException("Consumer scratch directory remained after failed proof cleanup.");
            }
        }
    }

    private static async Task<object> VerifyGuardLossPathsAsync(string adminCs, string ownerCs,
        LaneProofRolePair[] pairs, Guid storeId, Guid epoch, string receiptPath)
    {
        if (File.Exists(receiptPath))
            throw new InvalidOperationException("A consumer receipt existed before guard-loss negative checks.");
        var laneLoss = await VerifyLaneGuardLossAsync(adminCs, ownerCs, pairs, storeId, epoch, receiptPath);
        var activationLoss = await VerifyActivationGuardLossAsync(adminCs, ownerCs, pairs[0], storeId, epoch, receiptPath);
        var finalReceiptAbsentBeforeAndAfter = !File.Exists(receiptPath) && !Directory.Exists(receiptPath);
        if (!finalReceiptAbsentBeforeAndAfter)
            throw new InvalidOperationException("A failed guard-loss scenario published a consumer receipt.");
        return new
        {
            lanePassBackendPid = laneLoss.GuardBackendPid,
            lanePassExecutingBackendPid = laneLoss.ExecutingBackendPid,
            lanePassStoreId = laneLoss.StoreId.ToString("D"),
            lanePassActiveEpoch = laneLoss.ActiveEpoch.ToString("D"),
            lanePassStarted = laneLoss.ChildPassStarted,
            lanePassObservedExecuting = laneLoss.ChildPassObservedExecuting,
            lanePassBackendTerminated = laneLoss.BackendTerminated,
            lanePassCancellationObserved = laneLoss.CancellationObserved,
            lanePassChildDrained = laneLoss.ChildDrained,
            lanePassReceiptWithheld = laneLoss.FailedChildReceiptWithheld && finalReceiptAbsentBeforeAndAfter,
            activationBackendPid = activationLoss.GuardBackendPid,
            activationStoreId = activationLoss.StoreId.ToString("D"),
            activationActiveEpoch = activationLoss.ActiveEpoch.ToString("D"),
            activationWorkInvocationStartedBeforeCompletion = activationLoss.WorkInvocationStartedBeforeCompletion,
            activationDrainVerifiedCheckpointObserved = activationLoss.DrainVerifiedCheckpointObserved,
            activationHostedServicesStopped = activationLoss.HostedServicesStopped,
            activationChildDrained = activationLoss.ChildDrained,
            activationIdentityUnchanged = activationLoss.IdentityUnchanged,
            activationBackendTerminated = activationLoss.BackendTerminated,
            activationCancellationObserved = activationLoss.CancellationObserved,
            activationHostDrainPersisted = activationLoss.HostDrainPersisted,
            activationAdmissionClosed = activationLoss.AdmissionClosed,
            activationSessionsReleased = activationLoss.SessionsReleased,
            activationReceiptWithheld = activationLoss.FailedChildReceiptWithheld && finalReceiptAbsentBeforeAndAfter,
            finalReceiptAbsentBeforeAndAfter
        };
    }

    // The optional observer deliberately faults only after a real executing provider
    // lane has been observed. It is an in-assembly failure-cleanup regression seam.
    private static async Task<GuardLossEvidence> VerifyLaneGuardLossAsync(string adminCs, string ownerCs,
        LaneProofRolePair[] pairs, Guid storeId, Guid epoch, string receiptPath,
        Action<int, int, Task, Task, CancellationToken>? executingLaneObserved = null)
    {
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
            throw new InvalidOperationException("A receipt path existed before lane guard-loss injection.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var guard = await OpenNonPooledAsync(ownerCs, deadline.Token);
        await AcquireSharedGuardAsync(guard, deadline.Token);
        using var lost = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lost.Token);
        using var gate = new SemaphoreSlim(1, 1);
        var monitor = MonitorOwnerGuardAsync(guard, gate, stop.Token, lost);
        Task<LaneProofEvidence>? pass = null;
        try
        {
            var backendPid = await VerifyGuardAsync(guard, gate, linked.Token);
            pass = RunLaneProofAsync(ObserverConnectionForDatabase(adminCs, ownerCs), ownerCs, pairs, epoch, storeId, linked.Token);
            var activeLaneBackendPid = await WaitForExecutingLaneBackendAsync(adminCs, ownerCs, pairs, pass, deadline.Token);
            executingLaneObserved?.Invoke(backendPid, activeLaneBackendPid, pass, monitor, linked.Token);
            var terminated = await TerminateBackendAsync(adminCs, backendPid);
            if (!terminated)
                throw new InvalidOperationException("Lane guard-loss injection could not terminate its real PostgreSQL backend.");
            try { await pass.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (Exception) when (lost.IsCancellationRequested) { }
            if (!lost.IsCancellationRequested || !pass.IsCompleted)
                throw new InvalidOperationException("Actual lane proof did not cancel and drain after owner guard loss.");
            if (!pass.IsCanceled && !pass.IsFaulted)
                throw new InvalidOperationException("Lane proof unexpectedly completed successfully after guard loss.");
            var receiptWithheld = !File.Exists(receiptPath) && !Directory.Exists(receiptPath);
            if (!receiptWithheld)
                throw new InvalidOperationException("The lane guard-loss child unexpectedly published a receipt.");
            return new GuardLossEvidence(backendPid, activeLaneBackendPid, storeId, epoch,
                ChildPassStarted: true, ChildPassObservedExecuting: activeLaneBackendPid > 0,
                BackendTerminated: terminated, CancellationObserved: lost.IsCancellationRequested,
                ChildDrained: true, FailedChildReceiptWithheld: receiptWithheld);
        }
        finally
        {
            // Initial probing, observation, and termination can fail before guard loss.
            // Cancel and drain both owned tasks before disposing their guard or tokens.
            try { linked.Cancel(); }
            finally
            {
                try { stop.Cancel(); }
                finally
                {
                    try
                    {
                        if (pass is not null)
                            await DrainGuardLossTaskAsync(pass, TimeSpan.FromSeconds(15));
                    }
                    finally
                    {
                        await DrainGuardLossTaskAsync(monitor, TimeSpan.FromSeconds(5));
                    }
                }
            }
        }
    }

    // Completed task faults are expected negative evidence. An uncompleted cleanup
    // timeout remains a failure, so it cannot be reported as successful drainage.
    private static async Task DrainGuardLossTaskAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout); }
        catch (Exception) when (task.IsCompleted && !task.IsCompletedSuccessfully) { }
    }

    /// <summary>
    /// Runs the disposable regression for observation failure after real lane launch.
    /// Requires an initialized, reconciled fixture with no other service-role sessions;
    /// owns an admin table-lock transaction and the negative probe's guard and children.
    /// The lock keeps provider Work execution pending until cancellation, making the
    /// task-drain assertions deterministic. An optional throwing cancellation callback
    /// also verifies that a callback exception cannot skip either drain. Success requires both tasks terminal before
    /// releasing that lock, absent lane/guard sessions and fence, and no receipt.
    /// </summary>
    /// <remarks>
    /// This in-assembly test seam consumes synthetic fixture credentials only. It never
    /// returns activation evidence or reconciles roles. Cleanup timeouts remain failures.
    /// </remarks>
    internal static async Task VerifyLaneObservationFailureCleanupAsync(string adminCs, string ownerCs,
        LaneProofRolePair[] pairs, Guid storeId, Guid epoch, string receiptPath,
        bool throwingCancellationCallback = false)
    {
        var guardPid = 0;
        var executingPid = 0;
        Task? child = null;
        Task? monitor = null;
        using var regressionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var blocker = await OpenNonPooledAsync(ObserverConnectionForDatabase(adminCs, ownerCs), regressionDeadline.Token);
        await using var blockingTransaction = await blocker.BeginTransactionAsync(regressionDeadline.Token);
        await using (var lockWork = new NpgsqlCommand("LOCK TABLE appsurface_durable.work IN SHARE MODE;", blocker, blockingTransaction))
            await lockWork.ExecuteNonQueryAsync(regressionDeadline.Token);
        var injected = new InvalidOperationException("Intentional executing-lane observation failure.");
        var callbackFailure = new InvalidOperationException("Intentional lane cancellation callback failure.");
        CancellationTokenRegistration callbackRegistration = default;
        try
        {
            await VerifyLaneGuardLossAsync(adminCs, ownerCs, pairs, storeId, epoch, receiptPath,
                (guard, executing, laneTask, monitorTask, token) =>
                {
                    guardPid = guard;
                    executingPid = executing;
                    child = laneTask;
                    monitor = monitorTask;
                    if (throwingCancellationCallback)
                        callbackRegistration = token.Register(() => throw callbackFailure);
                    throw injected;
                });
            throw new InvalidOperationException("An executing-lane observer failure unexpectedly returned evidence.");
        }
        catch (InvalidOperationException exception) when (!throwingCancellationCallback && ReferenceEquals(exception, injected)) { }
        catch (AggregateException exception) when (throwingCancellationCallback
            && exception.Flatten().InnerExceptions.Any(inner => ReferenceEquals(inner, callbackFailure))) { }
        finally { callbackRegistration.Dispose(); }

        if (guardPid <= 0 || executingPid <= 0 || guardPid == executingPid
            || child is null || !child.IsCompleted || child.IsCompletedSuccessfully
            || monitor is null || !monitor.IsCompleted)
            throw new InvalidOperationException("The observer failure was not injected during real lane execution.");
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var observer = await OpenNonPooledAsync(adminCs, cleanup.Token);
        var database = new NpgsqlConnectionStringBuilder(ownerCs).Database;
        var roles = pairs.SelectMany(pair => new[] { pair.DispatcherRole, pair.RuntimeRole }).ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity
                               WHERE pid = @guard OR pid = @executing
                                  OR (datname = @database AND usename = ANY(@roles)))
               AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_locks
                               WHERE pid = @guard AND locktype = 'advisory');
            """, observer);
        command.Parameters.AddWithValue("guard", guardPid);
        command.Parameters.AddWithValue("executing", executingPid);
        command.Parameters.AddWithValue("database", database ?? throw new InvalidOperationException("The fixture database identity is missing."));
        command.Parameters.AddWithValue("roles", roles);
        while (await command.ExecuteScalarAsync(cleanup.Token) is not true)
            await Task.Delay(TimeSpan.FromMilliseconds(25), cleanup.Token);
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
            throw new InvalidOperationException("Observer failure cleanup published a consumer receipt.");
    }

    private static async Task<ActivationGuardLossEvidence> VerifyActivationGuardLossAsync(string adminCs,
        string ownerCs, LaneProofRolePair pair, Guid storeId, Guid epoch, string receiptPath)
    {
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
            throw new InvalidOperationException("A receipt path existed before activation guard-loss injection.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var guard = await OpenNonPooledAsync(ownerCs, deadline.Token);
        await AcquireSharedGuardAsync(guard, deadline.Token);
        using var lost = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lost.Token);
        using var gate = new SemaphoreSlim(1, 1);
        var monitor = MonitorOwnerGuardAsync(guard, gate, stop.Token, lost);
        var backendPid = 0;
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainVerified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new FixtureActivationCheckpointHook(async (point, token) =>
        {
            if (point == FixtureActivationCheckpoint.WorkInvocationStartedBeforeCompletion)
                workStarted.TrySetResult();
            if (point == FixtureActivationCheckpoint.DrainVerified)
                drainVerified.TrySetResult();
            if (point == FixtureActivationCheckpoint.WorkInvocationStartedBeforeCompletion)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Task<FixtureActivationLease>? activation = null;
        var terminated = false;
        try
        {
            backendPid = await VerifyGuardAsync(guard, gate, linked.Token);
            activation = FixtureActivationProof.StartAsync(ownerCs, pair, storeId, epoch, linked.Token, hook);
            await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
            terminated = await TerminateBackendAsync(adminCs, backendPid);
            if (!terminated)
                throw new InvalidOperationException("Activation guard-loss injection could not terminate its real PostgreSQL backend.");
            try { await activation.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception) when (lost.IsCancellationRequested) { }
        }
        finally
        {
            try { linked.Cancel(); }
            finally
            {
                try { stop.Cancel(); }
                finally
                {
                    try
                    {
                        if (activation is not null)
                        {
                            await DrainGuardLossTaskAsync(activation, TimeSpan.FromSeconds(15));
                            if (activation.IsCompletedSuccessfully)
                                await activation.Result.DisposeAsync();
                        }
                    }
                    finally
                    {
                        await DrainGuardLossTaskAsync(monitor, TimeSpan.FromSeconds(5));
                    }
                }
            }
        }
        if (activation is null || !lost.IsCancellationRequested || !activation.IsCompleted || (!activation.IsCanceled && !activation.IsFaulted))
            throw new InvalidOperationException("Real fixture activation did not cancel after guard loss during its Work handler.");
        await drainVerified.Task.WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
        var persisted = await ReadGuardLostActivationHeartbeatAsync(ownerCs, epoch);
        var sessionsReleased = await AreFixtureActivationSessionsReleasedAsync(ownerCs);
        var identity = await ReadIdentityAsync(ownerCs, deadline.Token);
        var identityUnchanged = identity.Store == storeId && identity.Epoch == epoch;
        if (!persisted || !sessionsReleased || !identityUnchanged)
            throw new InvalidOperationException("Guard-lost fixture activation did not persist its actual drain and release worker sessions.");
        var receiptWithheld = !File.Exists(receiptPath) && !Directory.Exists(receiptPath);
        if (!receiptWithheld)
            throw new InvalidOperationException("The activation guard-loss child unexpectedly published a receipt.");
        return new ActivationGuardLossEvidence(backendPid, storeId, epoch,
            WorkInvocationStartedBeforeCompletion: workStarted.Task.IsCompletedSuccessfully,
            BackendTerminated: terminated, CancellationObserved: lost.IsCancellationRequested,
            DrainVerifiedCheckpointObserved: drainVerified.Task.IsCompletedSuccessfully,
            HostedServicesStopped: drainVerified.Task.IsCompletedSuccessfully,
            HostDrainPersisted: persisted, AdmissionClosed: drainVerified.Task.IsCompletedSuccessfully,
            SessionsReleased: sessionsReleased, IdentityUnchanged: identityUnchanged,
            ChildDrained: activation.IsCompleted && drainVerified.Task.IsCompletedSuccessfully,
            FailedChildReceiptWithheld: receiptWithheld);
    }

    private static async Task<int> WaitForExecutingLaneBackendAsync(string adminCs, string ownerCs,
        LaneProofRolePair[] pairs, Task<LaneProofEvidence> pass, CancellationToken cancellationToken)
    {
        var database = new NpgsqlConnectionStringBuilder(ownerCs).Database;
        var roles = pairs.SelectMany(pair => new[] { pair.DispatcherRole, pair.RuntimeRole }).ToArray();
        await using var observer = await OpenNonPooledAsync(adminCs, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!deadline.IsCancellationRequested && !pass.IsCompleted)
        {
            await using var command = new NpgsqlCommand("""
                SELECT pid
                FROM pg_catalog.pg_stat_activity
                WHERE datname = @database AND usename = ANY(@roles) AND state = 'active'
                  AND pid <> pg_backend_pid()
                ORDER BY query_start
                LIMIT 1;
                """, observer);
            command.Parameters.AddWithValue("database", database ?? throw new InvalidOperationException("Owner connection has no database identity."));
            command.Parameters.AddWithValue("roles", roles);
            if (await command.ExecuteScalarAsync(deadline.Token) is int pid && pid > 0 && !pass.IsCompleted)
                return pid;
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
        if (pass.IsCompleted)
            throw new InvalidOperationException("Lane proof completed before a live runtime/dispatcher query could be observed for guard-loss injection.");
        throw new TimeoutException("Lane guard-loss injection did not observe an executing public-provider lane query.");
    }

    private static async Task<bool> TerminateBackendAsync(string adminCs, int backendPid)
    {
        await using var connection = await OpenNonPooledAsync(adminCs);
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid);", connection);
        command.Parameters.AddWithValue("pid", backendPid);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> ReadGuardLostActivationHeartbeatAsync(string ownerCs, Guid epoch)
    {
        await using var connection = await OpenNonPooledAsync(ownerCs);
        await using var heartbeat = new NpgsqlCommand("""
            SELECT draining AND NOT pass_active AND runtime_epoch = @epoch
            FROM appsurface_durable.runtime_heartbeat
            WHERE worker_id LIKE 'fixture-activation-%'
            ORDER BY updated_at DESC
            LIMIT 1;
            """, connection);
        heartbeat.Parameters.AddWithValue("epoch", epoch);
        return await heartbeat.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> AreFixtureActivationSessionsReleasedAsync(string ownerCs)
    {
        await using var connection = await OpenNonPooledAsync(ownerCs);
        await using var sessions = new NpgsqlCommand("SELECT count(*)::int FROM pg_stat_activity WHERE application_name LIKE 'fixture-activation-%';", connection);
        return (int)(await sessions.ExecuteScalarAsync())! == 0;
    }

    private static async Task AcquireSharedGuardAsync(NpgsqlConnection guard, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock_shared(@key);", guard);
        command.Parameters.AddWithValue("key", RecipeLock);
        await command.ExecuteNonQueryAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
    }

    private static async Task<ScenarioProof> GuardedScenarioAsync(string name, string observerCs, string ownerCs, string ownerDiagCs,
        (string Dispatcher, string Runtime)[] pairs, string cliPath, ConsumerOptions options, string manifestHash,
        string owner, Guid storeId, Guid epoch, int count, bool proveLanes, bool runOwnerDiagnostic, LaneProofRolePair[] lanePairs,
        List<PreflightReceipt> results,
        string? schema10Baseline = null, bool queueWriter = false, QueuedWriterAttempt? writerAttempt = null)
    {
        using var scenarioDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using var guard = await OpenNonPooledAsync(ownerCs, scenarioDeadline.Token);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock_shared(@key);", guard))
        {
            acquire.Parameters.AddWithValue("key", RecipeLock);
            await acquire.ExecuteNonQueryAsync(scenarioDeadline.Token).WaitAsync(TimeSpan.FromSeconds(20), scenarioDeadline.Token);
        }
        using var guardLost = new CancellationTokenSource();
        using var monitorStop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scenarioDeadline.Token, guardLost.Token);
        using var guardOperationGate = new SemaphoreSlim(1, 1);
        var monitorTask = MonitorOwnerGuardAsync(guard, guardOperationGate, monitorStop.Token, guardLost);
        QueuedWriterProbe? writerProbe = null;
        FixtureActivationLease? activationLease = null;
        FixtureActivationEvidence? activationEvidence = null;
        var sharedGuardReleased = false;
        var token = linked.Token;
        try
        {
            var backendPid = await VerifyGuardAsync(guard, guardOperationGate, token);
            for (var index = 0; index < count; index++)
            {
                var role = new NpgsqlConnectionStringBuilder(pairs[index].Runtime).Username!;
                if (queueWriter && index == 1 && writerProbe is not null)
                {
                    writerAttempt!.Stage = "runtime-child-pair-2";
                    writerAttempt.FailureKind = "runtime-child";
                }
                await VerifyGuardAsync(guard, guardOperationGate, token);
                try
                {
                    results.Add((await InvokeCliPreflightAsync(cliPath, options, pairs[index].Runtime,
                    $"PREFLIGHT_RUNTIME_CONNECTION_{index + 1}", "runtime", index + 1, role, count,
                    manifestHash, storeId, epoch, token,
                    queueWriter && index == 1 ? TimeSpan.FromSeconds(8) : null)).WithScenario(name));
                }
                catch (QueuedWriterBoundedFailureException exception) when (queueWriter && index == 1)
                {
                    writerAttempt!.FailureKind = exception.FailureKind;
                    writerAttempt.Stage = exception.Stage;
                    throw;
                }
                await VerifyGuardAsync(guard, guardOperationGate, token);
                if (queueWriter && index == 0)
                {
                    // Put the real exclusive waiter between the two runtime credential passes.
                    writerProbe = await StartQueuedWriterProbeAsync(ownerCs, token);
                    writerAttempt!.QueuedWriterObserved = writerProbe.Observed;
                    await VerifyGuardAsync(guard, guardOperationGate, token);
                }
            }
            if (runOwnerDiagnostic)
            {
                if (queueWriter) writerAttempt!.Stage = "owner-diagnostic";
                results.Add((await InvokeCliPreflightAsync(cliPath, options, ownerDiagCs, "PREFLIGHT_OWNER_CONNECTION",
                    "owner-diagnostic", null, owner, count, manifestHash, storeId, epoch, token)).WithScenario(name));
                await VerifyGuardAsync(guard, guardOperationGate, token);
            }
            if (!proveLanes || lanePairs.Length is < 1 or > 2)
                throw new InvalidOperationException("Each scenario requires full forwarding lane proof and at most one Source pair.");
            var laneEvidence = lanePairs.Length == 1
                ? "forwarder-work-flow-schedule-passed"
                : "forwarder-work-flow-schedule-and-source-work-only-denials-passed";
            if (queueWriter) { writerAttempt!.Stage = "lane-proof-before"; writerAttempt.FailureKind = "lane-proof"; }
            var laneObserver = ObserverConnectionForDatabase(observerCs, ownerCs);
            var laneBefore = await RunLaneProofAsync(laneObserver, ownerCs, lanePairs, epoch, storeId, token);
            await VerifyGuardAsync(guard, guardOperationGate, token);
            if (queueWriter) writerAttempt!.Stage = "lane-proof-after";
            var laneAfter = await RunLaneProofAsync(laneObserver, ownerCs, lanePairs, epoch, storeId, token);
            await VerifyGuardAsync(guard, guardOperationGate, token);
            if (queueWriter) { writerAttempt!.Stage = "fixture-activation"; writerAttempt.FailureKind = "fixture-activation"; }
            activationLease = await FixtureActivationProof.StartAsync(ownerCs, lanePairs[0], storeId, epoch, token);
            await VerifyGuardAsync(guard, guardOperationGate, token);
            var identity = await ReadIdentityAsync(ownerCs, token);
            if (identity.Store != storeId || identity.Epoch != epoch)
                throw new InvalidOperationException("Fixture activation changed identity or failed to complete.");

            using (var activationCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                activationEvidence = await activationLease.DrainAsync(activationCleanup.Token);
            await activationLease.DisposeAsync();
            activationLease = null;
            if (activationEvidence.StoreId != storeId || activationEvidence.RuntimeEpoch != epoch
                || !activationEvidence.HeartbeatObserved || !activationEvidence.WorkCompleted
                || !activationEvidence.DrainPersisted || !activationEvidence.AdmissionClosed
                || !activationEvidence.HostedServicesStopped || !activationEvidence.SessionsReleased)
                throw new InvalidOperationException("Fixture activation did not pass checked host, admission, heartbeat, and session cleanup.");

            await StopAndDrainGuardMonitorAsync(monitorTask, monitorStop);
            token.ThrowIfCancellationRequested();
            await ReleaseSharedGuardAsync(guard, guardOperationGate, token);
            sharedGuardReleased = true;
            if (queueWriter) writerAttempt!.GuardReleased = true;
            if (writerProbe is not null)
            {
                await writerProbe.FinishAsync(token);
                if (queueWriter) writerAttempt!.WriterFinished = writerProbe.Finished;
            }
            return new ScenarioProof(name, laneBefore, laneAfter, backendPid, count, true, true,
                schema10Baseline is not null, storeId, epoch, laneEvidence,
                writerProbe?.Observed ?? false, writerProbe?.Finished ?? false, activationEvidence);
        }
        finally
        {
            if (activationLease is not null)
            {
                using var activationCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    await activationLease.DrainAsync(activationCleanup.Token);
                }
                catch (OperationCanceledException) when (guardLost.IsCancellationRequested)
                {
                    // Guard loss invalidates the proof; the helper still owns and drains its cleanup task.
                }
                finally
                {
                    await activationLease.DisposeAsync();
                }
            }
            await StopAndDrainGuardMonitorAsync(monitorTask, monitorStop);
            if (!sharedGuardReleased)
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await ReleaseSharedGuardAsync(guard, guardOperationGate, cleanupDeadline.Token);
                    sharedGuardReleased = true;
                    if (queueWriter) writerAttempt!.GuardReleased = true;
                }
                catch
                {
                    await guard.CloseAsync();
                    throw;
                }
            }
            if (writerProbe is not null && !writerProbe.Finished)
            {
                await writerProbe.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                if (queueWriter) writerAttempt!.WriterFinished = writerProbe.Finished;
            }
        }
    }

    private static async Task<object> QueuedWriterAndFreshRerunAsync(string observerCs, string ownerCs, string ownerDiagCs,
        (string Dispatcher, string Runtime)[] pairs, string cliPath, ConsumerOptions options, string hash,
        string owner, Guid storeId, Guid epoch, int count, LaneProofRolePair[] lanePairs, List<PreflightReceipt> results)
    {
        var writerScenario = "queued-writer-prewriter-complete";
        var attempt = new QueuedWriterAttempt();
        ScenarioProof window;
        try
        {
            window = await GuardedScenarioAsync(writerScenario, observerCs, ownerCs, ownerDiagCs, pairs,
                cliPath, options, hash, owner, storeId, epoch, count, true, true,
                lanePairs, results, queueWriter: true, writerAttempt: attempt);
        }
        catch (QueuedWriterBoundedFailureException exception)
        {
            results.RemoveAll(result => result.Scenario == writerScenario);
            if (!attempt.QueuedWriterObserved || !attempt.GuardReleased || !attempt.WriterFinished)
                throw new InvalidOperationException("Queued-writer attempt failed without verified guard release and writer completion; proof is invalid.", exception);
            var failure = new
            {
                failureKind = exception.FailureKind,
                stage = exception.Stage,
                durationMilliseconds = exception.DurationMilliseconds
            };
            writerScenario = "queued-writer-postwriter-rerun";
            window = await GuardedScenarioAsync(writerScenario, observerCs, ownerCs, ownerDiagCs, pairs,
                cliPath, options, hash, owner, storeId, epoch, count, true, true,
                lanePairs, results);
            return BuildQueuedWriterReceipt("bounded-failure-then-rerun", writerScenario, window,
                queuedWriterObserved: true, writerFinished: true, failure,
                authoritativeWindowCompletedBeforeWriter: false, writerAcquiredAfterWindow: false,
                writerReleasedAfterFailure: true, writerFinishedBeforeRerun: true,
                pairs, storeId, epoch);
        }

        return BuildQueuedWriterReceipt("completed-before-writer", writerScenario, window,
            window.QueuedWriterObserved, window.WriterFinished, null,
            authoritativeWindowCompletedBeforeWriter: window.GuardIntact,
            writerAcquiredAfterWindow: window.WriterFinished,
            writerReleasedAfterFailure: false, writerFinishedBeforeRerun: false,
            pairs, storeId, epoch);
    }

    private static object BuildQueuedWriterReceipt(string outcome, string writerScenario, ScenarioProof window,
        bool queuedWriterObserved, bool writerFinished, object? boundedFailure,
        bool authoritativeWindowCompletedBeforeWriter, bool writerAcquiredAfterWindow,
        bool writerReleasedAfterFailure, bool writerFinishedBeforeRerun,
        (string Dispatcher, string Runtime)[] pairs, Guid storeId, Guid epoch)
    {
        return new
        {
            outcome,
            authoritativeScenario = writerScenario,
            queuedWriterObserved,
            writerFinished,
            authoritativeWindowCompletedBeforeWriter,
            writerAcquiredAfterWindow,
            boundedFailure,
            writerReleasedAfterFailure,
            writerFinishedBeforeRerun,
            authoritativeWindow = new
            {
                scenario = writerScenario,
                ownerGuard = "continuous-shared-session-lock",
                fixtureActivationCallbackCompleted = window.ActivationCompleted,
                laneEvidence = window.LaneEvidence,
                laneEvidenceBefore = FormatLaneEvidence(writerScenario, window.GuardBackendPid, storeId, epoch,
                    new NpgsqlConnectionStringBuilder(pairs[0].Runtime).Username!,
                    new NpgsqlConnectionStringBuilder(pairs[1].Runtime).Username!, window.LaneBefore),
                laneEvidenceAfter = FormatLaneEvidence(writerScenario, window.GuardBackendPid, storeId, epoch,
                    new NpgsqlConnectionStringBuilder(pairs[0].Runtime).Username!,
                    new NpgsqlConnectionStringBuilder(pairs[1].Runtime).Username!, window.LaneAfter),
                runtimePreflightCount = window.RuntimePreflightCount,
                guardBackendPid = window.GuardBackendPid,
                storeId = window.StoreId.ToString("D"),
                activeEpoch = window.ActiveEpoch.ToString("D"),
                ownerDiagnostic = "separate-and-never-counted",
                modeledSchema10BaselinePresent = false
            }
        };
    }

    private static object FormatLaneEvidence(string scenario, int guardPid, Guid storeId, Guid epoch,
        string forwarderRuntimeRole, string? sourceRuntimeRole, LaneProofEvidence evidence)
        => new
        {
            scenario,
            storeId = storeId.ToString("D"),
            activeEpoch = epoch.ToString("D"),
            guardBackendPid = guardPid,
            forwarder = new
            {
                runtimeRole = forwarderRuntimeRole,
                workResult = evidence.SucceededWorkDelta > 0 ? "completed" : "missing",
                flowResult = evidence.CompletedFlowDelta > 0 ? "completed" : "missing",
                scheduleResult = evidence.ActiveScheduleDelta > 0 && evidence.MaterializedScheduleOccurrenceDelta > 0 ? "completed" : "missing",
                evidence.Before,
                evidence.After,
                evidence.SucceededWorkDelta,
                evidence.CompletedFlowDelta,
                evidence.ActiveScheduleDelta,
                evidence.MaterializedScheduleOccurrenceDelta
            },
            sourceWorkOnly = sourceRuntimeRole is null ? null : new
            {
                runtimeRole = sourceRuntimeRole,
                workResult = evidence.SourceWorkOnlyDenialsVerified ? "completed" : "missing",
                flowResult = evidence.SourceWorkOnlyDenialsVerified ? "denied" : "missing",
                scheduleResult = evidence.SourceWorkOnlyDenialsVerified ? "denied" : "missing",
                allResult = evidence.SourceWorkOnlyDenialsVerified ? "denied" : "missing"
            }
        };

    private static async Task<QueuedWriterProbe> StartQueuedWriterProbeAsync(string ownerCs, CancellationToken token)
    {
        var writerCs = new NpgsqlConnectionStringBuilder(ownerCs)
        { Pooling = false, ApplicationName = "issue845-queued-writer" }.ConnectionString;
        var writer = await OpenNonPooledAsync(writerCs, token);
        var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key);", writer);
        command.Parameters.AddWithValue("key", RecipeLock);
        var lockTask = command.ExecuteNonQueryAsync(token);
        var watch = Stopwatch.StartNew();
        try
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                token.ThrowIfCancellationRequested();
                if (await HasWaitingWriterAsync(ownerCs))
                    return new QueuedWriterProbe(writer, command, lockTask);
                await Task.Delay(40, token);
            }
            throw new InvalidOperationException("Exclusive writer did not queue behind the shared owner guard.");
        }
        catch
        {
            await writer.DisposeAsync();
            await DrainChildAsync(lockTask);
            await command.DisposeAsync();
            throw;
        }
    }

    private static async Task ReleaseSharedGuardAsync(NpgsqlConnection guard, SemaphoreSlim operationGate,
        CancellationToken token)
    {
        await operationGate.WaitAsync(token);
        try
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock_shared(@key);", guard);
            release.Parameters.AddWithValue("key", RecipeLock);
            if (await release.ExecuteScalarAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token) is not true)
                throw new InvalidOperationException("Owner guard release failed.");
        }
        finally { operationGate.Release(); }
    }

    private static async Task<PreflightReceipt> InvokeCliPreflightAsync(string cliPath, ConsumerOptions options,
        string connection, string envName, string caller, int? pair, string role, int count, string manifestHash,
        Guid storeId, Guid epoch, CancellationToken cancellationToken, TimeSpan? timeoutOverride = null)
    {
        var info = new ProcessStartInfo(cliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "durable", "schema", "preflight", "--role-pairs-file", options.RolePairsPath,
                     "--migration-owner-role", options.MigrationOwnerRole, "--connection-env", envName })
            info.ArgumentList.Add(argument);
        info.Environment[envName] = connection;
        var timer = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Exact-bundle preflight did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutOverride ?? TimeSpan.FromSeconds(35));
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var stdout = await stdoutTask; _ = await stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("Exact-bundle CLI preflight failed.");
            var receipt = CliMachineReceiptParser.Parse(stdout);
            if (receipt.Caller != caller || receipt.Pair != pair || receipt.Role != role || receipt.Owner != options.MigrationOwnerRole
                || receipt.RuntimeCount != count || receipt.ManifestSha256 != manifestHash || receipt.StoreId != storeId || receipt.ActiveEpoch != epoch)
                throw new InvalidOperationException("Exact-bundle CLI receipt identity mismatch.");
            return new PreflightReceipt(string.Empty, receipt.Caller, receipt.Pair, receipt.Role, receipt.Owner, receipt.RuntimeCount,
                receipt.ManifestSha256, receipt.StoreId, receipt.ActiveEpoch, timer.Elapsed.TotalMilliseconds);
        }
        catch
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await DrainChildAsync(stdoutTask);
            await DrainChildAsync(stderrTask);
            if (timeoutOverride is not null && !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new QueuedWriterBoundedFailureException("runtime-child", "runtime-child-pair-2",
                    timer.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private static async Task ApplyWithExactCliAsync(string cli, string cs)
    {
        var info = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "durable", "schema", "apply", "--apply", "--connection-env", "PREFLIGHT_APPLY_CONNECTION" })
            info.ArgumentList.Add(argument);
        info.Environment["PREFLIGHT_APPLY_CONNECTION"] = cs;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Exact CLI apply process failed to start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token); var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        var all = await stdout + await stderr;
        if (process.ExitCode != 0 || !all.Contains("applied:", StringComparison.Ordinal)) throw new InvalidOperationException("Exact packaged CLI apply failed.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token); if (read == 0) return result.ToString();
            if (result.Length + read > 256 * 1024) throw new InvalidOperationException("Child output exceeded safe bound."); result.Append(buffer, 0, read);
        }
    }

    private static async Task ApplyPackagedRecipeAsync(PostgreSqlContainer pg, string db, string owner, string manifestPath)
    {
        var manifest = await File.ReadAllTextAsync(manifestPath);
        var result = await pg.ExecAsync(["env", "PGAPPNAME=issue845-role-recipe", "psql", "-U", "postgres", "-d", db,
            "-v", $"migration_owner_role={owner}", "-v", $"retention_operator_role={RetentionRole}",
            "-v", $"role_pairs_json={manifest}", "-f", "/tmp/configure-postgresql-roles.sql"]).WaitAsync(TimeSpan.FromMinutes(2));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Frozen provider package recipe failed (exit {result.ExitCode}). {FormatExecDiagnostic(result.Stdout, result.Stderr)}");
    }

    private static async Task ApplySchema10FixtureAsync(PostgreSqlContainer pg, string db, string owner,
        IReadOnlyList<RolePair> pairs)
    {
        var result = await pg.ExecAsync(["psql", "-U", "postgres", "-d", db,
            "-v", $"migration_owner_role={owner}",
            "-v", $"full_dispatcher_role={pairs[0].Dispatcher}",
            "-v", $"full_runtime_role={pairs[0].Runtime}",
            "-v", $"source_dispatcher_role={pairs[1].Dispatcher}",
            "-v", $"source_runtime_role={pairs[1].Runtime}",
            "-f", "/tmp/schema10-two-pair.sql"]).WaitAsync(TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Schema-10 test-owned fixture failed (exit {result.ExitCode}). {FormatExecDiagnostic(result.Stdout, result.Stderr)}");
    }

    private static string FormatExecDiagnostic(string stdout, string stderr)
    {
        const int limit = 2048;
        static string Tail(string value, int maximum) => value.Length <= maximum ? value : value[^maximum..];
        var output = Tail(stdout.Trim(), limit);
        var error = Tail(stderr.Trim(), limit);
        return $"stdout={output}; stderr={error}";
    }

    private static async Task<IReadOnlyList<MigrationEvidence>> ApplySchemaTenBaselineAsync(string cs)
    {
        var assembly = typeof(PostgreSqlDurableRuntimeSchemaManager).Assembly;
        var migrations = assembly.GetManifestResourceNames().Where(n => n.Contains(".Migrations.", StringComparison.Ordinal))
            .Select(n => LoadMigration(assembly, n)).OrderBy(x => x.Version).ToArray();
        if (migrations.Length < 11 || !migrations.Select(x => x.Version).SequenceEqual(Enumerable.Range(1, migrations.Length)))
            throw new InvalidOperationException("Frozen provider package migration resource set is not contiguous through 0011.");
        await using var connection = await OpenNonPooledAsync(cs);
        foreach (var migration in migrations.Take(10))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var sql = new NpgsqlCommand(migration.Sql, connection, transaction))
            { if (migration.Version == 10) sql.CommandTimeout = 330; await sql.ExecuteNonQueryAsync(); }
            await using (var record = new NpgsqlCommand("INSERT INTO appsurface_durable.schema_migration(version,name,sha256) VALUES(@v,@n,@h); UPDATE appsurface_durable.store_metadata SET schema_version=@v,minimum_reader_version=1,maximum_reader_version=@v,minimum_writer_version=1,maximum_writer_version=@v,updated_at=clock_timestamp() WHERE singleton;", connection, transaction))
            { record.Parameters.AddWithValue("v", migration.Version); record.Parameters.AddWithValue("n", migration.Name); record.Parameters.AddWithValue("h", migration.Sha); await record.ExecuteNonQueryAsync(); }
            await transaction.CommitAsync();
        }
        return migrations.Take(10).Select(x => new MigrationEvidence(x.Version, x.Name, x.Sha)).ToArray();
    }

    private static Migration LoadMigration(Assembly assembly, string resource)
    {
        var filename = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + 12)..];
        var match = System.Text.RegularExpressions.Regex.Match(filename, @"^(\d{4})_([a-z0-9_]+)\.sql$");
        if (!match.Success) throw new InvalidOperationException("Packaged migration resource name malformed.");
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Packaged migration resource missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        return new Migration(int.Parse(match.Groups[1].Value), match.Groups[2].Value, sql,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
    }

    private static async Task AssertSchemaTenAsync(string cs)
    {
        await using var connection = await OpenNonPooledAsync(cs);
        await using var command = new NpgsqlCommand("SELECT schema_version=10 AND (SELECT count(*) FROM appsurface_durable.schema_migration)=10 AND to_regprocedure('appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)') IS NULL FROM appsurface_durable.store_metadata WHERE singleton;", connection);
        if (await command.ExecuteScalarAsync() is not true) throw new InvalidOperationException("Baseline is not modeled schema 10 with exactly 0001-0010 and no schema-11 routine.");
    }

    private static async Task AssertSchema10BoundaryAsync(string cs, IReadOnlyList<RolePair> pairs)
    {
        await using var connection = await OpenNonPooledAsync(cs);
        await using var command = new NpgsqlCommand("""
            SELECT has_function_privilege(@full_dispatcher, 'appsurface_durable.discover_work_dispatch(text[],text[],integer)', 'EXECUTE')
               AND has_function_privilege(@full_dispatcher, 'appsurface_durable.claim_schedule_dispatch(text,interval)', 'EXECUTE')
               AND has_table_privilege(@full_dispatcher, 'appsurface_durable.flow_dispatch', 'SELECT')
               AND NOT has_function_privilege(@source_dispatcher, 'appsurface_durable.claim_schedule_dispatch(text,interval)', 'EXECUTE')
               AND NOT has_table_privilege(@source_dispatcher, 'appsurface_durable.flow_dispatch', 'SELECT')
               AND (SELECT count(*) = 2 FROM pg_proc WHERE oid IN
                 ('appsurface_durable.discover_work_dispatch(text[],text[],integer)'::regprocedure,
                  'appsurface_durable.claim_schedule_dispatch(text,interval)'::regprocedure)
                  AND proowner = (SELECT oid FROM pg_roles WHERE rolname=@migration_owner))
               AND to_regprocedure('appsurface_durable.prune_runtime_heartbeats(interval,integer,text,uuid)') IS NULL;
            """, connection);
        command.Parameters.AddWithValue("full_dispatcher", pairs[0].Dispatcher);
        command.Parameters.AddWithValue("source_dispatcher", pairs[1].Dispatcher);
        command.Parameters.AddWithValue("migration_owner", (await ReadSchemaOwnerAsync(connection)));
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException("Schema-10 forwarding grants, Source denials, function ownership, or pre-0011 absence failed.");
    }

    private static async Task<Schema10BaselineEvidence> RunSchema10BoundaryPreUpgradeProofAsync(
        string ownerCs, LaneProofRolePair[] lanePairs, Guid epoch, Guid storeId)
    {
        if (lanePairs.Length != 2 || epoch == Guid.Empty || storeId == Guid.Empty)
            throw new InvalidOperationException("Schema-10 low-level proof requires the full/work_only role pairs and initialized identity.");
        await AssertSchema10BoundaryAsync(ownerCs,
            [new RolePair(lanePairs[0].DispatcherRole, lanePairs[0].RuntimeRole, "full"),
             new RolePair(lanePairs[1].DispatcherRole, lanePairs[1].RuntimeRole, "work_only")]);

        var suffix = Guid.NewGuid().ToString("N");
        var scope = "issue845-schema10-" + suffix;
        var workId = "work-" + suffix;
        var workName = "issue845.schema10." + suffix;
        var flowId = "flow-" + suffix;
        var scheduleId = "schedule-" + suffix;
        await using (var seed = await OpenNonPooledAsync(ownerCs))
        {
            await using (var setScope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope, false);", seed))
            {
                setScope.Parameters.AddWithValue("scope", scope);
                await setScope.ExecuteNonQueryAsync();
            }
            await using (var command = new NpgsqlCommand("""
            INSERT INTO appsurface_durable.scope(scope_id) VALUES (@scope);
            INSERT INTO appsurface_durable.work
            (scope_id,work_id,activity_id,command_id,idempotency_key,work_name,work_version,contract_id,
             payload_schema_version,codec_id,payload,payload_sha256,payload_classification,payload_retention,
             request_fingerprint_schema,request_fingerprint_sha256,state,provider_safety,due_at,scope_generation,
             runtime_epoch,maximum_attempts,maximum_elapsed,backoff_algorithm,initial_retry_delay,maximum_retry_delay,
             lease_duration,lease_renewal_cadence,maximum_lease_lifetime)
            VALUES (@scope,@work,@activity,@command,@key,@name,'v1','issue845-schema10','v1','text/plain',
             decode('00','hex'),decode(repeat('00',32),'hex'),'internal','default','issue845-schema10-v1',repeat('0',64),
             'pending','idempotent',clock_timestamp()-interval '1 minute',1,@epoch,3,interval '1 hour',
             'exponential-v1',interval '1 second',interval '1 minute',interval '30 seconds',interval '10 seconds',interval '5 minutes');
            INSERT INTO appsurface_durable.work
            (scope_id,work_id,activity_id,command_id,idempotency_key,work_name,work_version,contract_id,
             payload_schema_version,codec_id,payload,payload_sha256,payload_classification,payload_retention,
             request_fingerprint_schema,request_fingerprint_sha256,state,provider_safety,due_at,scope_generation,
             runtime_epoch,maximum_attempts,maximum_elapsed,backoff_algorithm,initial_retry_delay,maximum_retry_delay,
             lease_duration,lease_renewal_cadence,maximum_lease_lifetime)
            SELECT scope_id,work_id||'-source',activity_id||'-source',command_id||'-source',idempotency_key||'-source',
             work_name||'.source',work_version,contract_id,payload_schema_version,codec_id,payload,payload_sha256,
             payload_classification,payload_retention,request_fingerprint_schema,request_fingerprint_sha256,
             'pending',provider_safety,due_at,scope_generation,runtime_epoch,maximum_attempts,maximum_elapsed,
             backoff_algorithm,initial_retry_delay,maximum_retry_delay,lease_duration,lease_renewal_cadence,maximum_lease_lifetime
            FROM appsurface_durable.work WHERE scope_id=@scope AND work_id=@work;
            INSERT INTO appsurface_durable.dispatch(dispatch_id,scope_id,aggregate_kind,aggregate_id,due_at,state,expected_revision)
            VALUES (@dispatch,@scope,'work',@work,clock_timestamp()-interval '1 minute','available',1);
            INSERT INTO appsurface_durable.dispatch(dispatch_id,scope_id,aggregate_kind,aggregate_id,due_at,state,expected_revision)
            VALUES (md5(@work||'-source-dispatch')::uuid,@scope,'work',@work||'-source',clock_timestamp()-interval '1 minute','available',1);
            INSERT INTO appsurface_durable.flow_instance
            (scope_id,flow_instance_id,flow_id,flow_version,manifest_id,authoring_model,definition_fingerprint_schema,
             definition_fingerprint_sha256,current_node_id,state,revision,scope_generation,runtime_epoch)
            VALUES (@scope,@flow,'issue845-schema10','v1','issue845-schema10','test','issue845-schema10-v1',repeat('0',64),
             'start','ready',1,1,@epoch);
            INSERT INTO appsurface_durable.flow_dispatch(dispatch_id,scope_id,kind,flow_instance_id,due_at,state,expected_revision)
            VALUES (@flow_dispatch,@scope,'flow',@flow,clock_timestamp()-interval '1 minute','available',1);
            INSERT INTO appsurface_durable.schedule_definition
            (scope_id,schedule_id,state,active_generation,revision,accepted_at_utc,cursor_utc,next_due_utc,scope_generation,runtime_epoch)
            VALUES (@scope,@schedule,'active',1,1,clock_timestamp()-interval '2 minutes',clock_timestamp()-interval '2 minutes',
             clock_timestamp()-interval '1 minute',1,@epoch);
            INSERT INTO appsurface_durable.schedule_dispatch
            (scope_id,schedule_id,dispatch_revision,due_at,state,lease_generation)
            VALUES (@scope,@schedule,1,clock_timestamp()-interval '1 minute','available',0);
            """, seed))
            {
                command.Parameters.AddWithValue("scope", scope);
                command.Parameters.AddWithValue("work", workId);
                command.Parameters.AddWithValue("activity", "activity-" + suffix);
                command.Parameters.AddWithValue("command", "command-" + suffix);
                command.Parameters.AddWithValue("key", "key-" + suffix);
                command.Parameters.AddWithValue("name", workName);
                command.Parameters.AddWithValue("epoch", epoch);
                command.Parameters.AddWithValue("dispatch", Guid.NewGuid());
                command.Parameters.AddWithValue("flow", flowId);
                command.Parameters.AddWithValue("flow_dispatch", Guid.NewGuid());
                command.Parameters.AddWithValue("schedule", scheduleId);
                await command.ExecuteNonQueryAsync();
            }
        }

        var dispatcher = CreateRoleDataSource(ownerCs, lanePairs[0].DispatcherRole, lanePairs[0].DispatcherPassword);
        var runtime = CreateRoleDataSource(ownerCs, lanePairs[0].RuntimeRole, lanePairs[0].RuntimePassword);
        var sourceDispatcher = CreateRoleDataSource(ownerCs, lanePairs[1].DispatcherRole, lanePairs[1].DispatcherPassword);
        var sourceRuntime = CreateRoleDataSource(ownerCs, lanePairs[1].RuntimeRole, lanePairs[1].RuntimePassword);
        try
        {
            string? discoveredScope;
            string? discoveredWork;
            await using (var connection = await dispatcher.OpenConnectionAsync())
            await using (var query = new NpgsqlCommand("SELECT scope_id,aggregate_id FROM appsurface_durable.discover_work_dispatch(@names,@versions,10);", connection))
            {
                query.Parameters.AddWithValue("names", new[] { workName });
                query.Parameters.AddWithValue("versions", new[] { "v1" });
                await using var reader = await query.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw new InvalidOperationException("Schema-10 full dispatcher did not discover seeded Work.");
                discoveredScope = reader.GetString(0);
                discoveredWork = reader.GetString(1);
                if (await reader.ReadAsync()) throw new InvalidOperationException("Schema-10 Work discovery returned duplicate candidates.");
            }
            if (discoveredScope != scope || discoveredWork != workId)
                throw new InvalidOperationException("Schema-10 Work discovery returned mismatched fixture identity.");

            await using (var connection = await runtime.OpenConnectionAsync())
            {
                await using (var scopeConfig = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id',@scope,false);", connection))
                { scopeConfig.Parameters.AddWithValue("scope", scope); await scopeConfig.ExecuteNonQueryAsync(); }
                await using var complete = new NpgsqlCommand("UPDATE appsurface_durable.work SET state='succeeded',terminal_at=clock_timestamp(),updated_at=clock_timestamp() WHERE scope_id=@scope AND work_id=@work AND state='pending';", connection);
                complete.Parameters.AddWithValue("scope", scope);
                complete.Parameters.AddWithValue("work", workId);
                if (await complete.ExecuteNonQueryAsync() != 1)
                    throw new InvalidOperationException("Schema-10 runtime role could not commit the modeled Work completion.");
            }

            int flowRows;
            await using (var connection = await dispatcher.OpenConnectionAsync())
            await using (var query = new NpgsqlCommand("SELECT count(*)::int FROM appsurface_durable.flow_dispatch WHERE scope_id=@scope AND flow_instance_id=@flow AND state='available';", connection))
            {
                query.Parameters.AddWithValue("scope", scope);
                query.Parameters.AddWithValue("flow", flowId);
                flowRows = (int)(await query.ExecuteScalarAsync())!;
            }
            if (flowRows != 1) throw new InvalidOperationException("Schema-10 full dispatcher did not discover the seeded Flow dispatch.");

            int scheduleRows;
            await using (var connection = await dispatcher.OpenConnectionAsync())
            await using (var claim = new NpgsqlCommand("SELECT count(*)::int FROM appsurface_durable.claim_schedule_dispatch(@worker,interval '1 minute') WHERE scope_id=@scope AND schedule_id=@schedule;", connection))
            {
                claim.Parameters.AddWithValue("worker", "issue845-schema10-" + suffix);
                claim.Parameters.AddWithValue("scope", scope);
                claim.Parameters.AddWithValue("schedule", scheduleId);
                scheduleRows = (int)(await claim.ExecuteScalarAsync())!;
            }
            if (scheduleRows != 1) throw new InvalidOperationException("Schema-10 full dispatcher did not claim the seeded Schedule dispatch.");

            var sourceWork = 0;
            await using (var connection = await sourceDispatcher.OpenConnectionAsync())
            await using (var discover = new NpgsqlCommand("SELECT count(*)::int FROM appsurface_durable.discover_work_dispatch(@names,@versions,10);", connection))
            {
                discover.Parameters.AddWithValue("names", new[] { workName });
                discover.Parameters.AddWithValue("versions", new[] { "v1" });
                sourceWork = (int)(await discover.ExecuteScalarAsync())!;
            }
            if (sourceWork != 1) throw new InvalidOperationException("Schema-10 Source Work-only role could not discover its pending Work candidate.");
            await using (var connection = await sourceRuntime.OpenConnectionAsync())
            {
                await using (var scopeConfig = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id',@scope,false);", connection))
                { scopeConfig.Parameters.AddWithValue("scope", scope); await scopeConfig.ExecuteNonQueryAsync(); }
                await using var complete = new NpgsqlCommand("UPDATE appsurface_durable.work SET state='succeeded',terminal_at=clock_timestamp(),updated_at=clock_timestamp() WHERE scope_id=@scope AND work_id=@work AND state='pending';", connection);
                complete.Parameters.AddWithValue("scope", scope);
                complete.Parameters.AddWithValue("work", workId + "-source");
                if (await complete.ExecuteNonQueryAsync() != 1)
                    throw new InvalidOperationException("Schema-10 Source runtime role could not complete its Work-only lane.");
            }

            var flowDenied = await AssertDeniedAsync(sourceDispatcher, "SELECT count(*) FROM appsurface_durable.flow_dispatch;");
            var scheduleDenied = await AssertDeniedAsync(sourceDispatcher, "SELECT count(*) FROM appsurface_durable.claim_schedule_dispatch('issue845-source',interval '1 minute');");
            if (!flowDenied || !scheduleDenied)
                throw new InvalidOperationException("Schema-10 Source role unexpectedly accessed Flow or Schedule dispatch.");

            // These rows model schema-10 SQL capabilities, not registered provider payloads.
            // Retire only this probe's dispatch under the restricted runtime before the real
            // post-upgrade host can discover it; retain every row for independent verification.
            await using (var connection = await runtime.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using (var scopeConfig = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id',@scope,true);", connection, transaction))
                { scopeConfig.Parameters.AddWithValue("scope", scope); await scopeConfig.ExecuteNonQueryAsync(); }
                (string Sql, int ExpectedRows)[] retirements =
                [
                    ("UPDATE appsurface_durable.dispatch SET state='terminal',updated_at=clock_timestamp() WHERE scope_id=@scope AND aggregate_id IN (@work,@work||'-source') AND state='available';", 2),
                    ("UPDATE appsurface_durable.flow_instance SET state='canceled',terminal_at=clock_timestamp(),terminal_code='schema10-capability-probe-retired',updated_at=clock_timestamp() WHERE scope_id=@scope AND flow_instance_id=@flow AND state='ready';", 1),
                    ("UPDATE appsurface_durable.flow_dispatch SET state='terminal',updated_at=clock_timestamp() WHERE scope_id=@scope AND flow_instance_id=@flow AND state='available';", 1),
                    ("UPDATE appsurface_durable.schedule_definition SET state='paused',updated_at=clock_timestamp() WHERE scope_id=@scope AND schedule_id=@schedule AND state='active';", 1),
                    ("UPDATE appsurface_durable.schedule_dispatch SET state='terminal',lease_owner=NULL,lease_expires_at=NULL,updated_at=clock_timestamp() WHERE scope_id=@scope AND schedule_id=@schedule AND state='leased' AND lease_owner=@worker;", 1)
                ];
                foreach (var (sql, expectedRows) in retirements)
                {
                    await using var retire = new NpgsqlCommand(sql, connection, transaction);
                    retire.Parameters.AddWithValue("scope", scope);
                    retire.Parameters.AddWithValue("work", workId);
                    retire.Parameters.AddWithValue("flow", flowId);
                    retire.Parameters.AddWithValue("schedule", scheduleId);
                    retire.Parameters.AddWithValue("worker", "issue845-schema10-" + suffix);
                    if (await retire.ExecuteNonQueryAsync() != expectedRows)
                        throw new InvalidOperationException("Schema-10 restricted runtime did not retire every capability probe row.");
                }
                await transaction.CommitAsync();
            }

            await using var verify = await OpenNonPooledAsync(ownerCs);
            await using (var setScope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope, false);", verify))
            {
                setScope.Parameters.AddWithValue("scope", scope);
                await setScope.ExecuteNonQueryAsync();
            }
            await using var result = new NpgsqlCommand("SELECT state='succeeded' AND terminal_at IS NOT NULL FROM appsurface_durable.work WHERE scope_id=@scope AND work_id=@work;", verify);
            result.Parameters.AddWithValue("scope", scope);
            result.Parameters.AddWithValue("work", workId);
            if (await result.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("Schema-10 modeled Work completion was not durable.");
            await using var sourceResult = new NpgsqlCommand("SELECT state='succeeded' AND terminal_at IS NOT NULL FROM appsurface_durable.work WHERE scope_id=@scope AND work_id=@work;", verify);
            sourceResult.Parameters.AddWithValue("scope", scope);
            sourceResult.Parameters.AddWithValue("work", workId + "-source");
            if (await sourceResult.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("Schema-10 Source Work-only completion was not durable.");

            await using var retired = new NpgsqlCommand("""
                SELECT
                    (SELECT count(*)=2 FROM appsurface_durable.dispatch
                     WHERE scope_id=@scope AND aggregate_id IN (@work,@work||'-source') AND state='terminal')
                    AND EXISTS (SELECT 1 FROM appsurface_durable.flow_instance
                                WHERE scope_id=@scope AND flow_instance_id=@flow AND state='canceled'
                                AND terminal_at IS NOT NULL AND terminal_code='schema10-capability-probe-retired')
                    AND (SELECT count(*)=1 FROM appsurface_durable.flow_dispatch
                         WHERE scope_id=@scope AND flow_instance_id=@flow AND state='terminal')
                    AND EXISTS (SELECT 1 FROM appsurface_durable.schedule_definition
                                WHERE scope_id=@scope AND schedule_id=@schedule AND state='paused')
                    AND EXISTS (SELECT 1 FROM appsurface_durable.schedule_dispatch
                                WHERE scope_id=@scope AND schedule_id=@schedule AND state='terminal'
                                AND lease_owner IS NULL AND lease_expires_at IS NULL);
                """, verify);
            retired.Parameters.AddWithValue("scope", scope);
            retired.Parameters.AddWithValue("work", workId);
            retired.Parameters.AddWithValue("flow", flowId);
            retired.Parameters.AddWithValue("schedule", scheduleId);
            if (await retired.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("Schema-10 capability probe retirement was not durable before upgrade.");

            return new Schema10BaselineEvidence(true,
                new { role = lanePairs[0].RuntimeRole, dispatchDiscovered = true, durableCompletion = true },
                new { dispatcher = lanePairs[0].DispatcherRole, candidateRows = flowRows },
                new { dispatcher = lanePairs[0].DispatcherRole, claimedRows = scheduleRows },
                new { dispatcher = lanePairs[1].DispatcherRole, workDiscoveryRows = sourceWork, flowDenied, scheduleDenied });
        }
        finally
        {
            await dispatcher.DisposeAsync();
            await runtime.DisposeAsync();
            await sourceDispatcher.DisposeAsync();
            await sourceRuntime.DisposeAsync();
        }
    }

    private static async Task<bool> AssertDeniedAsync(NpgsqlDataSource source, string sql)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            _ = await command.ExecuteScalarAsync();
            return false;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return true;
        }
    }

    private static async Task<string> ReadSchemaOwnerAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT r.rolname FROM pg_namespace n JOIN pg_roles r ON r.oid=n.nspowner WHERE n.nspname='appsurface_durable';", connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Schema owner missing."));
    }

    private static async Task<string> ReadCatalogSnapshotAsync(string cs)
    {
        await using var connection = await OpenNonPooledAsync(cs);
        await using var command = new NpgsqlCommand("SELECT jsonb_build_object('owners',(SELECT jsonb_agg(jsonb_build_array(c.relname,r.rolname) ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner WHERE n.nspname='appsurface_durable'),'policies',(SELECT jsonb_agg(jsonb_build_array(c.relname,p.polname,p.polcmd,p.polroles::text,p.polqual::text,p.polwithcheck::text) ORDER BY c.relname,p.polname) FROM pg_policy p JOIN pg_class c ON c.oid=p.polrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='appsurface_durable'),'functions',(SELECT jsonb_agg(jsonb_build_array(p.oid::regprocedure::text,r.rolname,p.proacl::text) ORDER BY p.oid::regprocedure::text) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_roles r ON r.oid=p.proowner WHERE n.nspname='appsurface_durable'))::text;", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> VerifyGuardAsync(NpgsqlConnection connection, SemaphoreSlim operationGate,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_backend_pid(),EXISTS(SELECT 1 FROM pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory' AND granted AND mode='ShareLock' AND classid=((@key >> 32)&4294967295)::oid AND objid=(@key&4294967295)::oid);", connection);
            command.Parameters.AddWithValue("key", RecipeLock);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (!reader.GetBoolean(1)) throw new InvalidOperationException("Continuous owner guard was lost.");
            return reader.GetInt32(0);
        }
        finally { operationGate.Release(); }
    }

    private static async Task MonitorOwnerGuardAsync(NpgsqlConnection connection, SemaphoreSlim operationGate,
        CancellationToken stopToken, CancellationTokenSource guardLost)
    {
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                probeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                await VerifyGuardAsync(connection, operationGate, probeDeadline.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), stopToken);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            guardLost.Cancel();
            throw new TimeoutException("The concurrent owner-guard probe exceeded its two-second bound.");
        }
        catch
        {
            guardLost.Cancel();
            throw;
        }
    }

    private static async Task StopAndDrainGuardMonitorAsync(Task monitorTask, CancellationTokenSource monitorStop)
    {
        monitorStop.Cancel();
        await monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task DrainChildAsync(Task child)
    {
        try { await child; }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private static async Task<LaneProofEvidence> RunLaneProofAsync(string observerConnectionString, string ownerConnectionString,
        LaneProofRolePair[] lanePairs, Guid epoch, Guid storeId, CancellationToken cancellationToken)
    {
        var before = await ReadLaneDurableEffectsAsync(observerConnectionString, cancellationToken);
        if (lanePairs.Length == 1)
            await LaneProof.VerifyForwarderAsync(ownerConnectionString, lanePairs[0], epoch, storeId, cancellationToken);
        else if (lanePairs.Length == 2)
            await LaneProof.VerifyAsync(ownerConnectionString, lanePairs[0], lanePairs[1], epoch, storeId, cancellationToken);
        else
            throw new InvalidOperationException("Lane proof requires the full pair and optional work_only pair.");
        var after = await ReadLaneDurableEffectsAsync(observerConnectionString, cancellationToken);
        var evidence = new LaneProofEvidence(
            before,
            after,
            after.SucceededWork - before.SucceededWork,
            after.CompletedFlow - before.CompletedFlow,
            after.ActiveSchedule - before.ActiveSchedule,
            after.MaterializedScheduleOccurrence - before.MaterializedScheduleOccurrence,
            lanePairs.Length == 2);
        if (evidence.SucceededWorkDelta < (lanePairs.Length == 1 ? 2 : 3)
            || evidence.CompletedFlowDelta < 1
            || evidence.ActiveScheduleDelta < 1
            || evidence.MaterializedScheduleOccurrenceDelta < 1)
            throw new InvalidOperationException(
                "Provider lane calls returned without producing the required new durable Work/Flow/Schedule effects " +
                $"(work={evidence.SucceededWorkDelta}, flow={evidence.CompletedFlowDelta}, " +
                $"activeSchedule={evidence.ActiveScheduleDelta}, " +
                $"materializedOccurrence={evidence.MaterializedScheduleOccurrenceDelta}; " +
                $"before={before}, after={after}).");
        return evidence;
    }

    private static async Task<LaneEffectSnapshot> ReadLaneDurableEffectsAsync(string ownerCs, CancellationToken token)
    {
        await using var connection = await OpenNonPooledAsync(ownerCs, token);
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*)::int FROM appsurface_durable.work WHERE scope_id LIKE 'lane-proof-%' AND state='succeeded'),
              (SELECT count(*)::int FROM appsurface_durable.flow_instance WHERE scope_id LIKE 'lane-proof-%' AND state='completed'),
              (SELECT count(*)::int FROM appsurface_durable.schedule_definition WHERE scope_id LIKE 'lane-proof-%' AND state='active'),
              (SELECT count(*)::int FROM appsurface_durable.schedule_occurrence WHERE scope_id LIKE 'lane-proof-%' AND state='materialized');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Durable lane evidence query returned no row.");
        return new LaneEffectSnapshot(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private static string ObserverConnectionForDatabase(string observerCs, string targetCs)
    {
        var target = new NpgsqlConnectionStringBuilder(targetCs);
        return new NpgsqlConnectionStringBuilder(observerCs)
        {
            Database = target.Database,
            Pooling = false,
            Multiplexing = false,
            Enlist = false
        }.ConnectionString;
    }

    private sealed class QueuedWriterProbe(NpgsqlConnection connection, NpgsqlCommand command, Task<int> lockTask)
    {
        public bool Observed { get; } = true;
        public bool Finished { get; private set; }

        public async Task FinishAsync(CancellationToken cancellationToken)
        {
            if (Finished) return;
            await lockTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", connection);
            unlock.Parameters.AddWithValue("key", RecipeLock);
            if (await unlock.ExecuteScalarAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken) is not true)
                throw new InvalidOperationException("Queued exclusive writer did not own its advisory lock after the scenario window.");
            Finished = true;
            await command.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class QueuedWriterAttempt
    {
        internal bool QueuedWriterObserved { get; set; }
        internal bool GuardReleased { get; set; }
        internal bool WriterFinished { get; set; }
        internal string? FailureKind { get; set; }
        internal string? Stage { get; set; } = "runtime-child-pair-1";
    }

    private sealed class QueuedWriterBoundedFailureException(string failureKind, string stage,
        double durationMilliseconds) : Exception("Queued writer bounded the protected scenario.")
    {
        internal string FailureKind { get; } = failureKind;
        internal string Stage { get; } = stage;
        internal double DurationMilliseconds { get; } = Math.Max(1, durationMilliseconds);
    }

    private static async Task<bool> HasWaitingWriterAsync(string cs)
    {
        await using var connection = await OpenNonPooledAsync(cs);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND classid=((@key >> 32)&4294967295)::oid AND objid=(@key&4294967295)::oid AND NOT granted);", connection);
        command.Parameters.AddWithValue("key", RecipeLock); return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task PublishReceiptAtomicallyAsync(string path, byte[] bytes)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Receipt destination has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(destination) || Directory.Exists(destination) || File.Exists(destination + ".tmp"))
            throw new InvalidOperationException("Receipt destination already exists.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static async Task<(Guid Store, Guid? Epoch)> ReadIdentityAsync(string cs, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenNonPooledAsync(cs, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT store_id,active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Store metadata absent.");
        return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private static async Task InitializeEpochAsync(string cs, Guid epoch)
    { await using var source = NpgsqlDataSource.Create(cs); await new PostgreSqlDurableRuntimeSchemaManager(source).InitializeRuntimeEpochAsync(epoch, "issue845-test", "fixture-epoch-bootstrap"); }
    private static async Task SetStoreIdAsync(string cs, Guid id)
    { await using var connection = await OpenNonPooledAsync(cs); await using var command = new NpgsqlCommand("UPDATE appsurface_durable.store_metadata SET store_id=@id WHERE singleton;", connection); command.Parameters.AddWithValue("id", id); await command.ExecuteNonQueryAsync(); }

    private static async Task SetActiveEpochDirectAsync(string cs, Guid epoch)
    {
        await using var connection = await OpenNonPooledAsync(cs);
        await using var command = new NpgsqlCommand("UPDATE appsurface_durable.store_metadata SET active_runtime_epoch=@epoch WHERE singleton AND schema_version=10;", connection);
        command.Parameters.AddWithValue("epoch", epoch);
        if (await command.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException("Could not install the disposable epoch on the modeled schema-10 baseline.");
    }

    private static async Task CreateRolesAsync(string admin, string owner, string ownerPassword,
        string retentionRole, string retentionPassword, IReadOnlyList<RolePair> pairs,
        string[] runtimePasswords, string[] dispatcherPasswords)
    {
        await using var connection = await OpenNonPooledAsync(admin);
        var sql = new StringBuilder().Append("CREATE ROLE ").Append(QI(owner)).Append(" LOGIN PASSWORD ").Append(QL(ownerPassword)).AppendLine(" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;")
            .Append("CREATE ROLE ").Append(QI(retentionRole)).Append(" LOGIN PASSWORD ").Append(QL(retentionPassword)).AppendLine(" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;");
        for (var i = 0; i < pairs.Count; i++)
            sql.Append("CREATE ROLE ").Append(QI(pairs[i].Dispatcher)).Append(" LOGIN PASSWORD ").Append(QL(dispatcherPasswords[i])).AppendLine(" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;")
                .Append("CREATE ROLE ").Append(QI(pairs[i].Runtime)).Append(" LOGIN PASSWORD ").Append(QL(runtimePasswords[i])).AppendLine(" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;");
        await using var command = new NpgsqlCommand(sql.ToString(), connection); await command.ExecuteNonQueryAsync();
    }

    private static LaneProofRolePair[] BuildLanePairs(IReadOnlyList<RolePair> pairs, string[] dispatcherPasswords, string[] runtimePasswords) =>
        pairs.Select((pair, index) => new LaneProofRolePair(pair.Dispatcher, dispatcherPasswords[index], pair.Runtime, runtimePasswords[index])).ToArray();

    private static NpgsqlDataSource CreateRoleDataSource(string connectionString, string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        { Username = role, Password = password, Pooling = false, Multiplexing = false, Enlist = false };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static async Task CreateDatabaseAsync(string admin, string database, string owner)
    { await using var connection = await OpenNonPooledAsync(admin); await using var command = new NpgsqlCommand($"CREATE DATABASE {QI(database)} OWNER {QI(owner)};", connection); await command.ExecuteNonQueryAsync(); }
    private static (string Dispatcher, string Runtime)[] BuildPairConnections(string admin, string db, IReadOnlyList<RolePair> pairs, string[] runtime, string[] dispatcher) =>
        pairs.Select((pair, index) => (Connection(admin, db, pair.Dispatcher, dispatcher[index], true), Connection(admin, db, pair.Runtime, runtime[index], true))).ToArray();
    private static string Connection(string admin, string database, string user, string password, bool pooling) =>
        new NpgsqlConnectionStringBuilder(admin) { Database = database, Username = user, Password = password, Pooling = pooling, Multiplexing = false, Enlist = false }.ConnectionString;
    private static async Task<NpgsqlConnection> OpenNonPooledAsync(string cs, CancellationToken cancellationToken = default)
    { var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false, Multiplexing = false, Enlist = false }.ConnectionString); await connection.OpenAsync(cancellationToken); return connection; }
    private static string RandomPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private static string QI(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string QL(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private sealed record Migration(int Version, string Name, string Sql, string Sha);
    private sealed record MigrationEvidence(int Version, string Name, string Sha);
    private sealed record Schema10BaselineEvidence(bool Passed, object Work, object Flow, object Schedule, object SourceDenials);
    private sealed record LaneEffectSnapshot(int SucceededWork, int CompletedFlow, int ActiveSchedule,
        int MaterializedScheduleOccurrence);
    private sealed record LaneProofEvidence(LaneEffectSnapshot Before, LaneEffectSnapshot After,
        int SucceededWorkDelta, int CompletedFlowDelta, int ActiveScheduleDelta,
        int MaterializedScheduleOccurrenceDelta, bool SourceWorkOnlyDenialsVerified);
    private sealed record GuardLossEvidence(int GuardBackendPid, int ExecutingBackendPid, Guid StoreId, Guid ActiveEpoch,
        bool ChildPassStarted, bool ChildPassObservedExecuting, bool BackendTerminated,
        bool CancellationObserved, bool ChildDrained, bool FailedChildReceiptWithheld);
    private sealed record ActivationGuardLossEvidence(int GuardBackendPid, Guid StoreId, Guid ActiveEpoch,
        bool WorkInvocationStartedBeforeCompletion, bool BackendTerminated, bool CancellationObserved,
        bool DrainVerifiedCheckpointObserved, bool HostedServicesStopped, bool HostDrainPersisted,
        bool AdmissionClosed, bool SessionsReleased, bool IdentityUnchanged, bool ChildDrained,
        bool FailedChildReceiptWithheld);
    private sealed record ScenarioProof(string Scenario, LaneProofEvidence LaneBefore, LaneProofEvidence LaneAfter, int GuardBackendPid,
        int RuntimePreflightCount, bool GuardIntact, bool ActivationCompleted, bool ModeledSchema10BaselinePresent,
        Guid StoreId, Guid ActiveEpoch, string LaneEvidence, bool QueuedWriterObserved, bool WriterFinished,
        FixtureActivationEvidence? ActivationEvidence);
}
