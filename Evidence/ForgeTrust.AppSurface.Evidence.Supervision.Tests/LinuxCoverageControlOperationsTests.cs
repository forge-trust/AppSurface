using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Closed command data controls; no case constructs a native producer or artifact custody.</summary>
public sealed class LinuxCoverageControlOperationsTests
{
    [Fact]
    public void ExactExistingTransportCommandSelectsOnlyItsOpaqueRelativeToken()
    {
        var selected = Selected();
        Assert.Equal("coverage-a1", LinuxCoverageControlOperations.ValidateCommand(selected, "/run/raw", Request(selected)));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("cwd")]
    [InlineData("solution")]
    [InlineData("configuration")]
    [InlineData("collector")]
    [InlineData("results")]
    [InlineData("verbosity")]
    [InlineData("parallel")]
    [InlineData("compilation")]
    [InlineData("reuse")]
    [InlineData("extra")]
    [InlineData("missing")]
    public void AlteredCommandCannotSupplyExecutableArgumentsOrPathAuthority(string field)
    {
        var selected = Selected();
        var request = Request(selected);
        var args = request.Arguments.ToList();
        var index = field switch
        {
            "solution" => 1, "configuration" => 3, "collector" => 4, "results" => 6,
            "verbosity" => 8, "parallel" => 9, "compilation" => 10, "reuse" => 11,
            _ => -1,
        };
        if (index >= 0) args[index] = "CANARY";
        if (field == "extra") args.Add("CANARY");
        if (field == "missing") args.RemoveAt(11);
        request = new EvidenceRunControlRequest(field == "exe" ? "/canary/runtime" : request.Executable, args,
            field == "cwd" ? "/canary/source" : request.WorkingDirectory);
        Reject(() => LinuxCoverageControlOperations.ValidateCommand(selected, "/run/raw", request));
        Assert.Equal("coverage-a1", LinuxCoverageControlOperations.ValidateCommand(selected, "/run/raw", Request(selected)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a.b")]
    [InlineData("a_b")]
    [InlineData("é")]
    [InlineData("a\n")]
    public void InvalidTokenCannotSelectAResultRoot(string value)
    {
        Reject(() => LinuxCoverageControlOperations.RequireToken(value));
        var selected = Selected();
        var request = Request(selected);
        var args = request.Arguments.ToArray();
        args[6] = "/run/raw/" + value;
        Reject(() => LinuxCoverageControlOperations.ValidateCommand(selected, "/run/raw", new EvidenceRunControlRequest(request.Executable, args, request.WorkingDirectory)));
    }

    [Fact]
    public void TokenBoundsAndMissingSolutionRejectBeforeNativeDispatch()
    {
        LinuxCoverageControlOperations.RequireToken(new string('a', 96));
        Reject(() => LinuxCoverageControlOperations.RequireToken(new string('a', 97)));
        Reject(() => LinuxCoverageControlOperations.RequireToken(null!));
        var selected = Selected();
        Reject(() => LinuxCoverageControlOperations.ValidateCommand(Selected(solution: false), "/run/raw", Request(selected)));
    }

    private static EvidenceSupervisorRequest Selected(bool solution = true) => EvidenceSupervisorRequest.Parse(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["schema"] = "evidence-supervisor-linux-v1", ["mode"] = "observation",
            ["tool_root"] = "/run/tool", ["runtime_root"] = "/run/sdk",
            ["runtime_host"] = "/run/sdk/dotnet", ["entry_path"] = "/run/tool/appsurface.dll",
            ["policy_file"] = "/run/tool/policy.json", ["subject_root"] = "/run/subject",
            ["base_revision"] = new string('a', 40), ["subject_revision"] = new string('b', 40),
            ["workflow_identity"] = "workflow", ["paths"] = new[] { "src/A.cs" },
            ["observation_profile_ids"] = new[] { "coverage" }, ["observation_producer_ids"] = new[] { "coverage" },
            ["job_deadline_utc"] = "2099-01-01T00:00:00Z", ["admission_seconds"] = 30,
            ["start_seconds"] = 10, ["collection_seconds"] = 5, ["cleanup_seconds"] = 5, ["stopping_seconds"] = 5,
            ["solution"] = solution ? "/run/subject/Subject.csproj" : null,
        }));

    private static EvidenceRunControlRequest Request(EvidenceSupervisorRequest selected) => new(selected.RuntimeHost,
        ["test", selected.Solution!, "--configuration", "Debug", "--collect:XPlat Code Coverage", "--results-directory",
            "/run/raw/coverage-a1", "--verbosity", "quiet", "-m:1", "-p:UseSharedCompilation=false", "-nodeReuse:false"], selected.SubjectRoot);

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("CANARY", error.Message, StringComparison.Ordinal);
    }
}
