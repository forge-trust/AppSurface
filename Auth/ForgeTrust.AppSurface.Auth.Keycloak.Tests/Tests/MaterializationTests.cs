using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// testcard protects: snapshot lifecycle and offline materialization atomicity.
// fails_when: a snapshot accepts altered bytes, a disposed handle admits consumers, or invalid context input publishes output.
// why_new: these are the deterministic seams beneath public capture and Docker build preparation.
// seam: InternalsVisibleTo exposes SnapshotStore and ContextMaterialization without reflection.
// no_claim: this does not prove Docker execution or OCI graph export.
public sealed class MaterializationTests
{
    [Fact]
    public void OwnedContextCleanupDeletesDirectoriesAndToleratesExpectedFilesystemFailures()
    {
        var root = NewDirectory();
        try
        {
            var stage = Path.Combine(root, "owned-stage");
            Directory.CreateDirectory(Path.Combine(stage, "nested"));
            File.WriteAllText(Path.Combine(stage, "nested", "temporary"), "owned bytes");
            ContextMaterialization.TryDelete(stage);
            Assert.False(Directory.Exists(stage));
            ContextMaterialization.TryDelete(stage);
            var file = Path.Combine(root, "file");
            File.WriteAllText(file, "preserve");
            Assert.ThrowsAny<IOException>(() => Directory.Delete(file, true));
            ContextMaterialization.TryDelete(file);
            Assert.Equal("preserve", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OwnedContextCleanupPropagatesUnexpectedPathErrors()
    {
        Assert.Throws<ArgumentException>(() => ContextMaterialization.TryDelete("\0"));
    }

    [Fact]
    public void OwnedContextCleanupToleratesDirectoryAccessFailures()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewDirectory();
        var parent = Path.Combine(root, "readonly-parent");
        var stage = Path.Combine(parent, "owned-stage");
        Directory.CreateDirectory(stage);
        var mode = File.GetUnixFileMode(parent);
        try
        {
            File.SetUnixFileMode(parent, (UnixFileMode)365);
            var failure = Record.Exception(() => Directory.Delete(stage, true));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            ContextMaterialization.TryDelete(stage);
            Assert.True(Directory.Exists(stage));
        }
        finally
        {
            File.SetUnixFileMode(parent, mode);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/opt/keycloak/bin/kc.sh")]
    [InlineData("/opt/keycloak/bin/reviewed-entrypoint.sh")]
    public async Task PublicMaterializationUsesCapturedEntrypointBytesAtDefaultAndCustomPaths(string entrypoint)
    {
        using var fixture = TinyImageFixture.Create();
        var material = fixture.Recipe.MaterialReferences.Single(x => x.Id == "theme-input");
        var themeMaterial = fixture.Recipe.MaterialReferences.Single(x => x.Id == "theme-baseline");
        var recipe = fixture.Recipe with
        {
            EntrypointPath = entrypoint,
            ThemeInputs =
            [
                new KeycloakThemeInput("entrypoint", material.Sha256, null, null) { LocalInputId = material.Id, DestinationPath = entrypoint },
                new KeycloakThemeInput("theme", themeMaterial.Sha256, null, null) { LocalInputId = themeMaterial.Id, DestinationPath = "/opt/keycloak/themes/reviewed.txt" }
            ]
        };
        var bindings = fixture.Bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)) };
        using var plan = await KeycloakSecurity.CaptureAsync(recipe, fixture.Policy, bindings, Path.Combine(fixture.Root, "entrypoint-capture"));
        await File.WriteAllTextAsync(fixture.MaterialPaths[material.Id], "changed after capture");

        var context = await KeycloakSecurity.MaterializeAsync(plan, Path.Combine(fixture.Root, "entrypoint-context"));

        var target = TestPathUtils.PathUnder(context.DirectoryPath, "rootfs", entrypoint.TrimStart('/'));
        Assert.Equal("theme-input", await File.ReadAllTextAsync(target));
        Assert.Equal(material.Sha256, context.Result.ExpectedFinalFiles["rootfs" + entrypoint]);
        Assert.Equal(entrypoint, context.Result.Entrypoint);
        Assert.Equal("theme-baseline", await File.ReadAllTextAsync(Path.Combine(context.DirectoryPath, "rootfs", "opt", "keycloak", "themes", "reviewed.txt")));
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)493, File.GetUnixFileMode(target));
        context.Verify();
    }

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
        Assert.Equal(destination, result.ContextPath);
        Assert.Equal(HexFile(Path.Combine(destination, "context-manifest.json")), result.ContextDigest);
        Assert.Equal(result.ContextDigest, result.ManifestDigest);
        Assert.Null(result.RepairedCliSha256);
        Assert.Equal("/opt/keycloak/bin/kc.sh", result.Entrypoint);
        Assert.Equal(ContextPlatform.LinuxAmd64, result.Platform);
        var graphBytes = Directory.EnumerateFiles(input.Runtime.RootPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            + Directory.EnumerateFiles(input.Frontend.RootPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        Assert.Equal(37 + graphBytes, result.VerifiedBytes);
        Assert.Equal(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/')).Order(StringComparer.Ordinal), result.ExpectedFinalFiles.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.ExpectedFinalFiles, pair => Assert.Equal(HexFile(Path.Combine(destination, pair.Key)), pair.Value));
        Assert.Equal(Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/')).Order(StringComparer.Ordinal), result.FinalDirectories.Select(x => x.RelativePath).Order(StringComparer.Ordinal));
        if (!OperatingSystem.IsWindows()) foreach (var directory in result.FinalDirectories) Assert.Equal(File.GetUnixFileMode(TestPathUtils.PathUnder(destination, directory.RelativePath)), directory.Mode);
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
        Assert.True(File.Exists(TestPathUtils.PathUnder(context.ContextPath, "rootfs", path.TrimStart('/'))));
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

    // protects: prevalidation rejects unsupported source/count/package shapes before staging.
    // fails_when: an invalid field reaches copying or leaves a partial destination behind.
    // why_new: prior controls exercised duplicate names and aggregate exhaustion after copying.
    // seam: intentionally exposed bounded materializer with real tiny OCI graph inputs.
    // no_claim: the synthetic deb bytes are not a package installation or security proof.
    [Theory]
    [InlineData("file count ceiling", "Context bounds are unsupported.")]
    [InlineData("source root empty", "Source root is required.")]
    [InlineData("replacement digest", "Invalid replacement.")]
    [InlineData("deb nested name", "Invalid deb name.")]
    public async Task MaterializerRejectsIndependentClosedShapesBeforeCreatingStage(string mutation, string expected)
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "shape-input"));
        var limits = new ContextMaterializationLimits();
        switch (mutation)
        {
            case "file count ceiling": limits = limits with { MaxFiles = 65537 }; break;
            case "source root empty": input = input with { Source = input.Source with { RootPath = string.Empty } }; break;
            case "replacement digest": input = input with { Replacements = [input.Replacements[0] with { ReplacementSha256 = "invalid" }] }; break;
            case "deb nested name": input = input with { CapturedDebs = [input.CapturedDebs[0] with { Name = "nested/tiny.deb" }] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var destination = Path.Combine(fixture.Root, "invalid-shape-context");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(input, destination, limits: limits));
        Assert.Equal(expected, failure.Message);
        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "invalid-shape-context.owned-*"));
    }

    // protects: revalidation independently validates inventory declarations and actual modes.
    // fails_when: a caller supplies matching manifest bytes containing aliases/duplicates or changes a directory mode.
    // why_new: digest-tamper tests never reached these inventory and mode conditions.
    // seam: bounded internal revalidation; the edited manifest digest is explicit test input.
    // no_claim: accepting a new manifest hash is not authorization to replace a public build-context identity.
    [Theory]
    [InlineData("duplicate file", "Invalid or duplicate context file.")]
    [InlineData("unsafe file", "Invalid or duplicate context file.")]
    [InlineData("duplicate directory", "Invalid or duplicate context directory.")]
    [InlineData("unsafe directory", "Invalid or duplicate context directory.")]
    [InlineData("directory mode", "Context directory changed.")]
    public async Task ContextRevalidationRejectsMalformedInventoryAndChangedDirectoryModes(string mutation, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "inventory-input"));
        var result = await ContextMaterialization.MaterializeAsync(input, Path.Combine(fixture.Root, "inventory-context"));
        Assert.Equal(result.ManifestDigest, ContextMaterialization.Revalidate(result.ContextPath, result.ManifestDigest));
        var manifestPath = Path.Combine(result.ContextPath, "context-manifest.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!;
        var files = root["files"]!.AsArray();
        var directories = root["directories"]!.AsArray();
        switch (mutation)
        {
            case "duplicate file": files.Add(files[0]!.DeepClone()); break;
            case "unsafe file": files[0]!["path"] = "../outside"; break;
            case "duplicate directory": directories.Add(directories[0]!.DeepClone()); break;
            case "unsafe directory": directories[0]!["path"] = "../outside"; break;
            case "directory mode":
                var path = Path.Combine(result.ContextPath, directories[0]!["path"]!.GetValue<string>());
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) ^ UnixFileMode.GroupWrite);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        if (mutation != "directory mode") await File.WriteAllTextAsync(manifestPath, root.ToJsonString(), new UTF8Encoding(false));
        var failure = Assert.Throws<InvalidDataException>(() => ContextMaterialization.Revalidate(result.ContextPath, HexFile(manifestPath)));
        Assert.Equal(expected, failure.Message);
    }


    // protects: a finite caller deadline and each supported context platform remain usable.
    // fails_when: a valid nondefault deadline rejects or context metadata loses its requested platform.
    // why_new: prior positive materializer cases used the default deadline and amd64 selector only.
    // seam: bounded internal context materializer and real copied OCI graph bytes; no Docker.
    // no_claim: the tiny graph remains amd64; this verifies context selection, not arm64 runtime conformance.
    [Theory]
    [InlineData("arm64")]
    [InlineData("amd64")]
    public async Task ExplicitThirtySecondDeadlinePreservesTheSelectedContextPlatform(string architecture)
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "deadline-input"));
        var platform = architecture == "arm64" ? ContextPlatform.LinuxArm64 : ContextPlatform.LinuxAmd64;
        var limits = new ContextMaterializationLimits(Deadline: TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), limits.EffectiveDeadline);
        var result = await ContextMaterialization.MaterializeAsync(input with { Platform = platform },
            Path.Combine(fixture.Root, "deadline-context"), limits: limits);
        Assert.Equal(platform, result.Platform);
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(result.ContextPath, "context-manifest.json")));
        Assert.Equal(architecture == "arm64" ? "LinuxArm64" : "LinuxAmd64", manifest.RootElement.GetProperty("platform").GetString());
        Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(result.ContextPath, "rootfs", "opt", "keycloak", "lib", "server.jar")));
        Assert.Equal(result.ManifestDigest, ContextMaterialization.Revalidate(result.ContextPath, result.ManifestDigest));
    }

    // protects: both replacement hashes must be valid independently before any input is copied.
    // fails_when: a 64-character nonhex hash bypasses either original or replacement validation.
    // why_new: existing destination/short-hash controls did not reach each hexadecimal predicate.
    // seam: intentionally exposed materializer with valid sibling fields and graphs preserved.
    // no_claim: the replacement payload is synthetic data, not an installed Java library proof.
    [Theory]
    [InlineData("original")]
    [InlineData("replacement")]
    public async Task EachReplacementDigestRejectsNonhexCharactersBeforeStaging(string field)
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "digest-input"));
        var replacement = input.Replacements[0];
        var invalid = field switch
        {
            "original" => replacement with { OriginalSha256 = new string('g', 64) },
            "replacement" => replacement with { ReplacementSha256 = new string('g', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Equal("/opt/keycloak/lib/server.jar", invalid.Destination);
        Assert.Equal(field == "original" ? replacement.ReplacementSha256 : replacement.OriginalSha256,
            field == "original" ? invalid.ReplacementSha256 : invalid.OriginalSha256);
        var destination = Path.Combine(fixture.Root, "digest-context");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ContextMaterialization.MaterializeAsync(input with { Replacements = [invalid] }, destination));
        Assert.Equal("Invalid replacement.", failure.Message);
        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "digest-context.owned-*"));
    }

    // protects: missing rooted inputs reject while an existing relative input retains exact bytes.
    // fails_when: path syntax alone accepts an absent file or rejects a valid caller-owned relative file.
    // why_new: public capture always supplies absolute paths, leaving this internal copy boundary distinct.
    // seam: real files through ContextMaterialization; process working directory is never changed.
    // no_claim: relative paths here do not widen public capture's ownership or provenance authority.
    [Fact]
    public async Task MissingAbsoluteReplacementInputRejectsAndCleansPartialContext()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "missing-input"));
        var missing = Path.Combine(fixture.Root, "absent-replacement.bin");
        Assert.True(Path.IsPathRooted(missing));
        Assert.False(File.Exists(missing));
        var changed = input with { Replacements = [input.Replacements[0] with { ReplacementPrivatePath = missing }] };
        var destination = Path.Combine(fixture.Root, "missing-context");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(changed, destination));
        Assert.Equal("Input file missing.", failure.Message);
        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "missing-context.owned-*"));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(input.Source.RootPath, "lib", "server.jar")));
    }

    [Fact]
    public async Task ExistingRelativeReplacementInputCopiesExactBytesWithoutChangingWorkingDirectory()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "relative-input"));
        var workingDirectory = Environment.CurrentDirectory;
        var originalPath = input.Replacements[0].ReplacementPrivatePath;
        var relative = Path.GetRelativePath(workingDirectory, originalPath);
        Assert.False(Path.IsPathRooted(relative));
        Assert.True(File.Exists(relative));
        Assert.Equal(originalPath, Path.GetFullPath(relative));
        var changed = input with { Replacements = [input.Replacements[0] with { ReplacementPrivatePath = relative }] };
        var result = await ContextMaterialization.MaterializeAsync(changed, Path.Combine(fixture.Root, "relative-context"));
        var output = Path.Combine(result.ContextPath, "rootfs", "opt", "keycloak", "lib", "server.jar");
        Assert.Equal(await File.ReadAllBytesAsync(originalPath), await File.ReadAllBytesAsync(output));
        Assert.Equal(Hex("replacement"), HexFile(output));
        Assert.Equal(workingDirectory, Environment.CurrentDirectory);
        Assert.Equal(result.ManifestDigest, ContextMaterialization.Revalidate(result.ContextPath, result.ManifestDigest));
    }

    // protects: default CLI destination publishes truthful repaired bytes with absent or exact output pins.
    // fails_when: null destination/digest defaults skip repair, output differs, or source bytes are modified.
    // why_new: earlier tiny-archive materialization only proved the wrong-output-hash rejection.
    // seam: real tiny source/replacement ZIPs and OCI context files through the bounded materializer.
    // no_claim: ZIP entry checks do not prove real Keycloak CLI execution or image conformance.
    [Fact]
    public async Task DefaultCliDestinationRepairsArchiveWithOptionalAndExactOutputDigest()
    {
        using var fixture = TinyImageFixture.Create();
        var root = Path.Combine(fixture.Root, "default-cli-input");
        var input = CreateRichInput(fixture, root);
        const string relativeCli = "bin/client/keycloak-admin-cli-26.7.2.jar";
        var sourceJar = TestPathUtils.PathUnder(input.Source.RootPath, relativeCli);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceJar)!);
        var replacementJar = Path.Combine(root, "replacement.jar");
        WriteTinyJar(sourceJar, "1", "old");
        WriteTinyJar(replacementJar, "2", "new");
        var originalBytes = await File.ReadAllBytesAsync(sourceJar);
        var replacementBytes = await File.ReadAllBytesAsync(replacementJar);
        var sourceDigest = HexFile(sourceJar);
        var archive = new ArchiveTransformSpec(sourceJar, Path.Combine(root, "unused-output.jar"), sourceDigest,
            [new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", HexFile(replacementJar), ReplacementPath: replacementJar)], new());
        input = input with
        {
            Source = input.Source with
            {
                Files = [new ContextSourceFile(relativeCli, sourceDigest, originalBytes.LongLength, (UnixFileMode)420)],
                Directories = [new ContextSourceDirectory("bin", (UnixFileMode)493), new ContextSourceDirectory("bin/client", (UnixFileMode)493)]
            },
            Replacements = [],
            Archive = archive,
            ArchiveDestination = null,
            ExpectedRepairedCliSha256 = null
        };
        var first = await ContextMaterialization.MaterializeAsync(input, Path.Combine(fixture.Root, "optional-digest-context"));
        var repairedPath = TestPathUtils.PathUnder(first.ContextPath, "rootfs", "opt", "keycloak", relativeCli);
        var repairedBytes = await File.ReadAllBytesAsync(repairedPath);
        var repairedDigest = HexFile(repairedPath);
        Assert.Equal(repairedDigest, first.RepairedCliSha256);
        Assert.NotEqual(sourceDigest, repairedDigest);
        using (var repaired = ZipFile.OpenRead(repairedPath))
        {
            Assert.Equal(["META-INF/MANIFEST.MF", "META-INF/maven/com.example/jackson-core/pom.properties", "META-INF/services/example", "com/example/Main.class"],
                repaired.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
            using var manifest = new StreamReader(repaired.GetEntry("META-INF/MANIFEST.MF")!.Open());
            Assert.Equal("Manifest-Version: 1.0\r\n", await manifest.ReadToEndAsync());
            using var pom = new StreamReader(repaired.GetEntry("META-INF/maven/com.example/jackson-core/pom.properties")!.Open());
            Assert.Equal("groupId=com.example\nartifactId=jackson-core\nversion=2\n", await pom.ReadToEndAsync());
            using var payload = new StreamReader(repaired.GetEntry("com/example/Main.class")!.Open());
            Assert.Equal("new", await payload.ReadToEndAsync());
            using var service = new StreamReader(repaired.GetEntry("META-INF/services/example")!.Open());
            Assert.Equal("com.example.Main\n", await service.ReadToEndAsync());
        }
        var second = await ContextMaterialization.MaterializeAsync(input with { ExpectedRepairedCliSha256 = repairedDigest },
            Path.Combine(fixture.Root, "exact-digest-context"));
        Assert.Equal(repairedDigest, second.RepairedCliSha256);
        Assert.Equal(repairedBytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(second.ContextPath, "rootfs", "opt", "keycloak", relativeCli)));
        Assert.Equal(first.ContextDigest, second.ContextDigest);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourceJar));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(replacementJar));
        Assert.False(File.Exists(archive.OutputPath));
        Assert.Equal(first.ManifestDigest, ContextMaterialization.Revalidate(first.ContextPath, first.ManifestDigest));
        Assert.Equal(second.ManifestDigest, ContextMaterialization.Revalidate(second.ContextPath, second.ManifestDigest));
    }


    // protects: unsupported platforms and path roles reject before context publication.
    // fails_when: an unknown platform, non-Keycloak replacement, or traversal deb name is accepted.
    // why_new: v44 misses these rejecting alternatives after prior valid/short-hash controls.
    // seam: bounded internal materializer with real graph input and valid sibling fields.
    // no_claim: these declaration checks do not prove a runtime or install synthetic deb bytes.
    [Theory]
    [InlineData("platform", "Unsupported platform.")]
    [InlineData("replacement namespace", "Invalid replacement.")]
    [InlineData("deb traversal", "Invalid deb name.")]
    public async Task MaterializerRejectsUnsupportedPlatformAndPathRolesBeforeStaging(string mutation, string expected)
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "role-input"));
        input = mutation switch
        {
            "platform" => input with { Platform = (ContextPlatform)999 },
            "replacement namespace" => input with { Replacements = [input.Replacements[0] with { Destination = "/usr/lib/server.jar" }] },
            "deb traversal" => input with { CapturedDebs = [input.CapturedDebs[0] with { Name = "../tiny.deb" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var destination = Path.Combine(fixture.Root, "role-context");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(input, destination));
        Assert.Equal(expected, failure.Message);
        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "role-context.owned-*"));
    }

    // protects: an absent context cannot be published through a linked parent into another directory.
    // fails_when: prepublication path checks follow a directory link or alter its prior contents.
    // why_new: v44 contains a remaining filesystem-link guard, distinct from inventory tampering.
    // seam: local directory symlink and bounded materializer; no reflection or global path changes.
    // no_claim: this does not prove behavior on unsupported filesystems.
    [Fact]
    public async Task MaterializerRejectsLinkedOutputParentAndPreservesItsTarget()
    {
        using var fixture = TinyImageFixture.Create();
        var input = CreateRichInput(fixture, Path.Combine(fixture.Root, "linked-input"));
        var target = Path.Combine(fixture.Root, "prior-output-parent");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "prior.txt");
        await File.WriteAllTextAsync(sentinel, "preserve", new UTF8Encoding(false));
        var linked = Path.Combine(fixture.Root, "linked-parent");
        Directory.CreateSymbolicLink(linked, target);
        var destination = Path.Combine(linked, "context");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ContextMaterialization.MaterializeAsync(input, destination));
        Assert.Equal("Context path contains a link.", failure.Message);
        Assert.False(Path.Exists(Path.Combine(target, "context")));
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(sentinel, Assert.Single(Directory.EnumerateFileSystemEntries(target)));
        Assert.Equal(target, new DirectoryInfo(linked).LinkTarget);
    }

}
