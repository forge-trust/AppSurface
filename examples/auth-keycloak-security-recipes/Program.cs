using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

var actionArguments = args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
var action = actionArguments.FirstOrDefault();
var positionalCount = args.Length - actionArguments.Length;
if (positionalCount is < 5 or > 7 || actionArguments.Length > 1
    || action is not (null or "--run" or "--inspect" or "--wrong-identity")
    || (actionArguments.Length == 1 && args[^1] != action))
{
    Console.Error.WriteLine("Usage: AuthKeycloakSecurityRecipes <recipe.json> <policy.json> <bindings.json> <snapshot-directory> <host-image-reference> [context-directory] [evidence-file] [--run|--inspect|--wrong-identity]");
    return 2;
}

string recipePath, policyPath, bindingsPath, snapshotPath, contextPath, evidencePath;
try
{
    recipePath = Path.GetFullPath(args[0]);
    policyPath = Path.GetFullPath(args[1]);
    bindingsPath = Path.GetFullPath(args[2]);
    snapshotPath = Path.GetFullPath(args[3]);
    contextPath = positionalCount >= 6 ? Path.GetFullPath(args[5]) : Path.Combine(Path.GetDirectoryName(snapshotPath)!, "keycloak-context");
    evidencePath = positionalCount >= 7 ? Path.GetFullPath(args[6]) : Path.Combine(Path.GetDirectoryName(snapshotPath)!, "keycloak-evidence.json");
}
catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
{
    Console.Error.WriteLine("Supply valid host-owned input, snapshot, context and evidence paths.");
    return 2;
}
var hostImageReference = args[4];
var run = action is null or "--run";
var wrongIdentity = action == "--wrong-identity";

if (string.IsNullOrWhiteSpace(hostImageReference) || hostImageReference.Length > 128 || hostImageReference.StartsWith('-')
    || hostImageReference.Contains('@', StringComparison.Ordinal)
    || hostImageReference.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not '/' and not ':'))
{
    Console.Error.WriteLine("host-image-reference must be an explicit local tag; digest references are unsupported for the build handoff.");
    return 2;
}

ConcurrentQueue<KeycloakStageEvent>? stageEvents = null;
IDisposable? stageSubscription = null;
string? stageProgressPath = null;
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
    var token = deadline.Token;
    stageEvents = [];
    stageSubscription = KeycloakStageEvents.Subscribe(stageEvent =>
    {
        stageEvents.Enqueue(stageEvent);
        try
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                stage = stageEvent.Phase.ToString(),
                outcome = stageEvent.Outcome.ToString(),
                correlation = stageEvent.CorrelationId,
                elapsedMilliseconds = stageEvent.ElapsedMilliseconds,
                materialCount = stageEvent.MaterialCount,
                materialBytes = stageEvent.MaterialBytes,
                checkCount = stageEvent.CheckCount,
                effect = stageEvent.Effect.ToString(),
                recipeDigest = stageEvent.RecipeDigest,
                policyDigest = stageEvent.PolicyDigest,
                planDigest = stageEvent.PlanDigest,
                operationId = stageEvent.OperationId,
                checkId = stageEvent.CheckId,
                profile = stageEvent.Profile,
                safeCode = stageEvent.SafeCode,
                truncated = stageEvent.Truncated
            }));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });
    stageProgressPath = evidencePath + ".stages.json";
    var recipeBytes = await File.ReadAllBytesAsync(recipePath);
    var policyBytes = await File.ReadAllBytesAsync(policyPath);
    var recipe = KeycloakCanonicalJson.ReadRecipe(recipeBytes);
    var policy = KeycloakCanonicalJson.ReadPolicy(policyBytes);
    var bindings = ReadBindings(bindingsPath);
    Console.WriteLine($"recipeSHA256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(KeycloakCanonicalJson.Write(recipe)))}");
    Console.WriteLine($"policySHA256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(KeycloakCanonicalJson.Write(policy)))}");

    if (run)
    {
        using var preflight = KeycloakStageEvents.Begin(KeycloakStagePhase.DockerPreflight, profile: "docker-host");
        try
        {
            var receipt = await ReadDockerHostReceiptAsync(token);
            await WriteJsonAsync(evidencePath + ".host.json", receipt, token);
            preflight.Complete(KeycloakEffectState.None);
        }
        catch
        {
            preflight.Reject("PREFLIGHT_FAILED");
            throw;
        }
    }

    using var plan = await KeycloakSecurity.CaptureAsync(recipe, policy, bindings, snapshotPath, token);
    Console.WriteLine($"captured={plan.Digest} platform={plan.Platform}");

    if (run)
    {
        var context = await KeycloakSecurity.MaterializeAsync(plan, contextPath, token);
        context.Verify(token);
        Console.WriteLine($"materialized={context.Digest} context={context.DirectoryPath}");
        var buildArguments = context.CreateDockerBuildArguments(hostImageReference, token);
        // The builder is a host observation recorded in keycloak-host-receipt.json;
        // this stage profile is deliberately not an asserted builder identity.
        using var buildStage = KeycloakStageEvents.Begin(KeycloakStagePhase.Build, plan.RecipeDigest, plan.PolicyDigest, "docker-build", plan.Digest, "build");
        var start = new ProcessStartInfo("docker") { WorkingDirectory = context.DirectoryPath, UseShellExecute = false };
        foreach (var argument in buildArguments) start.ArgumentList.Add(argument);
        using var docker = Process.Start(start) ?? throw new InvalidOperationException("Docker process could not start.");
        try { await docker.WaitForExitAsync(token); }
        catch (OperationCanceledException) when (!docker.HasExited) { docker.Kill(entireProcessTree: true); throw; }
        if (docker.ExitCode != 0)
        {
            var failure = new KeycloakSecurityException(KeycloakFailureCode.BuildFailed, KeycloakSafePhase.Build, plan.Digest,
                KeycloakEffectState.Unknown, KeycloakRecoveryClass.InspectBeforeRetry,
                "REJECTED BuildFailed. Inspect host effects before a fresh invocation.",
                "Inspect the host image and context before retrying.", new KeycloakCorrelationId(buildStage.CorrelationId));
            buildStage.Reject(failure);
            throw failure;
        }
        buildStage.Complete(KeycloakEffectState.Committed);
    }

    var image = await KeycloakSecurity.ResolveLocalImageAsync(hostImageReference, token);
    if (wrongIdentity)
    {
        try
        {
            _ = await KeycloakSecurity.InspectAsync(plan, image with { ConfigDigest = "sha256:" + new string('0', 64) }, token);
            Console.Error.WriteLine("wrong-identity=unexpectedly accepted");
            return 1;
        }
        catch (KeycloakSecurityException failure) when (failure.Code is KeycloakFailureCode.InspectionFailed or KeycloakFailureCode.DigestMismatch or KeycloakFailureCode.CaptureInvalid)
        {
            Console.WriteLine($"wrong-identity=rejected\n{failure.Message}");
            return 0;
        }
    }

    var evidence = await KeycloakSecurity.InspectAsync(plan, image, token);
    var writtenEvidence = await evidence.WriteAsync(evidencePath, token);
    using (var releaseStage = KeycloakStageEvents.Begin(KeycloakStagePhase.ReleasePending, plan.RecipeDigest, plan.PolicyDigest, recipe.SourceProfiles.FirstOrDefault(), plan.Digest, "release-pending"))
        releaseStage.Complete(KeycloakEffectState.None);
    Console.WriteLine($"inspection=passed checks={evidence.Checks.Length} evidence={writtenEvidence}");
    return 0;
}
catch (KeycloakSecurityException failure)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The bounded sample operation was cancelled or exceeded its deadline. Inspect host effects before any fresh invocation.");
    return 1;
}
catch (JsonException)
{
    Console.Error.WriteLine("The input JSON does not match the complete recipe, policy or binding contract. Correct the input before starting a fresh invocation.");
    return 1;
}
catch (System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine("Docker could not start. Verify the documented Docker host prerequisites before starting a fresh invocation.");
    return 1;
}
catch (Exception failure) when (failure is UnauthorizedAccessException or KeyNotFoundException or FormatException)
{
    Console.Error.WriteLine("A host input is inaccessible or does not match its declared contract. Correct it before starting a fresh invocation.");
    return 1;
}
catch (IOException)
{
    Console.Error.WriteLine("The host image/build boundary could not complete because a local input or process I/O operation failed. Inspect the host state and retry only with an unchanged reviewed invocation.");
    return 1;
}
catch (InvalidOperationException)
{
    Console.Error.WriteLine("The host image/build boundary could not complete. Inspect the host state and retry only with an unchanged reviewed invocation.");
    return 1;
}
finally
{
    if (stageEvents is not null && stageProgressPath is not null)
    {
        try { await WriteJsonAsync(stageProgressPath, new { schema = "appsurface-keycloak-stage-progress-v1", deadlineSeconds = 300, stages = stageEvents.ToArray() }, CancellationToken.None); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    stageSubscription?.Dispose();
}

static KeycloakCaptureInputs ReadBindings(string path)
{
    var bindingsDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = document.RootElement;
    var inputs = root.GetProperty("localInputs").EnumerateArray().Select(item => new KeycloakLocalInputBinding(
        item.GetProperty("materialId").GetString()!,
        ResolveBindingPath(bindingsDirectory, item.GetProperty("localPath").GetString()!),
        item.GetProperty("sha256").GetString()!,
        item.GetProperty("maxBytes").GetInt64())).ToImmutableArray();
    return KeycloakCaptureInputs.Create(root.GetProperty("reviewedRecipeId").GetString()!, inputs);
}

static string ResolveBindingPath(string bindingsDirectory, string declaredPath)
{
    if (string.IsNullOrWhiteSpace(declaredPath)) throw new JsonException("Binding localPath is required.");
    return Path.GetFullPath(Path.IsPathRooted(declaredPath) ? declaredPath : Path.Combine(bindingsDirectory, declaredPath));
}

static async Task<DockerHostReceipt> ReadDockerHostReceiptAsync(CancellationToken token)
{
    var version = await RunDockerTextAsync(["version", "--format", "{{json .}}"], token);
    using var document = JsonDocument.Parse(version);
    var root = document.RootElement;
    var client = root.GetProperty("Client").GetProperty("Version").GetString()!;
    var server = root.GetProperty("Server").GetProperty("Version").GetString()!;
    var buildx = SanitizeHostToken(await RunDockerTextAsync(["buildx", "version"], token));
    return new("appsurface-keycloak-host-v1", DateTimeOffset.UtcNow, SanitizeHostToken(client), SanitizeHostToken(server), buildx);
}

static async Task<string> RunDockerTextAsync(IReadOnlyList<string> arguments, CancellationToken token)
{
    var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker host preflight could not start.");
    using var drain = CancellationTokenSource.CreateLinkedTokenSource(token);
    var combinedBytes = 0;
    var outputTask = ReadBoundedTextAsync(process.StandardOutput, 64 * 1024, bytes => Interlocked.Add(ref combinedBytes, bytes) <= 64 * 1024, drain.Cancel, drain.Token);
    var errorTask = ReadBoundedTextAsync(process.StandardError, 64 * 1024, bytes => Interlocked.Add(ref combinedBytes, bytes) <= 64 * 1024, drain.Cancel, drain.Token);
    try
    {
        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(token);
    }
    catch
    {
        drain.Cancel();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }
        throw;
    }
    if (process.ExitCode != 0) throw new InvalidOperationException("Docker host preflight returned a nonzero result.");
    return await outputTask;
}

static async Task<string> ReadBoundedTextAsync(StreamReader reader, int maximumBytes, Func<int, bool> reserveCombinedBytes, Action cancelSiblings, CancellationToken token)
{
    var output = new StringBuilder(Math.Min(maximumBytes, 4096));
    var buffer = new char[4096];
    var count = 0;
    while (true)
    {
        var read = await reader.ReadAsync(buffer, token);
        if (read == 0) return output.ToString();
        count = checked(count + Encoding.UTF8.GetByteCount(buffer, 0, read));
        if (count > maximumBytes || !reserveCombinedBytes(Encoding.UTF8.GetByteCount(buffer, 0, read)))
        {
            cancelSiblings();
            throw new InvalidDataException("Docker host preflight output exceeded its bound.");
        }
        output.Append(buffer, 0, read);
    }
}

static string SanitizeHostToken(string value)
{
    var first = value.Trim().Split('\n', 2)[0];
    var safe = new string(first.Take(128).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' ? c : '_').ToArray());
    return safe.Length == 0 ? "unknown" : safe;
}

static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
{
    var output = Path.GetFullPath(path);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
    var parent = Path.GetDirectoryName(output)!;
    Directory.CreateDirectory(parent);
    if (Path.Exists(output)) throw new IOException("JSON output already exists.");
    var temporary = Path.Combine(parent, ".stage-json-" + Guid.NewGuid().ToString("N"));
    try
    {
        await File.WriteAllBytesAsync(temporary, bytes, token);
        await using (var file = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.Read))
            file.Flush(true);
        token.ThrowIfCancellationRequested();
        AtomicFilePublication.Publish(temporary, output);
        temporary = string.Empty;
    }
    finally
    {
        if (temporary.Length != 0)
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

sealed record DockerHostReceipt(string Schema, DateTimeOffset ObservedAtUtc, string DockerClientVersion, string DockerServerVersion, string BuildxVersion);
