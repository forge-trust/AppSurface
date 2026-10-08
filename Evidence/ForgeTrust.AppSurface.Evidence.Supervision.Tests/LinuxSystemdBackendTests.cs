using ForgeTrust.AppSurface.Evidence.Contracts;
using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

public sealed class LinuxSystemdBackendTests
{
    [Theory]
    [InlineData(0, "owner")]
    [InlineData(1, "worker")]
    [InlineData(2, "producer")]
    [InlineData(3, "application")]
    [InlineData(4, "utility")]
    public void UnitNamesUseOnlyTheClosedRoleAndRunIdentity(int role, string label)
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Equal($"appsurface-evidence-{label}-00112233445566778899aabbccddeeff.service",
            LinuxUnitName.Create((LinuxUnitRole)role, id).Value);
        Assert.Throws<EvidenceAdmissionException>(() => LinuxUnitName.Create((LinuxUnitRole)role, Guid.Empty));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void AnUnknownRoleCannotSelectAnotherUnit(int role) =>
        Assert.Throws<EvidenceAdmissionException>(() => LinuxUnitName.Create((LinuxUnitRole)role, Guid.NewGuid()));

    [Theory]
    [InlineData(":1.0")]
    [InlineData(":1.123456")]
    public void UniqueManagerNameIsAcceptedAsDataOnly(string name) => LinuxSystemdBackend.ValidateManagerName(name);

    [Theory]
    [InlineData("org.freedesktop.systemd1")]
    [InlineData(":1")]
    [InlineData(":1.")]
    [InlineData(":.1")]
    [InlineData(":1.canary")]
    [InlineData(":1.2\n")]
    public void InvalidManagerNameHasNoWireValueOrInnerException(string name)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxSystemdBackend.ValidateManagerName(name));
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectedCgroupAndZeroPidStayDataWithoutAnExitClaim()
    {
        var (unit, service) = Properties();
        service["ControlGroup"] = string.Empty;
        service["MainPID"] = 0u;
        var result = LinuxUnitProperties.Parse(unit, service);
        Assert.Equal(0u, result.MainPid);
        Assert.Empty(result.ControlGroup);
        Assert.Equal(17, result.ExecMainStatus);
        Assert.Equal(2_000_000ul, result.TimeoutStopMicroseconds);
    }

    [Theory]
    [InlineData("MainPID")]
    [InlineData("ExecMainCode")]
    [InlineData("RemainAfterExit")]
    [InlineData("RuntimeMaxUSec")]
    [InlineData("ControlGroup")]
    public void MissingOrWrongTypedFactsNeverBecomeDefaults(string name)
    {
        var (unit, service) = Properties();
        service.Remove(name);
        AssertClosed(() => LinuxUnitProperties.Parse(unit, service));
        service[name] = name == "ControlGroup" ? (VariantValue)0u : "canary-wire-value";
        AssertClosed(() => LinuxUnitProperties.Parse(unit, service));
    }

    [Theory]
    [InlineData("Id", false)]
    [InlineData("ControlGroup", true)]
    [InlineData("User", true)]
    [InlineData("Group", true)]
    public void PropertyTextIsBoundedAndRejectsControlCharacters(string name, bool isService)
    {
        var (unit, service) = Properties();
        var values = isService ? service : unit;
        values[name] = new string('a', 4096);
        _ = LinuxUnitProperties.Parse(unit, service);
        values[name] = new string('a', 4097);
        AssertClosed(() => LinuxUnitProperties.Parse(unit, service));
        values[name] = "canary\n";
        AssertClosed(() => LinuxUnitProperties.Parse(unit, service));
    }

    [Fact]
    public async Task CancelledOperationIsAbortedButCannotReturnUntilOriginalTaskSettles()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = SupervisionOperationJoin.RunAsync(operation.Task, () => aborted.SetResult(), cancellation.Token);
        cancellation.Cancel();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(joined.IsCompleted);
        operation.SetResult(7); // A late successful reply cannot erase cancellation.
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => joined);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task OriginalWireFaultRemainsOwnedUntilItIsObserved()
    {
        var failure = new IOException("test operation failure");
        var operation = Task.FromException<int>(failure);
        var abortCalls = 0;
        var error = await Assert.ThrowsAsync<IOException>(() =>
            SupervisionOperationJoin.RunAsync(operation, () => abortCalls++, CancellationToken.None));
        Assert.Same(failure, error);
        Assert.Equal(0, abortCalls);
    }

    [Fact]
    public async Task CancellationAlsoJoinsTheAbortedOperationFault()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = SupervisionOperationJoin.RunAsync(operation.Task,
            () => operation.SetException(new IOException("canary-aborted-wire")), cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => joined);
        Assert.True(operation.Task.IsCompleted);
    }

    [Fact]
    public async Task SuccessfulOperationDoesNotAbortItsTransport()
    {
        var abortCalls = 0;
        Assert.Equal(7, await SupervisionOperationJoin.RunAsync(Task.FromResult(7), () => abortCalls++, CancellationToken.None));
        Assert.Equal(0, abortCalls);
    }

    [Fact]
    public async Task AnAbortFailureCannotDetachTheOriginalOperationOrEchoItsException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = SupervisionOperationJoin.RunAsync(operation.Task,
            () => throw new IOException("canary-abort-failure"), cancellation.Token);
        Assert.False(joined.IsCompleted);
        operation.SetResult(1);
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => joined);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    private static void AssertClosed(Action operation)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(operation);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    private static (Dictionary<string, VariantValue> Unit, Dictionary<string, VariantValue> Service) Properties() =>
        (new(StringComparer.Ordinal)
        {
            ["Id"] = "appsurface-evidence-worker-00112233445566778899aabbccddeeff.service",
            ["LoadState"] = "loaded", ["ActiveState"] = "inactive", ["SubState"] = "dead",
        }, new(StringComparer.Ordinal)
        {
            ["ControlGroup"] = "/system.slice/appsurface-evidence-worker-00112233445566778899aabbccddeeff.service",
            ["MainPID"] = 123u, ["ExecMainPID"] = 123u, ["ExecMainCode"] = 1, ["ExecMainStatus"] = 17,
            ["User"] = "65010", ["Group"] = "65011", ["Type"] = "exec", ["KillMode"] = "control-group",
            ["RemainAfterExit"] = false, ["RuntimeMaxUSec"] = 10_000_000ul, ["TimeoutStopUSec"] = 2_000_000ul,
        });
    [Fact]
    public void StartErrorMappingUsesOnlyExactClosedNamesWithoutMessages()
    {
        (string Name, LinuxSystemdStartError Category)[] rows =
        [
            ("org.freedesktop.DBus.Error.AccessDenied", LinuxSystemdStartError.AccessDenied),
            ("org.freedesktop.DBus.Error.InvalidArgs", LinuxSystemdStartError.InvalidArgs),
            ("org.freedesktop.DBus.Error.NoReply", LinuxSystemdStartError.NoReply),
            ("org.freedesktop.DBus.Error.ServiceUnknown", LinuxSystemdStartError.ServiceUnknown),
            ("org.freedesktop.DBus.Error.UnknownMethod", LinuxSystemdStartError.UnknownMethod),
            ("org.freedesktop.systemd1.UnitExists", LinuxSystemdStartError.UnitExists),
            ("org.freedesktop.systemd1.LoadFailed", LinuxSystemdStartError.LoadFailed),
            ("org.freedesktop.systemd1.NoSuchUnit", LinuxSystemdStartError.NoSuchUnit),
        ];
        foreach (var row in rows)
        {
            var reply = new DBusErrorReplyException(row.Name, "private-canary-message");
            Assert.Equal(row.Category, LinuxSystemdBackend.ClassifyStartError(reply.ErrorName));
        }
        foreach (var name in new[] { null, "", "private-canary", "org.freedesktop.DBus.Error.AccessDenied\n",
            "org.freedesktop.dbus.Error.AccessDenied", "org.freedesktop.systemd1.NewUnknownError" })
            Assert.Equal(LinuxSystemdStartError.Other, LinuxSystemdBackend.ClassifyStartError(name));
    }

}
