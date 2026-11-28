using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakSupervisedDockerTests
{
    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherId = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void SyntheticClientRequiresAnAbsoluteExecutablePath()
    {
        var error = Assert.Throws<ArgumentException>(() => new SupervisedDocker("unix:///var/run/docker.sock", "docker"));

        Assert.Equal("clientExecutable", error.ParamName);
    }

    [Fact]
    public async Task RunPassesArgumentVectorAndTrimsOutput()
    {
        string[]? received = null;
        var docker = Docker((_, args) => { received = args; return Task.FromResult("  result\n"); });

        Assert.Equal("result", await docker.RunAsync(default, "ps", "--format", "{{.ID}}"));
        Assert.Equal(new[] { "ps", "--format", "{{.ID}}" }, received);
    }

    [Theory]
    [InlineData("", "info")]
    [InlineData("unix:///tmp/docker.sock", "info")]
    [InlineData("unix:///var/run/docker.sock", "bad\0arg")]
    public async Task RunRejectsUnsupportedEndpointOrArguments(string endpoint, string argument)
    {
        var docker = new SupervisedDocker(endpoint, (_, _) => Task.FromResult("should not run"));
        var error = await Assert.ThrowsAsync<IOException>(() => docker.RunAsync(default, argument));
        Assert.Equal("Docker inventory could not be verified.", error.Message);
    }

    [Fact]
    public async Task RunAcceptsTheSupervisedDefaultAndSystemDockerEndpoints()
    {
        var endpoints = new[] { new AppSurfaceKeycloakSupervisedOptions().DockerEndpoint, "unix:///var/run/docker.sock" };
        foreach (var endpoint in endpoints)
        {
            var docker = new SupervisedDocker(endpoint, (_, _) => Task.FromResult("accepted"));
            Assert.Equal("accepted", await docker.RunAsync(default, "info"));
        }
    }

    [Fact]
    public async Task RunHidesRunnerErrorsAndHonorsCancellation()
    {
        var docker = Docker((_, _) => throw new InvalidOperationException("sensitive daemon output"));
        var error = await Assert.ThrowsAsync<IOException>(() => docker.RunAsync(default, "info"));
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.Ordinal);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => docker.RunAsync(cancelled.Token, "info"));
    }

    [Fact]
    public async Task InspectPropagatesCancellationFromTheSyntheticCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var docker = Docker((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult($"[{{\"Id\":\"{Id}\"}}]");
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => docker.InspectAsync(Id, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData("daemon")]
    [InlineData("image")]
    [InlineData("volume")]
    [InlineData("attachments")]
    [InlineData("container")]
    public async Task InventoryPreservesCancellationAndDoesNotContinueWithPartialData(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string[]>();
        var docker = Docker((_, args) =>
        {
            calls.Add(args);
            cancellation.Cancel();
            return Task.FromResult(operation == "volume" ? "wanted" : Id);
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "daemon": await docker.DaemonAsync(cancellation.Token); break;
                case "image": await docker.ImageIdAsync("registry.example.test/runtime:fixture", cancellation.Token); break;
                case "volume": await docker.VolumeAsync("wanted", cancellation.Token); break;
                case "attachments": await docker.AttachmentsAsync("wanted", cancellation.Token); break;
                case "container": await docker.ContainerAsync("keycloak", Id, "run-1", cancellation.Token); break;
            }
        });

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Single(calls);
    }

    [Fact]
    public async Task SyntheticInventoryEnforcesArgumentAndOutputBounds()
    {
        var calls = 0;
        var docker = Docker((_, _) => { calls++; return Task.FromResult("ok"); });
        await Assert.ThrowsAsync<IOException>(() => docker.RunAsync(default, Enumerable.Repeat("x", 129).ToArray()));
        await Assert.ThrowsAsync<IOException>(() => docker.RunAsync(default, new string('x', 65 * 1024)));
        Assert.Equal(0, calls);

        var oversized = Docker((_, _) => Task.FromResult(new string('x', 1024 * 1024 + 1)));
        var error = await Assert.ThrowsAsync<IOException>(() => oversized.RunAsync(default, "info"));
        Assert.Equal("Docker inventory could not be verified.", error.Message);
    }

    [Fact]
    public async Task SyntheticInventoryRejectsArgumentsThatCannotBeEncodedAsStrictUtf8()
    {
        var calls = 0;
        var docker = Docker((_, _) => { calls++; return Task.FromResult("should not run"); });

        var error = await Assert.ThrowsAsync<IOException>(() => docker.RunAsync(default, "\ud800"));

        Assert.Equal("Docker inventory could not be verified.", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DaemonRequiresNonemptyControlFreeIdentifier()
    {
        Assert.Equal("daemon-1", await Docker((_, _) => Task.FromResult(" daemon-1 ")).DaemonAsync(default));
        foreach (var value in new[] { "", "  ", "bad\nvalue" })
        {
            var error = await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(value)).DaemonAsync(default));
            Assert.Equal("Docker inventory could not be verified.", error.Message);
        }
    }

    [Fact]
    public async Task ImageInventoryReturnsTheExactIdentifierForTheConfiguredReference()
    {
        const string reference = "registry.example.test/runtime:fixture";
        const string imageId = "sha256:fixture-runtime";
        var calls = new List<string[]>();
        var docker = Docker((_, args) =>
        {
            calls.Add(args);
            return Task.FromResult(JsonSerializer.Serialize(new[] { new { Id = imageId } }));
        });

        Assert.Equal(imageId, await docker.ImageIdAsync(reference, default));
        Assert.Equal(new[] { "image", "inspect", reference }, Assert.Single(calls));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("[{\"Id\":null}]")]
    [InlineData("[{\"Id\":\"  \"}]")]
    [InlineData("[{\"Id\":\"sha256:one\\nsha256:two\"}]")]
    [InlineData("[{\"Id\":\"sha256:one\",\"Id\":\"sha256:two\"}]")]
    [InlineData("[{\"Id\":\"sha256:one\"},{\"Id\":\"sha256:two\"}]")]
    public async Task ImageInventoryRejectsMalformedOrAmbiguousIdentity(string response)
    {
        var error = await Assert.ThrowsAsync<IOException>(
            () => Docker((_, _) => Task.FromResult(response)).ImageIdAsync("registry.example.test/runtime:fixture", default));

        Assert.Equal("Docker inventory could not be verified.", error.Message);
    }

    [Fact]
    public async Task InspectReturnsCallerOwnedSingleMatchingObject()
    {
        using var doc = await Docker((_, _) => Task.FromResult($"[{{\"Id\":\"{Id}\"}}]")).InspectAsync(Id, default);
        Assert.Equal(Id, doc.RootElement[0].GetProperty("Id").GetString());
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("[]")).InspectAsync(Id, default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult($"[{{\"Id\":\"{OtherId}\"}}]")).InspectAsync(Id, default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("not-json")).InspectAsync(Id, default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("[]")).InspectAsync("short", default));
        foreach (var malformed in new[]
        {
            "{}", "null", "[null]", "[1]", $"[{{\"Id\":\"{Id}\"}},{{\"Id\":\"{Id}\"}}]",
            $"[{{\"Id\":1}}]", $"[{{\"Id\":\"{Id}\",\"Id\":\"{Id}\"}}]"
        })
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(malformed)).InspectAsync(Id, default));
    }

    [Fact]
    public async Task InspectRejectsFullLengthIdentifierContainingNonHexadecimalCharacterBeforeRunningCommand()
    {
        var calls = 0;
        var docker = Docker((_, _) =>
        {
            calls++;
            return Task.FromResult("should not run");
        });
        var invalidId = Id[..10] + "A" + Id[11..];

        var error = await Assert.ThrowsAsync<IOException>(() => docker.InspectAsync(invalidId, default));

        Assert.Equal("Docker inventory could not be verified.", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task VolumeReturnsNullOnlyForSuccessfulAbsentSnapshotAndVerifiesInspect()
    {
        if (OperatingSystem.IsWindows()) return; // The qualified daemon reports absolute POSIX mountpoints.
        Assert.Null(await Docker((_, _) => Task.FromResult("other\n")).VolumeAsync("wanted", default));
        var valid = "[{\"Name\":\"wanted\",\"Driver\":\"local\",\"Scope\":\"local\",\"CreatedAt\":\"now\",\"Mountpoint\":\"/var/lib/docker/volumes/wanted/_data\",\"Options\":null,\"Labels\":{}}]";
        var calls = 0;
        var volume = await Docker((_, _) => Task.FromResult(++calls == 1 ? "wanted\n" : valid)).VolumeAsync("wanted", default);
        Assert.Equal(new SupervisedVolume("wanted", "local", "local", "now", "/var/lib/docker/volumes/wanted/_data"), volume);
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("wanted\n")).VolumeAsync("wanted", default));
        foreach (var bad in new[]
        {
            "{}", "null", "[]", "[null]", "[1]", "[{},{}]", valid.Replace("\"Name\":\"wanted\"", "\"Name\":1", StringComparison.Ordinal),
            valid.Replace("\"Driver\":\"local\"", "\"Driver\":\"nfs\"", StringComparison.Ordinal),
            valid.Replace("\"Scope\":\"local\"", "\"Scope\":\"global\"", StringComparison.Ordinal),
            valid.Replace("/var/lib/docker/volumes/wanted/_data", "relative", StringComparison.Ordinal),
            valid.Replace("/var/lib/docker/volumes/wanted/_data", "/var/lib/docker/volumes/wanted/_data,other", StringComparison.Ordinal),
            valid.Replace("/var/lib/docker/volumes/wanted/_data", "/var/lib/docker/volumes/wanted/_data\\nother", StringComparison.Ordinal),
            valid.Replace("\"Options\":null", "\"Options\":{\"type\":\"nfs\"}", StringComparison.Ordinal),
            valid.Replace("\"Options\":null", "\"Options\":[]", StringComparison.Ordinal),
            valid.Replace("\"Labels\":{}", "\"Labels\":[]", StringComparison.Ordinal),
            valid.Replace("\"Name\":\"wanted\"", "\"Name\":\"different\"", StringComparison.Ordinal),
            valid.Replace("\"CreatedAt\":\"now\"", "\"CreatedAt\":\"\"", StringComparison.Ordinal),
            valid.Replace("\"Name\":\"wanted\"", "\"Name\":\"wanted\",\"Name\":\"wanted\"", StringComparison.Ordinal)
        })
        {
            calls = 0;
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(++calls == 1 ? "wanted" : bad)).VolumeAsync("wanted", default));
        }
    }

    [Fact]
    public async Task AttachmentsRequireDistinctFullNativeIds()
    {
        Assert.Empty(await Docker((_, _) => Task.FromResult("\n")).AttachmentsAsync("volume", default));
        Assert.Equal(new[] { Id, OtherId }, await Docker((_, _) => Task.FromResult(Id + "\n" + OtherId)).AttachmentsAsync("volume", default));
        foreach (var output in new[] { "short", Id + "\n" + Id })
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(output)).AttachmentsAsync("volume", default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("")).AttachmentsAsync("bad name", default));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task VolumeInventoryUsesTheLeaseNameLengthLimit(int length, bool accepted)
    {
        var name = new string('v', length);
        var calls = 0;
        var docker = Docker((_, _) =>
        {
            calls++;
            return Task.FromResult(string.Empty);
        });

        if (accepted)
        {
            Assert.Null(await docker.VolumeAsync(name, default));
            Assert.Empty(await docker.AttachmentsAsync(name, default));
            Assert.Equal(2, calls);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => docker.VolumeAsync(name, default));
            await Assert.ThrowsAsync<IOException>(() => docker.AttachmentsAsync(name, default));
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task AttachmentsQueryKeepsValidDockerVolumePunctuationInTheExactFilter()
    {
        const string name = "fixture.keycloak_data-2";
        string[]? arguments = null;
        var docker = Docker((_, args) =>
        {
            arguments = args;
            return Task.FromResult(Id);
        });

        Assert.Equal(Id, Assert.Single(await docker.AttachmentsAsync(name, default)));
        Assert.NotNull(arguments);
        Assert.Equal(["ps", "-aq", "--no-trunc", "--filter", "volume=" + name], arguments);
    }

    [Fact]
    public async Task ContainerVerifiesNameIdRunLabelAndReturnsTypedObservation()
    {
        var calls = new List<string[]>();
        var docker = Docker((_, args) =>
        {
            calls.Add(args);
            if (args[0] == "ps") return Task.FromResult(Id);
            return Task.FromResult(ContainerJson(Id, "keycloak", "run-1"));
        });
        var result = await docker.ContainerAsync("keycloak", null, "run-1", default);
        Assert.NotNull(result);
        Assert.Equal(Id, result.Id);
        Assert.Equal("keycloak", result.Name);
        Assert.Equal("quay.io/keycloak/keycloak:latest", result.Image);
        Assert.Equal("", result.PidMode);
        Assert.Equal("bridge", result.NetworkMode);
        Assert.Null(result.ExitCode);
        Assert.Null(result.Profile);
        Assert.Equal(new[] { "ps", "-aq", "--no-trunc", "--filter", "name=^/keycloak$" }, calls[0]);

        var byIdCalls = new List<string[]>();
        var byIdDocker = Docker((_, args) =>
        {
            byIdCalls.Add(args);
            if (args[0] == "ps") return Task.FromResult(args[^1].StartsWith("id=", StringComparison.Ordinal) ? Id : "");
            return Task.FromResult(ContainerJson(Id, "keycloak", "run-1"));
        });
        await Assert.ThrowsAsync<IOException>(() => byIdDocker.ContainerAsync("keycloak", Id, "run-1", default));
        Assert.Equal(2, byIdCalls.Count);
        Assert.Null(await Docker((_, _) => Task.FromResult("")).ContainerAsync("keycloak", Id, "run-1", default));
        Assert.Null(await Docker((_, _) => Task.FromResult("")).ContainerAsync("keycloak", null, "run-1", default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(Id + "\n" + OtherId)).ContainerAsync("keycloak", null, "run-1", default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("")).ContainerAsync("bad name", null, "run-1", default));
        await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult("short")).ContainerAsync("keycloak", null, "run-1", default));
    }

    [Fact]
    public async Task ContainerReadsTypedExitCodeAndRequiresProofDetailsOnlyForOwnership()
    {
        var exitJson = ContainerJson(Id, "keycloak", "run-1")
            .Replace("\"Status\":\"exited\"", "\"Status\":\"exited\",\"ExitCode\":19", StringComparison.Ordinal);
        var calls = 0;
        var observed = await Docker((_, _) => Task.FromResult(++calls == 1 ? Id : exitJson))
            .ContainerAsync("keycloak", null, "run-1", default);
        Assert.NotNull(observed);
        Assert.Equal(19, observed.ExitCode);

        foreach (var json in new[]
        {
            ContainerJson(Id, "keycloak", "run-1"),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Status\":\"exited\"", "\"Status\":\"exited\",\"ExitCode\":null", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Status\":\"exited\"", "\"Status\":\"exited\",\"ExitCode\":\"19\"", StringComparison.Ordinal)
        })
        {
            calls = 0;
            var liveness = await Docker((_, _) => Task.FromResult(++calls == 1 ? Id : json))
                .ContainerAsync("keycloak", null, "run-1", default);
            Assert.NotNull(liveness);
            Assert.Null(liveness.ExitCode);

            calls = 0;
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(++calls == 1 ? Id : json))
                .ContainerAsync("keycloak", null, "run-1", default, requireProfile: true));
        }
    }

    [Fact]
    public async Task ContainerRejectsANameReassignedToAnotherFullIdentityWithoutInspectingIt()
    {
        var arguments = new List<string[]>();
        var docker = Docker((_, args) =>
        {
            arguments.Add(args);
            return Task.FromResult(OtherId);
        });

        var failure = await Assert.ThrowsAsync<IOException>(() => docker.ContainerAsync("keycloak", Id, "run-1", default));

        Assert.Equal("Docker inventory could not be verified.", failure.Message);
        Assert.Equal(["ps", "-aq", "--no-trunc", "--filter", "name=^/keycloak$"], Assert.Single(arguments));
    }

    [Fact]
    public async Task ContainerRejectsMismatchedIdentityLabelsAndMalformedInspect()
    {
        foreach (var json in new[]
        {
            ContainerJson(OtherId, "keycloak", "run-1"),
            ContainerJson(Id, "other", "run-1"),
            ContainerJson(Id, "keycloak", "other-run"),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Image\":\"sha256:image\"", "\"Image\":\"\"", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("quay.io/keycloak/keycloak:latest", "", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Status\":\"exited\"", "\"Status\":\"\"", StringComparison.Ordinal),
            "{}", "null", "[null]", "[1]", "[]", $"[{ContainerJson(Id, "keycloak", "run-1")[1..^1]},{ContainerJson(Id, "keycloak", "run-1")[1..^1]}]",
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Pid\":0", "\"Pid\":0.5", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Pid\":0", "\"Pid\":\"0\"", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Running\":false", "\"Running\":0", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Init\":null", "\"Init\":\"false\"", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Privileged\":false", "\"Privileged\":null", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Config\":{", "\"Config\":null,\"Config\":{", StringComparison.Ordinal)
        })
        {
            var calls = 0;
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(++calls == 1 ? Id : json)).ContainerAsync("keycloak", null, "run-1", default));
        }
    }

    [Fact]
    public async Task ContainerAcceptsMissingOrNullInitAndRejectsMalformedRequiredShapes()
    {
        foreach (var json in new[]
        {
            ContainerJson(Id, "keycloak", "run-1").Replace(",\"Init\":null", "", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1")
        })
        {
            var calls = 0;
            var observed = await Docker((_, _) => Task.FromResult(++calls == 1 ? Id : json))
                .ContainerAsync("keycloak", null, "run-1", default);
            Assert.NotNull(observed);
            Assert.False(observed.Init);
        }

        var missingLabels = JsonNode.Parse(ContainerJson(Id, "keycloak", "run-1"))!.AsArray();
        missingLabels[0]!["Config"]!["Labels"] = null;
        var invalidState = JsonNode.Parse(ContainerJson(Id, "keycloak", "run-1"))!.AsArray();
        invalidState[0]!["State"] = new JsonArray();
        foreach (var json in new[]
        {
            missingLabels.ToJsonString(),
            invalidState.ToJsonString(),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"RestartPolicy\":{\"Name\":\"no\"}", "\"RestartPolicy\":null", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"ReadonlyRootfs\":true", "\"ReadonlyRootfs\":\"true\"", StringComparison.Ordinal),
            ContainerJson(Id, "keycloak", "run-1").Replace("\"Image\":\"sha256:image\"", "\"Image\":\"\"", StringComparison.Ordinal)
        })
        {
            var calls = 0;
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(++calls == 1 ? Id : json))
                .ContainerAsync("keycloak", null, "run-1", default));
        }
    }

    [Fact]
    public async Task InventoryRejectsDuplicateAndMissingIdentityFieldsWithoutLeakingSnapshots()
    {
        var volume = "[{\"Name\":\"wanted\",\"Driver\":\"local\",\"Scope\":\"local\",\"CreatedAt\":\"now\",\"Mountpoint\":\"/var/lib/docker/volumes/wanted/_data\",\"Options\":null,\"Labels\":{}}]";
        foreach (var bad in new[]
        {
            volume.Replace("\"Driver\":\"local\"", "\"Driver\":\"local\",\"Driver\":\"local\"", StringComparison.Ordinal),
            volume.Replace(",\"Labels\":{}", "", StringComparison.Ordinal),
            volume.Replace("\"Options\":null", "\"Options\":null,\"Options\":null", StringComparison.Ordinal)
        })
        {
            var calls = 0;
            var failure = await Assert.ThrowsAsync<IOException>(() => Docker((_, _) =>
                    Task.FromResult(++calls == 1 ? "wanted" : bad))
                .VolumeAsync("wanted", default));
            Assert.Equal("Docker inventory could not be verified.", failure.Message);
            Assert.DoesNotContain("wanted", failure.ToString(), StringComparison.Ordinal);
        }

        foreach (var output in new[] { Id + "\nshort", Id + "\n" + OtherId + "\n" + Id })
            await Assert.ThrowsAsync<IOException>(() => Docker((_, _) => Task.FromResult(output))
                .AttachmentsAsync("volume", default));
    }

    [Fact]
    public async Task InventoryRejectsMalformedNamesAndRunIdentityBeforeInvokingRunner()
    {
        var calls = 0;
        var docker = Docker((_, _) =>
        {
            calls++;
            return Task.FromResult(string.Empty);
        });

        await Assert.ThrowsAsync<IOException>(() => docker.VolumeAsync("bad/name", default));
        await Assert.ThrowsAsync<IOException>(() => docker.VolumeAsync("", default));
        await Assert.ThrowsAsync<IOException>(() => docker.VolumeAsync("-bad", default));
        await Assert.ThrowsAsync<IOException>(() => docker.AttachmentsAsync("bad name", default));
        await Assert.ThrowsAsync<IOException>(() => docker.ContainerAsync("keycloak", null, "bad\nrun", default));
        await Assert.ThrowsAsync<IOException>(() => docker.ContainerAsync("keycloak", "short", "run-1", default));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingExecutableAfterConstructionReturnsSafeInventoryFailure()
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var command = CreateClient(folder, "exit 0");
        File.Delete(TestPathUtils.PathUnder(folder.Path, "docker-fixture"));

        var error = await Assert.ThrowsAsync<IOException>(() => command.RunAsync(default, "info"));

        Assert.Equal("Docker inventory could not be verified.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ProcessSupervisionPassesArgumentsWithoutShellEvaluation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var command = CreateClient(folder, """
            test "$1" = '--host' && test "$2" = 'unix:///var/run/docker.sock' || exit 2
            printf '  %s  \n' "$3"
            printf 'hidden diagnostic' >&2
            """);

        Assert.Equal("argument with spaces; $literal", await command.RunAsync(default, "argument with spaces; $literal"));
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("invalid-utf8")]
    [InlineData("large-stdout")]
    [InlineData("large-stderr")]
    public async Task ProcessSupervisionRejectsUntrustedOutputWithoutDisclosingIt(string failure)
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var script = failure switch
        {
            "nonzero" => "printf 'synthetic private value' >&2; exit 42",
            "invalid-utf8" => "printf '\\377'",
            "large-stdout" => "head -c 1048577 /dev/zero",
            "large-stderr" => "head -c 1048577 /dev/zero >&2",
            _ => throw new InvalidOperationException()
        };
        var command = CreateClient(folder, script);

        var error = await Assert.ThrowsAsync<IOException>(() => command.RunAsync(default, "inspect"));

        Assert.Equal("Docker inventory could not be verified.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ClientDeadlineReturnsSafeFailureAndReapsTheStartedProcess()
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var started = TestPathUtils.PathUnder(folder.Path, "started");
        var command = CreateClient(folder, """
            printf '%s' "$$" > "$3"
            exec /bin/sleep 12
            """);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            command.RunAsync(default, started).WaitAsync(TimeSpan.FromSeconds(11)));

        Assert.Equal("Docker inventory could not be verified.", error.Message);
        var pid = int.Parse(File.ReadAllText(started), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public async Task CancellingStartedClientReturnsWithinSharedStopBudget()
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var started = TestPathUtils.PathUnder(folder.Path, "started");
        var command = CreateClient(folder, """
            printf '%s' "$$" > "$3"
            exec /bin/sleep 60
            """);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = command.RunAsync(stop.Token, started);
        using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(started))
            await Task.Delay(10, observation.Token);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        stop.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static SupervisedDocker CreateClient(TempDirectory folder, string script)
    {
        var path = TestPathUtils.PathUnder(folder.Path, "docker-fixture");
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new("unix:///var/run/docker.sock", path);
    }

    private static SupervisedDocker Docker(Func<CancellationToken, string[], Task<string>> runner)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var socket = Path.GetFullPath(TestPathUtils.PathUnder(home, ".docker", "run", "docker.sock"));
        return new SupervisedDocker("unix://" + socket.Replace('\\', '/'), runner);
    }

    private static string ContainerJson(string id, string name, string run) => JsonSerializer.Serialize(new[] { new
    {
        Id = id, Name = "/" + name, Image = "sha256:image", Config = new { Image = "quay.io/keycloak/keycloak:latest", Labels = new Dictionary<string, string> { ["appsurface.run"] = run }, User = "1000" },
        HostConfig = new { PidMode = "", NetworkMode = "bridge", Init = (bool?)null, RestartPolicy = new { Name = "no" }, Privileged = false, ReadonlyRootfs = true },
        State = new { Running = false, Paused = false, Restarting = false, Pid = 0, Status = "exited" }
    } });
}
