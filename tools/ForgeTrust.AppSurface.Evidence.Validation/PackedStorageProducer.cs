using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Exercises public storage commands from a verified packed CLI with independent byte assertions.</summary>
internal sealed class PackedStorageProducer(ValidationSettings settings) : IEvidenceProducer, IAsyncDisposable
{
    private EvidenceWorkspace? _workspace;
    private readonly EvidenceCommandRunner _commands = new();
    private readonly List<object> _transcript = [];

    /// <inheritdoc />
    public string Id => "packed-storage";

    /// <inheritdoc />
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        _workspace = EvidenceWorkspace.Create(settings.ScratchParent,
            new EvidenceWorkspaceOptions(settings.MaximumScratchBytes, settings.MinimumFreeBytes));
        var root = _workspace.RootPath;
        using var package = ZipFile.OpenRead(settings.PackagePath);
        ValidationProof.VerifyPackage(package, settings);
        var toolRoot = _workspace.GetPath("tool");
        Directory.CreateDirectory(toolRoot);
        const string prefix = "tools/net10.0/any/";
        var budgetTimer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var entry in package.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.Name.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName[prefix.Length..];
            ValidationProof.Require(!Path.IsPathRooted(relative) && !relative.Contains('\\') && !relative.Contains(':') &&
                !relative.Split('/').Any(segment => segment is "." or ".." or ""), "normalized packed tool member");
            var target = _workspace.GetPath("tool/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ValidationProof.Require(entry.Length <= settings.MaximumScratchBytes, "bounded package member");
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                if (budgetTimer.ElapsedMilliseconds >= 250)
                {
                    _workspace.CheckBudget();
                    budgetTimer.Restart();
                }
            }
        }
        var tool = Path.Join(toolRoot, "ForgeTrust.AppSurface.Cli.dll");
        _workspace.CheckBudget();
        ValidationProof.Require(File.Exists(tool), "packed tool assembly exists");
        var domain = _workspace.GetPath("domain");
        var environment = new Dictionary<string, string>
        {
            ["APPSURFACE_ARTIFACT_HOME"] = domain,
            ["DOTNET_ROOT"] = Path.GetDirectoryName(settings.DotnetPath)!,
            ["DOTNET_CLI_HOME"] = _workspace.GetPath("cli-home"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

        async Task<string> Run(int expectedExit, params string[] arguments)
        {
            var request = new EvidenceCommandRequest(settings.DotnetPath, [tool, .. arguments], root, TimeSpan.FromSeconds(60))
            {
                Environment = environment,
                CheckBudget = _ => { _workspace.CheckBudget(); return ValueTask.CompletedTask; },
            };
            var result = await _commands.RunAsync(request, cancellationToken);
            _transcript.Add(new
            {
                command = arguments,
                result.ExitCode,
                result.ElapsedMilliseconds,
                stdout = result.StandardOutput.Replace(root, "<workspace>", StringComparison.Ordinal),
                stderr = result.StandardError.Replace(root, "<workspace>", StringComparison.Ordinal)
            });
            ValidationProof.Require(result.ExitCode == expectedExit && !result.OutputTruncated, "storage command exit and complete capture");
            return result.StandardOutput;
        }

        try
        {
            await Run(0, "coverage", "storage", "status", "--json");
            ValidationProof.Require(!Directory.Exists(domain), "absent status is read-only");
            var shard = _workspace.GetPath("shards/coverage.cobertura.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
            await File.WriteAllTextAsync(shard, FixtureCoverage, cancellationToken);
            await Run(0, "coverage", "merge", "--source", "shards", "--output", "merged");
            var currentRoot = _workspace.GetPath("merged");
            using var firstManifest = ValidationProof.ReadJsonFile(Path.Join(currentRoot, ".appsurface-coverage-run.json"), 16 * 1024 * 1024, cancellationToken);
            var first = firstManifest.RootElement.GetProperty("inventory").EnumerateArray().ToDictionary(
                item => item.GetProperty("path").GetString()!, item => item.GetProperty("sha256").GetString()!, StringComparer.Ordinal);
            var actualFirst = await ValidationProof.InventoryAsync(currentRoot, cancellationToken);
            ValidationProof.Require(first.Count > 0 && first.All(pair => actualFirst.TryGetValue(pair.Key, out var hash) && hash == pair.Value), "first published inventory matches bytes");
            await Run(0, "coverage", "merge", "--source", "shards", "--output", "merged");
            var archives = Directory.GetDirectories(Path.Join(domain, "archives"));
            ValidationProof.Require(archives.Length == 1, "one archived replacement");
            var runId = Path.GetFileName(archives[0]);
            var before = await ValidationProof.InventoryAsync(root, cancellationToken);
            var invalid = await Run(2, "coverage", "storage", "pin", "--run", "private-invalid-value", "--json");
            ValidationProof.Require(!invalid.Contains("private-invalid-value", StringComparison.Ordinal), "invalid value is not echoed");
            ValidationProof.Require(ValidationProof.Equal(before, await ValidationProof.InventoryAsync(root, cancellationToken)), "invalid input preserves bytes");
            await Run(0, "coverage", "storage", "maintain", "--json");
            ValidationProof.Require(ValidationProof.Equal(before, await ValidationProof.InventoryAsync(root, cancellationToken)), "maintenance preview preserves bytes");
            var pinned = ValidationProof.Data(await Run(0, "coverage", "storage", "pin", "--run", runId, "--json"));
            ValidationProof.Require(pinned.GetProperty("pinned").GetBoolean(), "pin protects history");
            await Run(0, "coverage", "storage", "restore", "--run", runId, "--output", "restored/", "--json");
            var restoredRoot = _workspace.GetPath("restored");
            var restored = await ValidationProof.InventoryAsync(restoredRoot, cancellationToken);
            ValidationProof.Require(first.Count > 0 && first.All(pair => restored.TryGetValue(pair.Key, out var hash) && hash == pair.Value), "restore exact original bytes");
            var current = await ValidationProof.InventoryAsync(currentRoot, cancellationToken);
            await Run(1, "coverage", "storage", "restore", "--run", runId, "--output", "restored", "--json");
            await Run(0, "coverage", "storage", "unpin", "--run", runId, "--json");
            var policy = Path.Join(domain, "policy.json");
            await WritePrivateRetentionPolicyAsync(policy, cancellationToken);
            var preview = ValidationProof.Data(await Run(0, "coverage", "storage", "maintain", "--json"));
            ValidationProof.Require(preview.GetProperty("candidateRunIds").EnumerateArray().Any(value => value.GetString() == runId), "expired archive preview");
            var applied = ValidationProof.Data(await Run(0, "coverage", "storage", "maintain", "--apply", "--json"));
            ValidationProof.Require(applied.GetProperty("deletedRunIds").EnumerateArray().Any(value => value.GetString() == runId), "explicit archive retirement");
            ValidationProof.Require(!Directory.Exists(archives[0]), "archive removed");
            ValidationProof.Require(ValidationProof.Equal(current, await ValidationProof.InventoryAsync(currentRoot, cancellationToken)) &&
                ValidationProof.Equal(restored, await ValidationProof.InventoryAsync(restoredRoot, cancellationToken)), "current and restore preserved after retirement");
            await context.Artifacts!.WriteAsync("result", "proof/result.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                packageSha256 = await ValidationProof.HashAsync(settings.PackagePath, cancellationToken),
                sourceCommit = settings.ExpectedSourceCommit,
                commands = _transcript.Count,
                originalFiles = first,
                currentPreserved = true,
                restoredPreserved = true,
            }), cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed,
                ["storage/published-cli@1", "storage/byte-preservation@1", "storage/explicit-retirement@1"]);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Native validation contract failed: ", StringComparison.Ordinal))
        {
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Failed, [], exception.Message);
        }
        catch (EvidenceCommandException exception)
        {
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Failed, [], "Packed storage command failed: " + exception.Code + ". " + exception.Message);
        }
        finally
        {
            if (context.Artifacts is not null)
                await context.Artifacts.WriteAsync("transcript", "proof/transcript.json", JsonSerializer.SerializeToUtf8Bytes(_transcript), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _workspace?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>Writes this journey's zero-history policy inside its freshly owned scratch domain.</summary>
    /// <param name="path">Policy path in the producer-owned domain; never a caller-owned policy.</param>
    /// <param name="cancellationToken">Cancellation for the policy write.</param>
    /// <remarks>Uses Unix mode 0600 or a protected current-user Windows ACL before maintenance reads the policy.
    /// This internal seam lets native coverage tests verify the actual fixture permissions; it does not repair consumer policies.</remarks>
    internal static async Task WritePrivateRetentionPolicyAsync(string path, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"historyMaxBytes\":0}", cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current Windows user has no security identifier.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    internal const string FixtureCoverage = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="1" branch-rate="1" lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2" version="1" timestamp="1"><sources><source>.</source></sources><packages><package name="Synthetic" line-rate="1" branch-rate="1" complexity="0"><classes><class name="Fixture" filename="Fixture.cs" line-rate="1" branch-rate="1" complexity="0"><methods/><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" /></lines></class></classes></package></packages></coverage>
        """;
}
