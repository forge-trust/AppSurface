using ForgeTrust.AppSurface.Core;
using YamlDotNet.RepresentationModel;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class DocsExportWorkflowContractTests
{
    [Fact]
    public void BuildWorkflow_Should_Run_AppSurface_Docs_Export_On_PullRequests_And_Guard_Pages_Deploy()
    {
        var workflow = LoadBuildWorkflow();
        var root = (YamlMappingNode)workflow.Documents[0].RootNode;
        var on = GetMapping(root, "on");
        var pullRequest = GetMapping(on, "pull_request");
        var pullRequestBranches = GetSequence(pullRequest, "branches")
            .Select(GetScalar)
            .ToArray();

        Assert.Contains("main", pullRequestBranches);

        var jobs = GetMapping(root, "jobs");
        var exportJob = GetMapping(jobs, "export-appsurface-docs");

        Assert.DoesNotContain(exportJob.Children.Keys.OfType<YamlScalarNode>(), key => key.Value == "if");

        var steps = GetSequence(exportJob, "steps")
            .Cast<YamlMappingNode>()
            .ToArray();
        var checkout = FindStep(steps, "Checkout code");
        Assert.Equal("actions/checkout@93cb6efe18208431cddfb8368fd83d5badbf9bfd", GetScalar(checkout, "uses"));
        Assert.Equal("0", GetScalar(GetMapping(checkout, "with"), "fetch-depth"));

        var exportStep = FindStep(steps, "Export AppSurface Docs static site with CDN validation");
        var exportEnv = GetMapping(exportStep, "env");
        Assert.Contains("AppSurfaceDocs__Contributor__DefaultBranch", ScalarKeys(exportEnv));
        Assert.Contains("AppSurfaceDocs__Contributor__SourceRef", ScalarKeys(exportEnv));
        Assert.Contains("AppSurfaceDocs__Contributor__SourceUrlTemplate", ScalarKeys(exportEnv));
        Assert.Contains("AppSurfaceDocs__Contributor__SymbolSourceUrlTemplate", ScalarKeys(exportEnv));
        Assert.Contains("AppSurfaceDocs__Contributor__EditUrlTemplate", ScalarKeys(exportEnv));
        Assert.Contains("AppSurfaceDocs__Contributor__LastUpdatedMode", ScalarKeys(exportEnv));
        Assert.Equal("branding", GetScalar(exportEnv, "AppSurfaceDocs__Identity__BrandingAssets__DirectoryPath"));
        Assert.DoesNotContain("AppSurfaceDocs__Identity__BrandingAssets__RequestPath", ScalarKeys(exportEnv));
        Assert.Equal(
            "/branding/appsurface-site-icon.svg",
            GetScalar(exportEnv, "AppSurfaceDocs__Identity__Logo__Path"));
        Assert.Equal(
            "/branding/appsurface-site-icon.svg",
            GetScalar(exportEnv, "AppSurfaceDocs__Identity__Favicon__SvgPath"));

        var exportRun = GetScalar(exportStep, "run");
        Assert.Contains("printf '%s\\n' '/' '/docs' > \"$RUNNER_TEMP/appsurface-docs-seeds.txt\"", exportRun, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", exportRun, StringComparison.Ordinal);
        Assert.Contains("docs export", exportRun, StringComparison.Ordinal);
        Assert.Contains("--repo .", exportRun, StringComparison.Ordinal);
        Assert.Contains("--mode cdn", exportRun, StringComparison.Ordinal);
        Assert.Contains("--strict", exportRun, StringComparison.Ordinal);
        Assert.Contains("--seeds \"$RUNNER_TEMP/appsurface-docs-seeds.txt\"", exportRun, StringComparison.Ordinal);
        Assert.Contains("--output \"$RUNNER_TEMP/appsurface-docs-pages\"", exportRun, StringComparison.Ordinal);

        var uploadStep = FindStep(steps, "Upload Pages artifact");
        Assert.Equal("${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}", GetScalar(uploadStep, "if"));
        Assert.Equal("actions/upload-pages-artifact@7b1f4a764d45c48632c6b24a0339c27f5614fb0b", GetScalar(uploadStep, "uses"));
        Assert.Equal("${{ runner.temp }}/appsurface-docs-pages", GetScalar(GetMapping(uploadStep, "with"), "path"));

        var deployJob = GetMapping(jobs, "deploy-appsurface-docs");
        Assert.Equal("${{ github.event_name == 'push' && github.ref == 'refs/heads/main' }}", GetScalar(deployJob, "if"));
    }

    private static YamlStream LoadBuildWorkflow()
    {
        var repoRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        using var reader = File.OpenText(Path.Join(repoRoot, ".github", "workflows", "build.yml"));
        var workflow = new YamlStream();
        workflow.Load(reader);
        return workflow;
    }

    private static YamlMappingNode FindStep(IEnumerable<YamlMappingNode> steps, string name)
    {
        return steps.Single(step => string.Equals(GetScalar(step, "name"), name, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ScalarKeys(YamlMappingNode mapping)
    {
        return mapping.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value)
            .Where(value => value is not null)!;
    }

    private static YamlMappingNode GetMapping(YamlMappingNode mapping, string key)
    {
        return (YamlMappingNode)GetChild(mapping, key);
    }

    private static YamlSequenceNode GetSequence(YamlMappingNode mapping, string key)
    {
        return (YamlSequenceNode)GetChild(mapping, key);
    }

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return GetScalar(GetChild(mapping, key));
    }

    private static string GetScalar(YamlNode node)
    {
        return ((YamlScalarNode)node).Value ?? string.Empty;
    }

    private static YamlNode GetChild(YamlMappingNode mapping, string key)
    {
        var match = mapping.Children.SingleOrDefault(
            child => child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal));

        if (match.Key is null)
        {
            throw new KeyNotFoundException($"YAML mapping did not contain key '{key}'.");
        }

        return match.Value;
    }
}
