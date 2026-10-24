using System.Collections.Immutable;
using System.Diagnostics;
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

try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
    var token = deadline.Token;
    var recipeBytes = await File.ReadAllBytesAsync(recipePath);
    var policyBytes = await File.ReadAllBytesAsync(policyPath);
    var recipe = KeycloakCanonicalJson.ReadRecipe(recipeBytes);
    var policy = KeycloakCanonicalJson.ReadPolicy(policyBytes);
    var bindings = ReadBindings(bindingsPath);
    Console.WriteLine($"recipeSHA256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(KeycloakCanonicalJson.Write(recipe)))}");
    Console.WriteLine($"policySHA256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(KeycloakCanonicalJson.Write(policy)))}");

    using var plan = await KeycloakSecurity.CaptureAsync(recipe, policy, bindings, snapshotPath, token);
    Console.WriteLine($"captured={plan.Digest} platform={plan.Platform}");

    if (run)
    {
        var context = await KeycloakSecurity.MaterializeAsync(plan, contextPath, token);
        context.Verify(token);
        Console.WriteLine($"materialized={context.Digest} context={context.DirectoryPath}");
        var buildArguments = context.CreateDockerBuildArguments(hostImageReference, token);
        var start = new ProcessStartInfo("docker") { WorkingDirectory = context.DirectoryPath, UseShellExecute = false };
        foreach (var argument in buildArguments) start.ArgumentList.Add(argument);
        using var docker = Process.Start(start) ?? throw new InvalidOperationException("Docker process could not start.");
        try { await docker.WaitForExitAsync(token); }
        catch (OperationCanceledException) when (!docker.HasExited) { docker.Kill(entireProcessTree: true); throw; }
        if (docker.ExitCode != 0) return docker.ExitCode;
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

static KeycloakCaptureInputs ReadBindings(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = document.RootElement;
    var inputs = root.GetProperty("localInputs").EnumerateArray().Select(item => new KeycloakLocalInputBinding(
        item.GetProperty("materialId").GetString()!,
        item.GetProperty("localPath").GetString()!,
        item.GetProperty("sha256").GetString()!,
        item.GetProperty("maxBytes").GetInt64())).ToImmutableArray();
    return KeycloakCaptureInputs.Create(root.GetProperty("reviewedRecipeId").GetString()!, inputs);
}
