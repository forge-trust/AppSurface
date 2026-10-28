using ForgeTrust.AppSurface.Durable.CloudTasks;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

public sealed class CloudTasksContractsTests
{
    [Fact]
    public void Target_and_request_build_immutable_expected_resource_names()
    {
        // protects=canonical caller-derived task resource names; fails_when=resource segments are changed or inferred; no_claim=provider resource existence
        var target = Target();
        var request = new CloudTasksActivationRequest(target, "wake_01-abc");

        Assert.Equal("projects/123456789/locations/us-central1/queues/wake-queue", target.QueueName);
        Assert.Equal("projects/123456789/locations/us-central1/queues/wake-queue/tasks/wake_01-abc", request.TaskName);
        Assert.Same(target, request.Target);
        Assert.Equal("https://worker.example/private/activate?fixed=host-config", target.WorkerEndpoint.AbsoluteUri);
    }

    [Fact]
    public void Accepted_identifier_shapes_cover_numeric_legacy_and_documented_package_lengths()
    {
        // protects=project and resource segment compatibility; fails_when=numeric or legacy project forms are rejected; no_claim=provider lookup
        var numeric = new CloudTasksActivationTarget("123456789012", "us-central1", "q", Endpoint(), "svc@project.iam.gserviceaccount.com", "https://worker.example", TimeSpan.FromSeconds(15));
        var legacy = new CloudTasksActivationTarget("s~legacy.project:123", "a1-b", new string('q', 100), Endpoint(), "svc@project.iam.gserviceaccount.com", "custom-audience", TimeSpan.FromMinutes(30));
        var maximumTask = new CloudTasksActivationRequest(numeric, new string('x', 500));
        var packageLimits = new CloudTasksActivationTarget(
            new string('p', 128), new string('a', 63), new string('q', 100), Endpoint(), "svc@project.iam.gserviceaccount.com",
            new string('a', 2_048), TimeSpan.FromMinutes(30));

        Assert.StartsWith("projects/123456789012/", numeric.QueueName, StringComparison.Ordinal);
        Assert.Contains("s~legacy.project:123", legacy.QueueName, StringComparison.Ordinal);
        Assert.EndsWith(new string('x', 500), maximumTask.TaskName, StringComparison.Ordinal);
        Assert.Equal(128, packageLimits.ProjectId.Length);
        Assert.Equal(63, packageLimits.LocationId.Length);
        Assert.Equal(100, packageLimits.QueueId.Length);
        Assert.Equal(2_048, packageLimits.Audience.Length);
    }

    [Fact]
    public void Constructors_reject_null_required_values_with_safe_validation_metadata()
    {
        // protects=required argument diagnostics; fails_when=caller value is echoed or metadata is absent; no_claim=configuration suitability
        AssertValidation(
            () => new CloudTasksActivationTarget(null!, "us-central1", "q", Endpoint(), "svc@example.com", "aud", TimeSpan.FromSeconds(15)),
            "projectId", "ASCTV001");
        AssertValidation(() => new CloudTasksActivationRequest(null!, "wake"), "target", "ASCTV001");
        AssertValidation(() => new CloudTasksActivationClientOptions(TimeSpan.Zero), "submissionTimeout", "ASCTV003");
        AssertValidation(() => new CloudRunMetadataAccessTokenOptions(Timeout.InfiniteTimeSpan), "requestTimeout", "ASCTV003");
        AssertValidation(() => CreateTarget(project: " \t "), "projectId", "ASCTV001");
        AssertValidation(() => CreateTarget(location: ""), "locationId", "ASCTV001");
        AssertValidation(() => CreateTarget(queue: "\n"), "queueId", "ASCTV001");
        AssertValidation(() => CreateTarget(audience: "  "), "audience", "ASCTV001");
        AssertValidation(() => CreateTarget(email: ""), "oidcServiceAccountEmail", "ASCTV001");
        AssertValidation(() => new CloudTasksActivationTarget("p", "us-central1", "q", null!, "svc@example.com", "aud", TimeSpan.FromSeconds(15)), "workerEndpoint", "ASCTV001");
        AssertValidation(() => new CloudTasksActivationRequest(Target(), null!), "taskId", "ASCTV001");
    }

    [Fact]
    public void Target_rejects_resource_escape_syntax_and_oversized_segments_without_echo()
    {
        // protects=validation before any network I/O; fails_when=separator/control or oversized identifier is accepted; no_claim=queue existence
        AssertValidation(() => CreateTarget(project: "project/other"), "projectId", "ASCTV002");
        AssertValidation(() => CreateTarget(project: "-project"), "projectId", "ASCTV002");
        AssertValidation(() => CreateTarget(project: new string('p', 129)), "projectId", "ASCTV003");
        AssertValidation(() => CreateTarget(project: "p\u00e9"), "projectId", "ASCTV002");
        AssertValidation(() => CreateTarget(location: "US-central1"), "locationId", "ASCTV002");
        AssertValidation(() => CreateTarget(location: "-region"), "locationId", "ASCTV002");
        AssertValidation(() => CreateTarget(location: new string('a', 64)), "locationId", "ASCTV003");
        AssertValidation(() => CreateTarget(location: "a_b"), "locationId", "ASCTV002");
        AssertValidation(() => CreateTarget(queue: "queue/name"), "queueId", "ASCTV002");
        AssertValidation(() => CreateTarget(queue: "_queue"), "queueId", "ASCTV002");
        AssertValidation(() => CreateTarget(queue: "q_queue"), "queueId", "ASCTV002");
        Assert.Equal("Queue-01", CreateTarget(queue: "Queue-01").QueueId);
        AssertValidation(() => CreateTarget(queue: "q.queue"), "queueId", "ASCTV002");
        AssertValidation(() => new CloudTasksActivationRequest(Target(), "bad.task"), "taskId", "ASCTV002");
        AssertValidation(() => CreateTarget(queue: new string('q', 101)), "queueId", "ASCTV003");
        AssertValidation(() => new CloudTasksActivationRequest(Target(), new string('x', 501)), "taskId", "ASCTV003");
    }

    [Fact]
    public void Endpoint_email_audience_and_deadline_boundaries_are_enforced()
    {
        // protects=worker identity and endpoint syntax; fails_when=ambiguous URL, malformed email, or truncated deadline is accepted; no_claim=identity ownership or audience acceptance by worker
        AssertValidation(() => CreateTarget(endpoint: new Uri("http://worker.example")), "workerEndpoint", "ASCTV002");
        AssertValidation(() => CreateTarget(endpoint: new Uri("https://user@worker.example/path")), "workerEndpoint", "ASCTV002");
        AssertValidation(() => new CloudTasksActivationTarget("p", "us-central1", "q", new Uri("https://worker.example/\npath"), "svc@example.com", "aud", TimeSpan.FromSeconds(15)), "workerEndpoint", "ASCTV002");
        AssertValidation(() => CreateTarget(endpoint: new Uri("https://worker.example/path#fragment")), "workerEndpoint", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc@@example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc@localhost"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: ".svc@example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc.@example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc..name@example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc:name@example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc@bad..example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc@-bad.example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc@bad-.example.com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: new string('a', 249) + "@x.com"), "oidcServiceAccountEmail", "ASCTV003");
        AssertValidation(() => CreateTarget(email: "svc@" + new string('a', 64) + ".com"), "oidcServiceAccountEmail", "ASCTV002");
        AssertValidation(() => CreateTarget(email: "svc@exam_ple.com"), "oidcServiceAccountEmail", "ASCTV002");
        Assert.Equal("a.!#$%&'*+-/=?^_`{|}~@example.com", CreateTarget(email: "a.!#$%&'*+-/=?^_`{|}~@example.com").OidcServiceAccountEmail);
        AssertValidation(() => CreateTarget(audience: "aud\nience"), "audience", "ASCTV002");
        AssertValidation(() => CreateTarget(audience: new string('a', 2_049)), "audience", "ASCTV003");
        AssertValidation(() => CreateTarget(deadline: TimeSpan.FromMilliseconds(15_000.1)), "dispatchDeadline", "ASCTV002");
        AssertValidation(() => CreateTarget(deadline: TimeSpan.FromSeconds(14.999)), "dispatchDeadline", "ASCTV003");
        AssertValidation(() => CreateTarget(deadline: TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1)), "dispatchDeadline", "ASCTV003");
        AssertValidation(() => CreateTarget(deadline: TimeSpan.FromTicks(1)), "dispatchDeadline", "ASCTV003");
        AssertValidation(() => CreateTarget(endpoint: new Uri("https://worker.example/?q=" + new string('a', 2_080))), "workerEndpoint", "ASCTV003");
        AssertValidation(() => new CloudTasksActivationClientOptions(TimeSpan.FromMilliseconds(uint.MaxValue)), "submissionTimeout", "ASCTV003");
        Assert.Equal(TimeSpan.FromSeconds(15), CreateTarget(deadline: TimeSpan.FromSeconds(15)).DispatchDeadline);
        Assert.Equal(TimeSpan.FromMinutes(30), CreateTarget(deadline: TimeSpan.FromMinutes(30)).DispatchDeadline);
        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1d), new CloudTasksActivationClientOptions(TimeSpan.FromMilliseconds(uint.MaxValue - 1d)).SubmissionTimeout);
        Assert.Equal(TimeSpan.FromTicks(1), new CloudRunMetadataAccessTokenOptions(TimeSpan.FromTicks(1)).RequestTimeout);

        var maximumLengthEmail = new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61);
        Assert.Equal(254, CreateTarget(email: maximumLengthEmail).OidcServiceAccountEmail.Length);

        const string endpointPrefix = "https://worker.example/?q=";
        var endpointLength = 2_083 - new Uri(endpointPrefix).GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped).Length;
        Assert.Equal(2_083, CreateTarget(endpoint: new Uri(endpointPrefix + new string('a', endpointLength)))
            .WorkerEndpoint.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped).Length);
    }

    [Fact]
    public void Token_validator_enforces_bare_rfc6750_grammar_and_8192_character_limit()
    {
        // protects=token rejected before authorization metadata is built; fails_when=invalid grammar or oversized value passes; no_claim=token signature, expiry, or identity
        Assert.True(CloudTasksValidation.IsValidToken("A1-._~+/=="));
        Assert.True(CloudTasksValidation.IsValidToken(new string('t', 8_192)));
        foreach (var token in new string?[]
                 {
                     null, "", "Bearer abc", "abc def", "abc\n", "abcé", "=abc", "ab=c", "abc\t", new string('t', 8_193),
                 })
        {
            Assert.False(CloudTasksValidation.IsValidToken(token));
        }
    }

    [Fact]
    public void Receipt_accepts_only_exact_catalog_disposition_pairs()
    {
        // protects=closed public outcome contract; fails_when=undefined enum, arbitrary code, or cross-disposition code is accepted; no_claim=provider response equivalence
        Assert.Equal(4, Enum.GetValues<CloudTasksDispatchDisposition>().Length);
        var allowed = new Dictionary<CloudTasksDispatchDisposition, string[]>
        {
            [CloudTasksDispatchDisposition.Created] = ["ASCT001"],
            [CloudTasksDispatchDisposition.AlreadyExists] = ["ASCT002"],
            [CloudTasksDispatchDisposition.Rejected] = ["ASCT101", "ASCT102", "ASCT103", "ASCT201", "ASCT202", "ASCT203", "ASCT204", "ASCT205", "ASCT206", "ASCT207"],
            [CloudTasksDispatchDisposition.Indeterminate] = ["ASCT301", "ASCT302", "ASCT303", "ASCT304", "ASCT305", "ASCT306", "ASCT307"],
        };

        foreach (var pair in allowed)
            foreach (var code in pair.Value)
            {
                var receipt = new CloudTasksDispatchReceipt(Request().TaskName, pair.Key, code);
                Assert.Equal(Request().TaskName, receipt.TaskName);
                Assert.Equal(code, receipt.DiagnosticCode);
            }

        foreach (var pair in allowed)
            foreach (var other in allowed.Where(candidate => candidate.Key != pair.Key))
                foreach (var code in other.Value)
                {
                    AssertValidation(() => new CloudTasksDispatchReceipt(Request().TaskName, pair.Key, code), "diagnosticCode", "ASCTV002");
                }

        AssertValidation(() => new CloudTasksDispatchReceipt(Request().TaskName, CloudTasksDispatchDisposition.Created, "ASCT999"), "diagnosticCode", "ASCTV002");
        AssertValidation(() => new CloudTasksDispatchReceipt(Request().TaskName, (CloudTasksDispatchDisposition)99, "ASCT001"), "disposition", "ASCTV002");
        AssertValidation(() => new CloudTasksDispatchReceipt("provider/name", CloudTasksDispatchDisposition.Created, "ASCT001"), "taskName", "ASCTV002");
        var tooLongName = new string('a', 840);
        AssertValidation(() => new CloudTasksDispatchReceipt(tooLongName, CloudTasksDispatchDisposition.Created, "ASCT001"), "taskName", "ASCTV003");
    }

    private static CloudTasksActivationTarget Target() => CreateTarget();

    private static CloudTasksActivationRequest Request() => new(Target(), "wake_01-abc");

    private static Uri Endpoint() => new("https://worker.example/private/activate?fixed=host-config");

    private static CloudTasksActivationTarget CreateTarget(
        string project = "123456789",
        string location = "us-central1",
        string queue = "wake-queue",
        Uri? endpoint = null,
        string email = "worker@proof-project.iam.gserviceaccount.com",
        string audience = "https://worker.example",
        TimeSpan? deadline = null) => new(project, location, queue, endpoint ?? Endpoint(), email, audience, deadline ?? TimeSpan.FromSeconds(30));

    private static void AssertValidation(Action action, string parameter, string code)
    {
        var exception = Record.Exception(action);
        Assert.IsAssignableFrom<ArgumentException>(exception);
        var argumentException = (ArgumentException)exception!;
        Assert.Equal(parameter, argumentException.ParamName);
        Assert.Equal(code, argumentException.Data["CloudTasksValidationCode"]);
        Assert.NotNull(argumentException.Data["CloudTasksConstraint"]);
        Assert.Contains("Cloud Tasks activation validation", argumentException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("proof-project", argumentException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", argumentException.Message, StringComparison.Ordinal);
    }
}
