using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakSupervisedApiTests
{
    [Theory]
    [InlineData("anchor")]
    [InlineData("server")]
    [InlineData("worker")]
    [InlineData("workloads")]
    public void WorkloadPrivateMountLivesInADedicatedDirectory(string name)
    {
        using var run = new TempDirectory();
        var workload = SupervisedKeycloakRunLayout.WorkloadPrivate(name);
        var workloadPath = workload.HostPath(run.Path);

        Assert.Equal(TestPathUtils.PathUnder(run.Path, "private-workloads", name), workloadPath);
        Assert.Equal("/proof/private", workload.ContainerDirectory);
        foreach (var reserved in new[]
                 {
                     SupervisedKeycloakRunLayout.AnchorPrivate,
                     SupervisedKeycloakRunLayout.ServerPrivate,
                     SupervisedKeycloakRunLayout.WorkerPrivate
                 })
        {
            var reservedPath = reserved.HostPath(run.Path);
            Assert.NotEqual(reservedPath, workloadPath);
            Assert.False(workloadPath.StartsWith(reservedPath + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("observation", true, false)]
    [InlineData("initialization", true, true)]
    [InlineData("observation", false, false)]
    [InlineData("initialization", false, false)]
    public void OnlyIsolatedVolumeHelpersMayUseReservedRoles(string role, bool isolated, bool mayInitialize)
    {
        var intent = new SupervisedKeycloakOwnership.OwnedContainer(role, "fixture", null, "fixture-image", "fixture-id",
            isolated ? null : "joined-anchor", isolated, false, [], []);
        var profile = new SupervisedKeycloakContainerProfile(intent);

        Assert.Equal(isolated, profile.HasValidRole);
        Assert.Equal(isolated, profile.VolumeHelper);
        if (!isolated) return;

        var arguments = profile.CreateArguments("fixture-owner", false, ["fixture-operation"], null);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("none", arguments);
        Assert.Contains("0:0", arguments);
        Assert.Equal(mayInitialize, arguments.Contains("CHOWN", StringComparer.Ordinal));
        Assert.Equal(mayInitialize, arguments.Contains("FOWNER", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, false, false, 0, "exited", true)]
    [InlineData(false, false, false, 0, "created", true)]
    [InlineData(true, false, false, 42, "running", false)]
    [InlineData(false, true, false, 0, "exited", false)]
    [InlineData(false, false, true, 0, "exited", false)]
    [InlineData(false, false, false, 42, "exited", false)]
    [InlineData(false, false, false, 0, "dead", false)]
    public void DefiniteStoppedObservationRejectsUncertainStates(bool running, bool paused, bool restarting, int pid, string status, bool accepted)
    {
        var snapshot = new SupervisedObservedContainer("id", "name", "image", "imageid", "", "", true, running, paused, restarting, pid, status, "no", false, true, "user", default);
        if (accepted) SupervisedKeycloakOwnership.RequireStopped(snapshot);
        else Assert.Throws<IOException>(() => SupervisedKeycloakOwnership.RequireStopped(snapshot));
    }

    [Theory]
    [InlineData(KeycloakClientMode.Public)]
    [InlineData(KeycloakClientMode.Confidential)]
    public async Task RegistrationAndBuildDoNotResolveOrStageAndPreserveCompletionChain(KeycloakClientMode mode)
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var resolutions = 0;
        var user = builder.AddParameter("user", () => { resolutions++; return "admin"; });
        var password = builder.AddParameter("password", () => { resolutions++; return "private"; }, secret: true);
        var client = mode == KeycloakClientMode.Confidential
            ? builder.AddParameter("client", () => { resolutions++; return "private-client"; }, secret: true)
            : null;
        var execution = new AppSurfaceKeycloakSupervisedOptions { LeaseRoot = TestPathUtils.PathUnder(folder.Path, "leases"), RunRoot = TestPathUtils.PathUnder(folder.Path, "runs") };
        var wrapper = builder.AddAppSurfaceKeycloak("identity", user, password, new() { ClientSecret = client }, o =>
        {
            Configure(o, execution);
            o.ClientMode = mode;
        });
        var run = Assert.IsType<AppSurfaceKeycloakSupervisedRun>(wrapper.SupervisedRun);
        var expectedDirectory = run.RunDirectory;
        execution.RunRoot = TestPathUtils.PathUnder(folder.Path, "mutated");
        var seed = run.WithSeed(Workload(folder.Path, "required-seed") with
        {
            PrivateInputs = new Dictionary<string, IResourceBuilder<ParameterResource>> { ["AdminUsername"] = user, ["AdminPassword"] = password }.ToImmutableDictionary()
        });
        var web = run.WithWeb(Workload(folder.Path, "real-web"));

        await using var app = builder.Build();

        Assert.Equal(0, resolutions);
        Assert.False(Directory.Exists(expectedDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        Assert.StartsWith(TestPathUtils.PathUnder(folder.Path, "runs"), expectedDirectory, StringComparison.Ordinal);
        Assert.Same(wrapper.RealmReady(), run.RealmReady);
        Assert.Same(run.RealmReady.Resource.Resource, Assert.Single(seed.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Same(seed.Resource, Assert.Single(web.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Empty(wrapper.Resource.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(Workload(folder.Path, "late-seed")));
        Assert.Throws<AppSurfaceKeycloakException>(() => wrapper.WithLocalSeed("legacy", _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void NonlocalEnvironmentRejectsBeforeResourcesOrEffects(string environment)
    {
        using var folder = new TempDirectory();
        var builder = Builder(environment);
        var user = builder.AddParameter("user", () => throw new InvalidOperationException());
        var password = builder.AddParameter("password", () => throw new InvalidOperationException(), secret: true);
        var client = builder.AddParameter("client", () => throw new InvalidOperationException(), secret: true);

        Assert.Throws<AppSurfaceKeycloakException>(() => builder.AddAppSurfaceKeycloak("identity", user, password,
            new() { ClientSecret = client }, o => Configure(o, new() { LeaseRoot = folder.Path, RunRoot = folder.Path })));

        Assert.DoesNotContain(builder.Resources, resource => resource is ContainerResource or ExecutableResource);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("public-with-secret")]
    [InlineData("missing-seed")]
    [InlineData("unbound-profile")]
    [InlineData("bad-pin")]
    public void UnsupportedProfilesFailWithoutCreatingResources(string invalid)
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var user = builder.AddParameter("user", () => "admin");
        var password = builder.AddParameter("password", () => "private", secret: true);
        var client = builder.AddParameter("client", () => "private-client", secret: true);

        Assert.Throws<AppSurfaceKeycloakException>(() => builder.AddAppSurfaceKeycloak("identity", user, password, new() { ClientSecret = client }, o =>
        {
            var execution = new AppSurfaceKeycloakSupervisedOptions { LeaseRoot = folder.Path, RunRoot = folder.Path };
            Configure(o, execution);
            if (invalid == "public-with-secret") o.ClientMode = KeycloakClientMode.Public;
            if (invalid == "missing-seed") o.RequireIdentitySeed = false;
            if (invalid == "unbound-profile") o.SupervisedExecution = null;
            if (invalid == "bad-pin") execution.RuntimeImage = "example.invalid/image@sha256:bad";
        }));
        Assert.DoesNotContain(builder.Resources, resource => resource is ContainerResource or ExecutableResource);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("invalid-name")]
    [InlineData("seeded-users")]
    [InlineData("missing-required-seed")]
    public void DirectRegistrationRejectsUnsupportedSnapshotsBeforeAddingResources(string invalid)
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var (registration, bindings) = RegistrationSnapshot(builder, folder.Path, options =>
        {
            if (invalid == "seeded-users")
                options.SeededUsers.Add(new("fixture-user", "fixture-password", "fixture-subject", "Fixture User"));
        });
        var name = invalid == "invalid-name" ? "identity_with_underscore" : "identity";
        if (invalid == "missing-required-seed") registration.Options.RequireIdentitySeed = false;
        var resources = builder.Resources.ToArray();

        var error = Assert.Throws<AppSurfaceKeycloakException>(() =>
            AppSurfaceKeycloakSupervisedRun.Register(builder, name, registration, bindings));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidCredentialBinding, error.Code);
        Assert.Equal(resources, builder.Resources.ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("http://localhost:18443/realms/fixture-realm")]
    [InlineData("https://localhost:18444/realms/fixture-realm")]
    [InlineData("https://localhost:18443/realms/another-realm")]
    [InlineData("https://unapproved.example.net/realms/fixture-realm")]
    [InlineData("https://user@localhost:18443/realms/fixture-realm")]
    [InlineData("https://localhost:18443/realms/fixture-realm?query=1")]
    [InlineData("https://localhost:18443/realms/fixture-realm#fragment")]
    public void DirectRegistrationRefusesUnsupportedIssuerSnapshots(string authority)
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var (registration, bindings) = RegistrationSnapshot(builder, folder.Path, options =>
        {
            options.Realm = "fixture-realm";
            options.KeycloakPort = 18443;
        });
        registration.Options.Authority = new Uri(authority);
        var resources = builder.Resources.ToArray();

        var error = Assert.Throws<AppSurfaceKeycloakException>(() =>
            AppSurfaceKeycloakSupervisedRun.Register(builder, "identity", registration, bindings));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidCredentialBinding, error.Code);
        Assert.Equal(resources, builder.Resources.ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("https://localhost:18443/realms/fixture-realm", true)]
    [InlineData("https://127.0.0.1:18443/realms/fixture-realm", false)]
    [InlineData("http://localhost:18443/realms/fixture-realm", false)]
    [InlineData("https://localhost:18444/realms/fixture-realm", false)]
    [InlineData("https://localhost:18443/realms/another-realm", false)]
    public void LocalIssuerMustMatchTheConfiguredRealmAndTransport(string? authority, bool accepted)
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        AppSurfaceKeycloakResource Register() => RegisterPublicKeycloak(builder, folder.Path, options =>
        {
            options.Realm = "fixture-realm";
            options.KeycloakPort = 18443;
            options.Authority = authority is null ? null : new Uri(authority);
        });

        if (accepted)
        {
            var wrapper = Register();
            Assert.NotNull(wrapper.SupervisedRun);
            Assert.Equal("https://localhost:18443/realms/fixture-realm", wrapper.Configuration.Authority);
        }
        else
        {
            var error = Assert.Throws<AppSurfaceKeycloakException>(() => Register());
            Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, error.Code);
            Assert.DoesNotContain(builder.Resources, resource => resource is ContainerResource or ExecutableResource);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("theme-source", true)]
    [InlineData("theme,source", false)]
    public void SupervisedThemeSourceMustBeSafeForMountArguments(string directoryName, bool accepted)
    {
        using var folder = new TempDirectory();
        using var themeFolder = new TempDirectory();
        var source = TestPathUtils.PathUnder(themeFolder.Path, directoryName);
        Directory.CreateDirectory(TestPathUtils.PathUnder(source, "login"));
        File.WriteAllText(TestPathUtils.PathUnder(source, "login", "theme.properties"), "parent=keycloak\n");
        var image = $"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}";
        var builder = Builder();
        AppSurfaceKeycloakResource Register() => RegisterPublicKeycloak(builder, folder.Path, options =>
        {
            options.SupervisedExecution!.KeycloakImage = image;
            options.LoginTheme = AppSurfaceKeycloakThemeOptions.Login(
                "fixture-theme", source, AppSurfaceKeycloakImageReference.Parse(image));
            options.LoginTheme.Platform = "linux/arm64";
        });

        if (accepted)
        {
            var theme = Assert.IsType<AppSurfaceKeycloakThemeRegistration>(Register().Theme);
            Assert.Equal("fixture-theme", theme.Name);
        }
        else
        {
            var error = Assert.Throws<AppSurfaceKeycloakException>(() => Register());
            Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidCredentialBinding, error.Code);
            Assert.DoesNotContain(builder.Resources, resource => resource is ContainerResource or ExecutableResource);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("relative-lease")]
    [InlineData("relative-run")]
    [InlineData("comma-in-root")]
    [InlineData("control-in-root")]
    [InlineData("empty-image")]
    [InlineData("image-with-space")]
    [InlineData("image-with-control")]
    [InlineData("image-without-name")]
    [InlineData("image-without-digest")]
    [InlineData("image-with-short-digest")]
    [InlineData("image-with-invalid-digest")]
    [InlineData("unbounded-ready-timeout")]
    [InlineData("unbounded-seed-timeout")]
    [InlineData("zero-ready-timeout")]
    [InlineData("zero-seed-timeout")]
    public void SupervisedExecutionRejectsUnsafeRootsImagesAndDeadlines(string input)
    {
        using var directory = new TempDirectory();
        var execution = new AppSurfaceKeycloakSupervisedOptions
        {
            LeaseRoot = TestPathUtils.PathUnder(directory.Path, "leases"),
            RunRoot = TestPathUtils.PathUnder(directory.Path, "runs")
        };
        switch (input)
        {
            case "relative-lease": execution.LeaseRoot = "leases"; break;
            case "relative-run": execution.RunRoot = "runs"; break;
            case "comma-in-root": execution.RunRoot += ",suffix"; break;
            case "control-in-root": execution.LeaseRoot += "\nsuffix"; break;
            case "empty-image": execution.RuntimeImage = ""; break;
            case "image-with-space": execution.RuntimeImage += " "; break;
            case "image-with-control": execution.RuntimeImage += "\0"; break;
            case "image-without-name": execution.RuntimeImage = "@sha256:" + new string('a', 64); break;
            case "image-without-digest": execution.KeycloakImage = "quay.io/keycloak/keycloak:26.6"; break;
            case "image-with-short-digest": execution.KeycloakImage = "quay.io/keycloak/keycloak@sha256:a"; break;
            case "image-with-invalid-digest": execution.KeycloakImage = "quay.io/keycloak/keycloak@sha256:" + new string('g', 64); break;
            case "unbounded-ready-timeout": execution.TransportReadyTimeout = TimeSpan.FromMinutes(11); break;
            case "unbounded-seed-timeout": execution.LocalSeedTimeout = TimeSpan.FromMinutes(11); break;
            case "zero-ready-timeout": execution.TransportReadyTimeout = TimeSpan.Zero; break;
            case "zero-seed-timeout": execution.LocalSeedTimeout = TimeSpan.Zero; break;
            default: throw new ArgumentOutOfRangeException(nameof(input));
        }

        var error = Assert.Throws<AppSurfaceKeycloakException>(() => execution.Snapshot());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidCredentialBinding, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("space")]
    [InlineData("ambiguous-digest")]
    [InlineData("invalid-registry")]
    public void SupervisedThemeRejectsAnInvalidExecutionImageBeforeAccessingAssets(string input)
    {
        using var directory = new TempDirectory();
        var pin = "@sha256:" + new string('a', 64);
        var theme = AppSurfaceKeycloakThemeOptions.Login("fixture-theme", directory.Path,
            AppSurfaceKeycloakImageReference.Parse("quay.io/keycloak/keycloak:26.6" + pin));
        theme.Platform = "linux/arm64";
        var execution = new AppSurfaceKeycloakSupervisedOptions
        {
            KeycloakImage = input switch
            {
                "blank" => "",
                "space" => "quay.io/keycloak/key cloak" + pin,
                "ambiguous-digest" => "quay.io/keycloak/keycloak" + pin + pin,
                "invalid-registry" => "keycloak" + pin,
                _ => throw new ArgumentOutOfRangeException(nameof(input))
            }
        };

        var error = Assert.Throws<AppSurfaceKeycloakException>(() => theme.CreateSupervisedRegistration(directory.Path, execution));

        Assert.Contains("SupervisedExecution.KeycloakImage", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(null, true, true, false)]
    [InlineData("https://app.example.net", true, true, true)]
    [InlineData("https://app.example.net/", true, true, false)]
    [InlineData("https://unapproved.example.net", true, true, false)]
    [InlineData("https://app.example.net", false, true, false)]
    [InlineData("https://app.example.net", true, false, false)]
    public void PublicWebOriginRequiresExactApprovedCallbacks(
        string? webOrigin, bool hasSignIn, bool hasSignOut, bool accepted)
    {
        const string approved = "https://app.example.net";
        var options = new AppSurfaceKeycloakOptions
        {
            Realm = "example-realm",
            Authority = new Uri("https://auth.example.net/realms/example-realm")
        };
        options.ApprovedHttpsOrigins.Add(new Uri("https://auth.example.net"));
        options.ApprovedHttpsOrigins.Add(new Uri(approved));
        if (hasSignIn) options.RedirectUris.Add(new Uri(approved + options.CallbackPath));
        if (hasSignOut) options.PostLogoutRedirectUris.Add(new Uri(approved + options.SignedOutCallbackPath));
        var inputs = new Dictionary<string, string>();
        if (webOrigin is not null) inputs.Add("PublicWebOrigin", webOrigin);

        if (accepted) AppSurfaceKeycloakSupervisedRun.ValidatePublicWebOrigin(options, inputs);
        else Assert.Throws<AppSurfaceKeycloakException>(() =>
            AppSurfaceKeycloakSupervisedRun.ValidatePublicWebOrigin(options, inputs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadLinksAreRejectedBeforeAnyCopy(bool directoryLink)
    {
        using var folder = new TempDirectory();
        var source = TestPathUtils.PathUnder(folder.Path, "source");
        var outside = TestPathUtils.PathUnder(folder.Path, "outside");
        var destination = TestPathUtils.PathUnder(folder.Path, "destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(outside); Directory.CreateDirectory(destination);
        File.WriteAllText(TestPathUtils.PathUnder(outside, "private"), "must-not-copy");
        if (directoryLink) Directory.CreateSymbolicLink(TestPathUtils.PathUnder(source, "nested"), outside);
        else File.CreateSymbolicLink(TestPathUtils.PathUnder(source, "linked"), TestPathUtils.PathUnder(outside, "private"));

        Assert.Throws<IOException>(() => SupervisedKeycloakRunStager.CopyPayload(source, destination));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Equal("must-not-copy", File.ReadAllText(TestPathUtils.PathUnder(outside, "private")));
    }

    [Fact]
    public void PayloadCopyPreservesNestedClosureWithoutChangingSource()
    {
        using var folder = new TempDirectory();
        var source = TestPathUtils.PathUnder(folder.Path, "source"); var destination = TestPathUtils.PathUnder(folder.Path, "destination");
        Directory.CreateDirectory(TestPathUtils.PathUnder(source, "nested")); Directory.CreateDirectory(destination);
        File.WriteAllBytes(TestPathUtils.PathUnder(source, "nested", "data.bin"), [0, 10, 255]);

        SupervisedKeycloakRunStager.CopyPayload(source, destination);

        Assert.Equal(new byte[] { 0, 10, 255 }, File.ReadAllBytes(TestPathUtils.PathUnder(destination, "nested", "data.bin")));
        Assert.Equal(new byte[] { 0, 10, 255 }, File.ReadAllBytes(TestPathUtils.PathUnder(source, "nested", "data.bin")));
    }

    [Theory]
    [InlineData("a:b.dll")]
    [InlineData(@"\data.bin")]
    public void PayloadCopyPreservesNativeUnixNames(string fileName)
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var source = TestPathUtils.PathUnder(folder.Path, "source");
        var destination = TestPathUtils.PathUnder(folder.Path, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        // The fixture must use native filesystem semantics for names the shared helper rejects.
        var original = Path.GetFullPath(fileName, source);
        File.WriteAllText(original, "payload");

        SupervisedKeycloakRunStager.CopyPayload(source, destination);

        var copy = Assert.Single(Directory.EnumerateFiles(destination));
        Assert.Equal(fileName, Path.GetFileName(copy));
        Assert.Equal("payload", File.ReadAllText(copy));
        Assert.Equal("payload", File.ReadAllText(original));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundAuthenticationSecretsCannotBecomePublicMetadata(bool key)
    {
        const string secret = "synthetic-authentication-credential";
        var input = new Dictionary<string, string> { [key ? "prefix-" + secret : "PublicName"] = key ? "safe" : "prefix-" + secret + "-suffix" };
        var error = Assert.Throws<AppSurfaceKeycloakException>(() => SupervisedKeycloakRunStager.EnsurePublicInputsSafe(input, [secret]));
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        SupervisedKeycloakRunStager.EnsurePublicInputsSafe(new Dictionary<string, string> { { "Username", "synthetic-user" } }, [secret]);
    }

    [Fact]
    public void PublicInputsRejectAmbiguousNamesAndUnboundedMetadataBeforeStaging()
    {
        using var folder = new TempDirectory();
        var run = PublicRun(Builder(), folder.Path);
        var inputsAtLimit = Enumerable.Range(0, 31).ToDictionary(number => $"Field{number}", _ => "value");
        inputsAtLimit.Add(new string('x', 128), new string('x', 4096));
        Assert.Same(run, run.WithPublicInputs(inputsAtLimit));
        Assert.Throws<ArgumentNullException>(() => run.WithPublicInputs(null!));

        IReadOnlyDictionary<string, string>[] invalidInputs =
        [
            new Dictionary<string, string> { ["Caption"] = "one", ["caption"] = "two" },
            new Dictionary<string, string> { [" "] = "value" },
            new Dictionary<string, string> { [new string('x', 129)] = "value" },
            new Dictionary<string, string> { ["Caption"] = null! },
            new Dictionary<string, string> { ["Caption"] = new string('x', 4097) },
            Enumerable.Range(0, 33).ToDictionary(number => $"Field{number}", _ => "value")
        ];

        foreach (var inputs in invalidInputs)
        {
            Assert.Throws<AppSurfaceKeycloakException>(() => run.WithPublicInputs(inputs));
        }
        Assert.False(Directory.Exists(run.RunDirectory));
    }

    [Fact]
    public void WorkloadRejectionLeavesTheCompletionGraphAndBindingsUntouched()
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var run = PublicRun(builder, folder.Path);
        var seed = Workload(folder.Path, "required-seed");
        var otherBuilder = Builder();
        var foreignSecret = otherBuilder.AddParameter("foreign", () => throw new InvalidOperationException(), secret: true);
        var ordinary = builder.AddParameter("ordinary", () => throw new InvalidOperationException());
        var localSecret = builder.AddParameter("local-secret", () => throw new InvalidOperationException(), secret: true);
        Assert.Throws<ArgumentNullException>(() => run.WithSeed(null!));
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithWeb(Workload(folder.Path, "web")));

        AppSurfaceKeycloakSupervisedWorkload[] invalidWorkloads =
        [
            seed with { PayloadDirectory = "relative" },
            seed with { PayloadDirectory = TestPathUtils.PathUnder(folder.Path, "payload,unexpected") },
            seed with { Name = "" },
            seed with { Name = "xy" },
            seed with { Name = "Uppercase" },
            seed with { AssemblyName = "" },
            seed with { AssemblyName = "nested/File.dll" },
            seed with { AssemblyName = "File.txt" },
            seed with { Arguments = default },
            seed with { Arguments = Enumerable.Repeat("arg", 33).ToImmutableArray() },
            seed with { Arguments = [null!] },
            seed with { Arguments = [new string('x', 4097)] },
            seed with { Arguments = ["invalid\nargument"] },
            seed with { Environment = Enumerable.Range(0, 33).ToImmutableDictionary(i => $"FIELD_{i}", _ => "value") },
            seed with { Environment = ImmutableDictionary<string, string>.Empty.Add("ILLEGAL-NAME", "value") },
            seed with { Environment = ImmutableDictionary<string, string>.Empty.Add("VALID", "invalid\nvalue") },
            seed with { PrivateInputs = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty.Add("Password", foreignSecret) },
            seed with { PrivateInputs = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty.Add("Password", ordinary) },
            seed with { PrivateInputs = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty.Add("AdminUsername", ordinary) }
        ];

        foreach (var workload in invalidWorkloads)
        {
            Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(workload));
        }
        Assert.DoesNotContain(builder.Resources, resource => resource.Name == seed.Name);

        var handle = run.WithSeed(seed with
        {
            PrivateInputs = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty.Add("Password", localSecret)
        });
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(seed));
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithWeb(Workload(folder.Path, "web") with
        {
            PrivateInputs = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty.Add("Password", localSecret)
        }));
        Assert.Same(handle.Resource, Assert.Single(run.WithWeb(Workload(folder.Path, "web"))
            .Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.False(Directory.Exists(run.RunDirectory));
    }

    [Fact]
    public void WebClosesTheWorkloadChainToFurtherSeedsAndWebMembers()
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var run = PublicRun(builder, folder.Path);
        var seed = run.WithSeed(Workload(folder.Path, "required-seed"));
        var web = run.WithWeb(Workload(folder.Path, "first-web"));
        var resources = builder.Resources.ToArray();

        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(Workload(folder.Path, "late-seed")));
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithWeb(Workload(folder.Path, "second-web")));

        Assert.Equal(resources, builder.Resources.ToArray());
        Assert.Same(seed.Resource, Assert.Single(web.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("keycloak")]
    [InlineData("worker")]
    [InlineData("observation")]
    [InlineData("initialization")]
    [InlineData("initialize")]
    [InlineData("observe-0123456789abcdef0123456789abcdef")]
    public void ReservedWorkloadNamesAreRejectedBeforeRegisteringSeedsOrWeb(string name)
    {
        using var folder = new TempDirectory();
        var builder = Builder();
        var run = PublicRun(builder, folder.Path);
        var resolutions = 0;
        var binding = builder.AddParameter("seed-secret", () => { resolutions++; return "fixture-value"; }, secret: true);
        var workload = Workload(folder.Path, name);
        var resources = builder.Resources.ToArray();

        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(workload with
        {
            PrivateInputs = workload.PrivateInputs.Add("SeedCredential", binding)
        }));
        Assert.Equal(resources, builder.Resources.ToArray());

        var seed = run.WithSeed(Workload(folder.Path, "observe-application-seed"));
        resources = builder.Resources.ToArray();
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithWeb(workload));
        Assert.Equal(resources, builder.Resources.ToArray());

        var web = run.WithWeb(Workload(folder.Path, "real-web"));
        Assert.Same(seed.Resource, Assert.Single(web.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Equal(0, resolutions);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Fact]
    public void WorkloadCountIsBoundedIncludingTheWebMember()
    {
        using var folder = new TempDirectory();
        var run = PublicRun(Builder(), folder.Path);

        for (var index = 0; index < 8; index++)
        {
            run.WithSeed(Workload(folder.Path, $"seed-{index}"));
        }

        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(Workload(folder.Path, "seed-overflow")));
        Assert.Throws<AppSurfaceKeycloakException>(() => run.WithWeb(Workload(folder.Path, "web")));
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void WorkloadNameAcceptsTheMaximumLengthAndRejectsTheNextCharacter(int length, bool accepted)
    {
        using var folder = new TempDirectory();
        var run = PublicRun(Builder(), folder.Path);
        var workloadName = new string('a', length);

        if (accepted)
        {
            var handle = run.WithSeed(Workload(folder.Path, workloadName));
            Assert.Equal(workloadName, handle.Resource.Name);
        }
        else
        {
            Assert.Throws<AppSurfaceKeycloakException>(() => run.WithSeed(Workload(folder.Path, workloadName)));
        }

        Assert.False(Directory.Exists(run.RunDirectory));
    }

    [Fact]
    public void EmptyAndOversizedPayloadsFailBeforeCopyingAnyEntry()
    {
        using var folder = new TempDirectory();
        var source = TestPathUtils.PathUnder(folder.Path, "source");
        var destination = TestPathUtils.PathUnder(folder.Path, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Assert.Throws<IOException>(() => SupervisedKeycloakRunStager.CopyPayload(source, destination));

        File.WriteAllText(TestPathUtils.PathUnder(source, "App.dll"), "payload");
        for (var index = 0; index < 4096; index++)
        {
            Directory.CreateDirectory(TestPathUtils.PathUnder(source, $"folder-{index}"));
        }
        Assert.Throws<IOException>(() => SupervisedKeycloakRunStager.CopyPayload(source, destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    private static AppSurfaceKeycloakSupervisedRun PublicRun(IDistributedApplicationBuilder builder, string root) =>
        Assert.IsType<AppSurfaceKeycloakSupervisedRun>(RegisterPublicKeycloak(builder, root).SupervisedRun);

    private static (AppSurfaceKeycloakRegistrationSnapshot Registration, AppSurfaceKeycloakCredentialSnapshot Bindings)
        RegistrationSnapshot(IDistributedApplicationBuilder builder, string root, Action<AppSurfaceKeycloakOptions>? configure = null)
    {
        var user = builder.AddParameter("user", () => throw new InvalidOperationException());
        var password = builder.AddParameter("password", () => throw new InvalidOperationException(), secret: true);
        var options = new AppSurfaceKeycloakOptions();
        Configure(options, new()
        {
            LeaseRoot = TestPathUtils.PathUnder(root, "leases"),
            RunRoot = TestPathUtils.PathUnder(root, "runs")
        });
        options.ClientMode = KeycloakClientMode.Public;
        configure?.Invoke(options);
        var registration = options.CreateRegistrationSnapshot();
        var bindings = new AppSurfaceKeycloakCredentialBindings().CreateSnapshot(builder, registration.Desired, user, password);
        return (registration, bindings);
    }

    private static AppSurfaceKeycloakResource RegisterPublicKeycloak(
        IDistributedApplicationBuilder builder, string root, Action<AppSurfaceKeycloakOptions>? configure = null)
    {
        var user = builder.AddParameter("user", () => throw new InvalidOperationException());
        var password = builder.AddParameter("password", () => throw new InvalidOperationException(), secret: true);
        return builder.AddAppSurfaceKeycloak("identity", user, password, new(), options =>
        {
            Configure(options, new()
            {
                LeaseRoot = TestPathUtils.PathUnder(root, "leases"),
                RunRoot = TestPathUtils.PathUnder(root, "runs")
            });
            options.ClientMode = KeycloakClientMode.Public;
            configure?.Invoke(options);
        });
    }

    private static AppSurfaceKeycloakSupervisedWorkload Workload(string directory, string name) => new(name, directory, "App.dll", []);
    private static IDistributedApplicationBuilder Builder(string environment = "Testing") => DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    { Args = ["--environment", environment], DisableDashboard = true });
    private static void Configure(AppSurfaceKeycloakOptions options, AppSurfaceKeycloakSupervisedOptions execution)
    {
        options.ClientMode = KeycloakClientMode.Confidential;
        options.RequireIdentitySeed = true;
        options.SeededUsers.Clear();
        options.SupervisedExecution = execution;
    }
}
