using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

// Ordinary, non-friend consumer. The generated project references only the extracted pure-library DLL.
internal static class PublicCaptureProbe
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3) return 64;
        try
        {
            return await ExecuteAsync(args[0], args[1], args[2]);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(args[1], "result.json"), JsonSerializer.Serialize(new
            {
                status = "probe-error",
                type = error.GetType().Name,
                missingDependency = error is FileNotFoundException missing
                    && missing.FileName?.Split(',')[0] == "ForgeTrust.AppSurface.Auth.Keycloak"
                        ? "ForgeTrust.AppSurface.Auth.Keycloak" : null
            }));
            return 70;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> ExecuteAsync(string fixtureRoot, string work, string scenario)
    {
        var clock = Stopwatch.StartNew();
        var events = new List<KeycloakStageEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var signalReceived = false;
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Volatile.Write(ref signalReceived, true);
            cancellation.Cancel();
        });
        using var subscription = KeycloakStageEvents.Subscribe(value =>
        {
            events.Add(value);
            if (scenario == "signal-cancel" && value is { Phase: KeycloakStagePhase.Capture, Outcome: KeycloakStageOutcome.Started })
            {
                // Public progress-observer barrier: cancellation before acquisition, not a mid-copy claim.
                File.WriteAllText(Path.Combine(work, "capture-started"), "started");
                cancellation.Token.WaitHandle.WaitOne();
            }
        });
        var recipe = KeycloakCanonicalJson.ReadRecipe(File.ReadAllBytes(Path.Combine(fixtureRoot, "recipe.json")));
        var policy = KeycloakCanonicalJson.ReadPolicy(File.ReadAllBytes(Path.Combine(fixtureRoot, "policy.json")));
        var recipeDigest = Hash(KeycloakCanonicalJson.Write(recipe));
        var policyDigest = Hash(KeycloakCanonicalJson.Write(policy));
        using var description = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "bindings.json")));
        var bindings = description.RootElement.EnumerateArray().Select(value => new KeycloakLocalInputBinding(
            value.GetProperty("id").GetString()!,
            Path.Combine(fixtureRoot, value.GetProperty("file").GetString()!),
            value.GetProperty("sha256").GetString()!, value.GetProperty("maxBytes").GetInt64())).ToArray();
        var destination = Path.Combine(work, "snapshot");
        if (scenario == "wrong-review") recipeDigest = new string('0', 64);
        if (scenario == "policy-denied") policy = policy with { MaterialSha256ByIdentity = policy.MaterialSha256ByIdentity.Remove("openssl") };
        if (scenario == "pre-cancel") cancellation.Cancel();
        if (scenario == "existing-destination") Directory.CreateDirectory(destination);
        if (scenario is "valid" or "outer-tamper" or "repeat")
        {
            var local = Path.Combine(work, "bound-inputs");
            Directory.CreateDirectory(local);
            bindings = bindings.Select(binding =>
            {
                var copy = Path.Combine(local, binding.MaterialId);
                File.Copy(binding.LocalPath, copy);
                return binding with { LocalPath = copy };
            }).ToArray();
            if (scenario == "outer-tamper")
            {
                var boundPath = bindings.Single(binding => binding.MaterialId == "openssl").LocalPath;
                var changed = File.ReadAllBytes(boundPath);
                changed[^1] ^= 1;
                File.WriteAllBytes(boundPath, changed);
            }
        }
        var inputs = KeycloakCaptureInputs.Create(recipeDigest, bindings);
        try
        {
            using var plan = await KeycloakSecurity.CaptureAsync(recipe, policy, inputs, destination, cancellation.Token);
            Require(plan.RecipeDigest == recipeDigest && plan.PolicyDigest == policyDigest, "plan-identities");
            Require(plan.Mode == KeycloakRecipeMode.Security && plan.Platform == "linux/amd64", "plan-scope");
            VerifySnapshot(destination, bindings, plan.Digest);
            var digest = plan.Digest;
            string? repeatedDigest = null;
            var independentCopy = false;
            if (scenario == "valid")
            {
                var bound = bindings.Single(binding => binding.MaterialId == "openssl");
                File.AppendAllText(bound.LocalPath, "changed-after-capture");
                independentCopy = Hash(File.ReadAllBytes(Path.Combine(destination, bound.MaterialId))) == bound.Sha256
                    && Hash(File.ReadAllBytes(bound.LocalPath)) != bound.Sha256;
                Require(independentCopy, "capture-independent-copy");
            }
            if (scenario == "repeat")
            {
                var secondBindings = bindings.Select(binding => binding with
                {
                    LocalPath = Path.Combine(fixtureRoot, description.RootElement.EnumerateArray()
                        .Single(value => value.GetProperty("id").GetString() == binding.MaterialId).GetProperty("file").GetString()!)
                });
                using var second = await KeycloakSecurity.CaptureAsync(recipe, policy,
                    KeycloakCaptureInputs.Create(recipeDigest, secondBindings), Path.Combine(work, "snapshot-repeat"), cancellation.Token);
                repeatedDigest = second.Digest;
                Require(repeatedDigest == digest, "path-independent-plan-digest");
                VerifySnapshot(Path.Combine(work, "snapshot-repeat"), secondBindings.ToArray(), second.Digest);
            }
            plan.Dispose();
            Require(File.Exists(Path.Combine(destination, "snapshot.json")), "dispose-preserves-caller-snapshot");
            var result = new
            {
                status = "captured",
                recipeDigest,
                policyDigest,
                planDigest = digest,
                repeatedDigest,
                mode = plan.Mode.ToString(),
                platform = plan.Platform,
                materialCount = bindings.Length,
                independentCopy,
                snapshotRetainedAfterDispose = true,
                signalReceived,
                events = Events(events),
                elapsedMilliseconds = clock.ElapsedMilliseconds,
                assemblies = Assemblies(),
                directReferences = DirectReferences()
            };
            File.WriteAllText(Path.Combine(work, "result.json"), JsonSerializer.Serialize(result));
            return 0;
        }
        catch (KeycloakSecurityException error)
        {
            File.WriteAllText(Path.Combine(work, "result.json"), JsonSerializer.Serialize(new
            {
                status = "rejected",
                code = error.Code.ToString(),
                phase = error.Phase.ToString(),
                effect = error.Effect.ToString(),
                signalReceived,
                recovery = error.Recovery.ToString(),
                snapshotExists = Directory.Exists(destination),
                committedSnapshot = File.Exists(Path.Combine(destination, "snapshot.json")),
                elapsedMilliseconds = clock.ElapsedMilliseconds,
                events = Events(events),
                assemblies = Assemblies(),
                directReferences = DirectReferences()
            }));
            return 20;
        }
    }

    private static object[] Events(List<KeycloakStageEvent> events) => events.Select(value => (object)new
    {
        phase = value.Phase.ToString(),
        outcome = value.Outcome.ToString(),
        effect = value.Effect.ToString(),
        correlation = value.CorrelationId,
        materialCount = value.MaterialCount,
        code = value.SafeCode,
        recipeDigest = value.RecipeDigest,
        policyDigest = value.PolicyDigest,
        planDigest = value.PlanDigest
    }).ToArray();

    private static object[] Assemblies() => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
        .Select(assembly => (object)new { name = assembly.GetName().Name, location = assembly.Location }).ToArray();

    private static string?[] DirectReferences() => typeof(PublicCaptureProbe).Assembly.GetReferencedAssemblies()
        .Select(assembly => assembly.Name).ToArray();

    private static void VerifySnapshot(string path, KeycloakLocalInputBinding[] bindings, string digest)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Require(Hash(File.ReadAllBytes(Path.Combine(path, "snapshot.json"))) == digest, "snapshot-manifest-digest");
        foreach (var binding in bindings)
        {
            var captured = Path.Combine(path, binding.MaterialId);
            Require(new FileInfo(captured).Length <= binding.MaxBytes && Hash(File.ReadAllBytes(captured)) == binding.Sha256, "captured-material");
            Require((File.GetUnixFileMode(captured) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                "captured-material-readonly");
        }
    }

    private static void Require(bool condition, string invariant)
    {
        if (!condition) throw new InvalidOperationException(invariant);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
