using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// testcard protects: snapshot lifecycle and offline materialization atomicity.
// fails_when: a snapshot accepts altered bytes, a disposed handle admits consumers, or invalid context input publishes output.
// why_new: these are the deterministic seams beneath public capture and Docker build preparation.
// seam: InternalsVisibleTo exposes SnapshotStore and ContextMaterialization without reflection.
// no_claim: this does not prove Docker execution or OCI graph export.
public sealed class MaterializationTests
{
    [Fact]
    public async Task MaterializationRejectsUnexpectedRepairedArchiveDigestBeforePublishing()
    {
        using var fixture = TinyImageFixture.Create();
        var root = Path.Combine(fixture.Root, "archive-input");
        var input = CreateRichInput(fixture, root);
        var sourceJar = Path.Combine(root, "source", "lib", "server.jar");
        var replacementJar = Path.Combine(root, "replacement.jar");
        WriteTinyJar(sourceJar, "1", "old");
        WriteTinyJar(replacementJar, "2", "new");
        var sourceLength = new FileInfo(sourceJar).Length;
        var sourceFiles = input.Source.Files.SetItem(0, new ContextSourceFile("lib/server.jar", HexFile(sourceJar), sourceLength, (UnixFileMode)420));
        var archive = new ArchiveTransformSpec(sourceJar, Path.Combine(root, "repaired.jar"), HexFile(sourceJar),
            [new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", HexFile(replacementJar), ReplacementPath: replacementJar)], new());
        var invalid = input with { Source = input.Source with { Files = sourceFiles }, Replacements = [], Archive = archive, ArchiveDestination = "/opt/keycloak/lib/server.jar", ExpectedRepairedCliSha256 = new string('0', 64) };
        var destination = Path.Combine(fixture.Root, "archive-context");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(invalid, destination));
        Assert.Equal("Repaired archive differs from its declared output digest.", exception.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task PublicMaterializationUsesRecipeOutputCeilingAndLeavesDestinationAbsent()
    {
        using var fixture = TinyImageFixture.Create();
        var destination = Path.Combine(fixture.Root, "output-ceiling");
        var capturePath = Path.Combine(fixture.Root, "output-ceiling-capture");
        var recipe = fixture.Recipe with { ResourceLimits = fixture.Recipe.ResourceLimits with { MaxOutputBytes = 1 } };
        var bindings = fixture.Bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)) };
        using var plan = await KeycloakSecurity.CaptureAsync(recipe, fixture.Policy, bindings, capturePath);
        await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.MaterializeAsync(plan, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task MaterializationCopiesBoundedSourceReplacementThemeEntrypointDebAndRealOciGraphs()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "rich-input"));
        var destination = Path.Combine(fixture.Root, "rich-context");

        var result = await ContextMaterialization.MaterializeAsync(input, destination);

        Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(destination, "rootfs", "opt", "keycloak", "lib", "server.jar")));
        Assert.Equal("theme", await File.ReadAllTextAsync(Path.Combine(destination, "rootfs", "opt", "keycloak", "themes", "keycloak.v2", "login", "login.ftl")));
        Assert.Equal("entrypoint", await File.ReadAllTextAsync(Path.Combine(destination, "rootfs", "opt", "keycloak", "bin", "kc.sh")));
        Assert.Equal("deb", await File.ReadAllTextAsync(Path.Combine(destination, "debs", "tiny.deb")));
        Assert.Equal(1, result.FileCount);
        Assert.True(File.Exists(Path.Combine(destination, "inputs", "oci", "runtime", "index.json")));
        Assert.True(File.Exists(Path.Combine(destination, "inputs", "oci", "frontend", "index.json")));
    }

    [Fact]
    public async Task CapturedHostEntrypointUsesCanonicalPathAndJsonArgumentEncoding()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "host-entrypoint"));
        var path = "/opt/keycloak/bin/neutral-\"entrypoint.sh";
        var changed = input with { Entrypoint = input.Entrypoint! with { ImagePath = path }, ThemeOnly = false };
        var context = await ContextMaterialization.MaterializeAsync(changed, Path.Combine(fixture.Root, "neutral-context"));
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(context.ContextPath, "Dockerfile"));
        Assert.Contains("ENTRYPOINT " + System.Text.Json.JsonSerializer.Serialize(new[] { path }), dockerfile, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(context.ContextPath, "rootfs", path.TrimStart('/'))));
        await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(input with { Entrypoint = input.Entrypoint! with { ImagePath = "/usr/bin/escape" } }, Path.Combine(fixture.Root, "invalid-entrypoint")));
    }

    [Fact]
    public async Task MaterializationProducesDeterministicManifestAndRejectsTampering()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "deterministic-input"));
        var first = Path.Combine(fixture.Root, "context-one");
        var second = Path.Combine(fixture.Root, "context-two");
        await ContextMaterialization.MaterializeAsync(input, first);
        await ContextMaterialization.MaterializeAsync(input, second);

        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(first, "context-manifest.json")), await File.ReadAllBytesAsync(Path.Combine(second, "context-manifest.json")));
        var manifest = await File.ReadAllBytesAsync(Path.Combine(first, "context-manifest.json"));
        File.AppendAllText(Path.Combine(first, "Dockerfile"), "tampered", new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => ContextMaterialization.Revalidate(first, Convert.ToHexStringLower(SHA256.HashData(manifest))));
    }

    [Fact]
    public async Task MaterializationRejectsFileAndAggregateLimitsBeforePublishing()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "limit-input"));
        var tooFewFiles = Path.Combine(fixture.Root, "too-few-files");
        var duplicateSource = input with { Source = input.Source with { Files = [input.Source.Files[0], new ContextSourceFile("second.txt", Hex("second"), 6, (UnixFileMode)420)] } };
        File.WriteAllText(Path.Combine(input.Source.RootPath, "second.txt"), "second", new UTF8Encoding(false));
        await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(duplicateSource, tooFewFiles, limits: new ContextMaterializationLimits(MaxFiles: 1)));
        Assert.False(Directory.Exists(tooFewFiles));
        var tooMuchData = Path.Combine(fixture.Root, "too-much-data");
        await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(input, tooMuchData, limits: new ContextMaterializationLimits(MaxAggregateBytes: 1)));
        Assert.False(Directory.Exists(tooMuchData));
    }

    [Fact]
    public async Task MaterializationRejectsDuplicateSourceAndDestinationCollisions()
    {
        using var fixture = TinyImageFixture.Create();
        var root = Path.Combine(fixture.Root, "collision-input");
        var input = CreateRichInput(fixture, root);
        var duplicate = input with { Source = input.Source with { Files = [input.Source.Files[0], input.Source.Files[0]] } };
        await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(duplicate, Path.Combine(fixture.Root, "duplicate")));
        File.WriteAllText(Path.Combine(root, "theme"), "theme", new UTF8Encoding(false));
        var collision = input with { Themes = [new ContextThemeInput("/opt/keycloak/lib/server.jar", Path.Combine(root, "theme"), Hex("theme"), (UnixFileMode)420)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(collision, Path.Combine(fixture.Root, "collision")));
    }

    [Fact]
    public async Task SnapshotCaptureAndAcceptedCopyRemainDigestBound()
    {
        var root = NewDirectory();
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllTextAsync(source, "captured", new UTF8Encoding(false));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("captured"))).ToLowerInvariant();
        var snapshotPath = Path.Combine(root, "snapshot");
        var snapshot = await SnapshotStore.CaptureAsync([new CaptureFile("material", source, digest, 8, 420)], new string('a', 64), new string('b', 64), snapshotPath, CancellationToken.None);
        try
        {
            Assert.Equal(new string('a', 64), snapshot.RecipeDigest);
            Assert.Equal(new string('b', 64), snapshot.PolicyDigest);
            Assert.Single(snapshot.Files);
            var copyPath = Path.Combine(root, "accepted-copy");
            var copied = await SnapshotStore.CopyForUseAsync(snapshot, copyPath, CancellationToken.None);
            Assert.Equal("captured", await File.ReadAllTextAsync(Path.Combine(copied, "material")));
            Assert.True(File.Exists(Path.Combine(copied, "snapshot.json")));
        }
        finally { snapshot.Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SnapshotDisposalStopsNewConsumersButPreservesCallerDirectory()
    {
        var root = NewDirectory();
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllTextAsync(source, "captured", new UTF8Encoding(false));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("captured"))).ToLowerInvariant();
        var snapshotPath = Path.Combine(root, "snapshot");
        var snapshot = await SnapshotStore.CaptureAsync([new CaptureFile("material", source, digest, 8, 420)], new string('a', 64), new string('b', 64), snapshotPath, CancellationToken.None);
        snapshot.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => SnapshotStore.CopyForUseAsync(snapshot, Path.Combine(root, "copy"), CancellationToken.None));
        Assert.True(Directory.Exists(snapshotPath));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task MaterializationRejectsInvalidOfflineContextWithoutPublishingDestination()
    {
        var root = NewDirectory();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var destination = Path.Combine(root, "context");
        var input = new ContextMaterializationInput(
            new ContextSourceRoot(sourceRoot, [new ContextSourceFile("../escape", new string('a', 64), 1, (UnixFileMode)420)], []),
            new ContextOciLayout("runtime", Path.Combine(root, "runtime"), "sha256:" + new string('a', 64), new string('b', 64)),
            new ContextOciLayout("frontend", Path.Combine(root, "frontend"), "sha256:" + new string('a', 64), new string('b', 64)),
            [], [], null, [], null, ContextPlatform.LinuxAmd64);

        await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(input, destination));
        Assert.False(Directory.Exists(destination));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task MaterializationCancellationLeavesAbsentDestination()
    {
        var root = NewDirectory();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "kc.sh");
        await File.WriteAllTextAsync(source, "entrypoint", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(source, (UnixFileMode)420);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("entrypoint"))).ToLowerInvariant();
        var runtimeRoot = Path.Combine(root, "runtime");
        var frontendRoot = Path.Combine(root, "frontend");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(frontendRoot);
        await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "index.json"), "manifest", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(frontendRoot, "index.json"), "manifest", new UTF8Encoding(false));
        var input = new ContextMaterializationInput(
            new ContextSourceRoot(sourceRoot, [new ContextSourceFile("kc.sh", digest, 10, (UnixFileMode)420)], []),
            new ContextOciLayout("runtime", runtimeRoot, "manifest", ContextMaterialization.ComputeLayoutDigest(runtimeRoot)),
            new ContextOciLayout("frontend", frontendRoot, "manifest", ContextMaterialization.ComputeLayoutDigest(frontendRoot)),
            [], [], null, [], null, ContextPlatform.LinuxAmd64);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var destination = Path.Combine(root, "context");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContextMaterialization.MaterializeAsync(input, destination, cancellation.Token));
        Assert.False(Directory.Exists(destination));
        Directory.Delete(root, true);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("long")]
    [InlineData("option")]
    [InlineData("space")]
    [InlineData("digest")]
    public async Task BuildArgumentsRejectInvalidTagsBeforeReturningHostCommands(string shape)
    {
        using var fixture = TinyImageFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "capture"));
        var context = await KeycloakSecurity.MaterializeAsync(plan, Path.Combine(fixture.Root, "context"));
        var tag = shape switch { "empty" => " ", "long" => new string('a', 129), "option" => "--push", "space" => "bad tag", _ => "image@sha256:abc" };
        var failure = Assert.Throws<KeycloakSecurityException>(() => context.CreateDockerBuildArguments(tag));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Build, failure.Phase);
        Assert.Equal(KeycloakEffectState.Committed, failure.Effect);
        Assert.Equal(context.Result.ContextDigest, context.Digest);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(KeycloakFailureCode.Cancelled, Assert.Throws<KeycloakSecurityException>(() => context.Verify(cancelled.Token)).Code);
    }

    [Fact]
    public async Task PublicMaterializationRejectsNullDisposedAndCancelledPlansWithoutPublishing()
    {
        using var fixture = TinyImageFixture.Create();
        var absent = Path.Combine(fixture.Root, "absent-context");
        var missing = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.MaterializeAsync(null!, absent));
        Assert.Equal(KeycloakSafePhase.Materialization, missing.Phase);
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "captured"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(KeycloakFailureCode.Cancelled, (await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.MaterializeAsync(plan, absent, cancellation.Token))).Code);
        plan.Dispose();
        Assert.Equal(KeycloakFailureCode.CaptureInvalid, (await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.MaterializeAsync(plan, absent))).Code);
        Assert.False(Directory.Exists(absent));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("extra-binding")]
    [InlineData("destination")]
    [InlineData("wrong-reviewed")]
    [InlineData("policy")]
    public async Task PublicCaptureLeavesNoSnapshotForRejectedBoundMaterial(string mutation)
    {
        using var fixture = TinyImageFixture.Create();
        var recipe = fixture.Recipe; var policy = fixture.Policy; var bindings = fixture.Bindings;
        var destination = Path.Combine(fixture.Root, "rejected-capture");
        switch (mutation)
        {
            case "missing-file": File.Delete(fixture.ArchivePath); break;
            case "length": File.AppendAllText(fixture.ArchivePath, "extra"); break;
            case "hash": var bytes = await File.ReadAllBytesAsync(fixture.ArchivePath); bytes[0] ^= 1; await File.WriteAllBytesAsync(fixture.ArchivePath, bytes); break;
            case "extra-binding": bindings = bindings with { LocalInputs = bindings.LocalInputs.Add(new("undeclared", fixture.ArchivePath, new string('a', 64), 1)) }; break;
            case "destination": Directory.CreateDirectory(destination); break;
            case "wrong-reviewed": bindings = bindings with { ReviewedRecipeId = new string('0', 64) }; break;
            default: policy = policy with { MaterialSha256ByIdentity = policy.MaterialSha256ByIdentity.Remove(recipe.MaterialReferences[0].Id) }; break;
        }
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(recipe, policy, bindings, destination));
        Assert.Equal(KeycloakSafePhase.Capture, failure.Phase);
        if (mutation == "destination") Assert.Empty(Directory.EnumerateFileSystemEntries(destination)); else Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".capture-inputs-*"));
    }

    [Theory]
    [InlineData("max-file-zero")]
    [InlineData("max-file-large")]
    [InlineData("aggregate")]
    [InlineData("count-zero")]
    [InlineData("count-large")]
    [InlineData("deadline-zero")]
    [InlineData("deadline-large")]
    [InlineData("output-zero")]
    [InlineData("output-large")]
    [InlineData("redirect-negative")]
    [InlineData("redirect-large")]
    [InlineData("argument-zero")]
    [InlineData("argument-large")]
    [InlineData("argument-bytes-zero")]
    [InlineData("argument-bytes-large")]
    [InlineData("cleanup-zero")]
    [InlineData("cleanup-large")]
    public async Task SnapshotRejectsInvalidResourceEnvelopeBeforeCreatingDestination(string shape)
    {
        using var fixture = TinyImageFixture.Create();
        var limits = new InvocationLimits();
        limits = shape switch
        {
            "max-file-zero" => limits with { MaxFileBytes = 0 },
            "max-file-large" => limits with { MaxFileBytes = 1024L * 1024 * 1024 + 1 },
            "aggregate" => limits with { MaxAggregateBytes = 1 },
            "count-zero" => limits with { MaxFileCount = 0 },
            "count-large" => limits with { MaxFileCount = 65537 },
            "deadline-zero" => limits with { Deadline = TimeSpan.Zero },
            "deadline-large" => limits with { Deadline = TimeSpan.FromSeconds(301) },
            "output-zero" => limits with { MaxProcessOutputBytes = 0 },
            "output-large" => limits with { MaxProcessOutputBytes = 1024 * 1024 + 1 },
            "redirect-negative" => limits with { MaxRedirects = -1 },
            "redirect-large" => limits with { MaxRedirects = 33 },
            "argument-zero" => limits with { MaxArgumentCount = 0 },
            "argument-large" => limits with { MaxArgumentCount = 4097 },
            "argument-bytes-zero" => limits with { MaxArgumentBytes = 0 },
            "argument-bytes-large" => limits with { MaxArgumentBytes = 1024 * 1024 + 1 },
            "cleanup-zero" => limits with { CleanupDeadline = TimeSpan.Zero },
            _ => limits with { CleanupDeadline = TimeSpan.FromSeconds(31) }
        };
        var destination = Path.Combine(fixture.Root, "invalid-budget");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SnapshotStore.CaptureAsync([], new string('a', 64), new string('b', 64), destination, default, limits));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".capture-*"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    [InlineData("negative-length")]
    [InlineData("oversize")]
    [InlineData("aggregate")]
    [InlineData("short-digest")]
    [InlineData("unsafe-id")]
    [InlineData("dot-id")]
    [InlineData("long-id")]
    public async Task SnapshotRejectsInvalidInventoryBeforeCopying(string shape)
    {
        using var fixture = TinyImageFixture.Create();
        var source = Path.Combine(fixture.Root, "one-byte"); File.WriteAllText(source, "x");
        var input = new CaptureFile("one", source, Hex("x"), 1, 420);
        CaptureFile[] files = [input];
        var limits = new InvocationLimits(MaxFileBytes: 1, MaxAggregateBytes: 1, MaxFileCount: 1);
        switch (shape)
        {
            case "none": files = []; break;
            case "too-many": files = [input, input with { Id = "two" }]; break;
            case "duplicate": files = [input, input]; limits = limits with { MaxFileCount = 2, MaxAggregateBytes = 2 }; break;
            case "negative-length": files = [input with { Length = -1 }]; break;
            case "oversize": files = [input with { Length = 2 }]; break;
            case "aggregate": files = [input, input with { Id = "two" }]; limits = limits with { MaxFileCount = 2 }; break;
            case "short-digest": files = [input with { Sha256 = "ab" }]; break;
            case "unsafe-id": files = [input with { Id = "a/b" }]; break;
            case "dot-id": files = [input with { Id = "." }]; break;
            default: files = [input with { Id = new string('x', 129) }]; break;
        }
        var destination = Path.Combine(fixture.Root, "invalid-inventory");
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CaptureAsync(files, new string('a', 64), new string('b', 64), destination, default, limits));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task SnapshotRejectsSameLengthManifestMutationAndMissingMaterial()
    {
        using var fixture = TinyImageFixture.Create();
        var source = Path.Combine(fixture.Root, "one-byte"); File.WriteAllText(source, "x");
        var destination = Path.Combine(fixture.Root, "snapshot");
        using var snapshot = await SnapshotStore.CaptureAsync([new CaptureFile("one", source, Hex("x"), 1, 420)], new string('a', 64), new string('b', 64), destination, default);
        var manifestPath = Path.Combine(destination, "snapshot.json");
        var original = await File.ReadAllBytesAsync(manifestPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var mutation = (byte[])original.Clone(); mutation[0] = (byte)'['; await File.WriteAllBytesAsync(manifestPath, mutation);
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CopyForUseAsync(snapshot, Path.Combine(fixture.Root, "rejected-manifest"), default));
        await File.WriteAllBytesAsync(manifestPath, original);
        File.Delete(Path.Combine(destination, "one"));
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CopyForUseAsync(snapshot, Path.Combine(fixture.Root, "rejected-inventory"), default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "rejected-manifest")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "rejected-inventory")));
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "keycloak-materialization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ContextMaterializationInput CreateRichInput(TinyImageFixture fixture, string root)
    {
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(sourceRoot, "lib"));
        var replacementPath = Path.Combine(root, "replacement.bin"); var themePath = Path.Combine(root, "theme.ftl"); var entrypointPath = Path.Combine(root, "entrypoint.sh"); var debPath = Path.Combine(root, "tiny.deb");
        File.WriteAllText(Path.Combine(sourceRoot, "lib", "server.jar"), "original", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(sourceRoot, "lib", "server.jar"), (UnixFileMode)420);
        File.WriteAllText(replacementPath, "replacement", new UTF8Encoding(false)); File.WriteAllText(themePath, "theme", new UTF8Encoding(false)); File.WriteAllText(entrypointPath, "entrypoint", new UTF8Encoding(false)); File.WriteAllText(debPath, "deb", new UTF8Encoding(false));
        var runtime = Path.Combine(root, "runtime"); var frontend = Path.Combine(root, "frontend");
        OciLayout.CopyVerifiedGraph(fixture.ArchivePath, fixture.Image.PlatformManifest, fixture.Image.ConfigDigest!, runtime, CancellationToken.None);
        OciLayout.CopyVerifiedGraph(fixture.ArchivePath, fixture.Image.PlatformManifest, fixture.Image.ConfigDigest!, frontend, CancellationToken.None);
        return new(
            new ContextSourceRoot(sourceRoot, [new ContextSourceFile("lib/server.jar", Hex("original"), 8, (UnixFileMode)420)], [new ContextSourceDirectory("lib", (UnixFileMode)493)]),
            new ContextOciLayout("runtime", runtime, fixture.Image.PlatformManifest, ContextMaterialization.ComputeLayoutDigest(runtime)),
            new ContextOciLayout("frontend", frontend, fixture.Image.PlatformManifest, ContextMaterialization.ComputeLayoutDigest(frontend)),
            [new ContextDeb("tiny.deb", debPath, Hex("deb"))],
            [new ContextReplacement("/opt/keycloak/lib/server.jar", Hex("original"), replacementPath, Hex("replacement"))], null,
            [new ContextThemeInput("/opt/keycloak/themes/keycloak.v2/login/login.ftl", themePath, Hex("theme"), (UnixFileMode)420)],
            new ContextHostInput("/opt/keycloak/bin/kc.sh", entrypointPath, Hex("entrypoint"), (UnixFileMode)493), ContextPlatform.LinuxAmd64, "26.7.2", null, true);
    }

    private static string Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HexFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WriteTinyJar(string path, string version, string payload)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false), leaveOpen: false)) manifest.Write("Manifest-Version: 1.0\r\n");
        using (var pom = new StreamWriter(zip.CreateEntry("META-INF/maven/com.example/jackson-core/pom.properties").Open(), new UTF8Encoding(false), leaveOpen: false)) pom.Write($"groupId=com.example\nartifactId=jackson-core\nversion={version}\n");
        using (var main = new StreamWriter(zip.CreateEntry("com/example/Main.class").Open(), new UTF8Encoding(false), leaveOpen: false)) main.Write(payload);
        using (var service = new StreamWriter(zip.CreateEntry("META-INF/services/example").Open(), new UTF8Encoding(false), leaveOpen: false)) service.Write("com.example.Main\n");
    }
}
