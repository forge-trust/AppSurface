using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>
/// Portable proc grammar/continuity controls; these construct no retained live identity or admission.
/// </summary>
public sealed class LinuxProcessIdentityTests
{
    private static readonly Guid RunId = Guid.Parse("e65e3be5-0cc6-4d68-8fdc-33f172c8b510");
    private static readonly LinuxUnitName Worker = LinuxUnitName.Create(LinuxUnitRole.Worker, RunId);
    private static readonly LinuxUnitName Owner = LinuxUnitName.Create(LinuxUnitRole.Owner, RunId);

    [Fact]
    public void CompleteStatusRetainsAllFourIdsAndDetachesInput()
    {
        var bytes = Status();
        var ids = LinuxProcessData.ParseStatus(bytes);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(new LinuxProcessIds(65010, 65010, 65010, 65010), ids.Uids);
        Assert.Equal(new LinuxProcessIds(65011, 65011, 65011, 65011), ids.Gids);
        Assert.True(ids.Uids.AllEqual(65010));
        Assert.False(ids.Gids.AllEqual(65010));
    }

    [Theory]
    [InlineData("Uid:\t65010 65010 65010 65010\n")]
    [InlineData("Gid:\t65011 65011 65011 65011\n")]
    [InlineData("UID:\t65010 65010 65010 65010\n")]
    [InlineData("gid:\t65011 65011 65011 65011\n")]
    public void DuplicateAndCaseAliasedIdentityRowsReject(string extra) =>
        Reject(() => LinuxProcessData.ParseStatus([.. Status(), .. Bytes(extra)]));

    [Theory]
    [InlineData("65010 65010 65010")]
    [InlineData("65010 65010 65010 65010 65010")]
    [InlineData("-1 65010 65010 65010")]
    [InlineData("+65010 65010 65010 65010")]
    [InlineData("4294967296 65010 65010 65010")]
    [InlineData("65010.0 65010 65010 65010")]
    [InlineData("65010\u00a065010 65010 65010")]
    public void WrongTupleShapeOrNonDecimalIdsReject(string row) =>
        Reject(() => LinuxProcessData.ParseStatus(Status(uids: row)));

    [Theory]
    [InlineData("")]
    [InlineData("Uid:\t1 1 1 1\n")]
    [InlineData("Gid:\t1 1 1 1\n")]
    [InlineData("Uid:\t1 1 1 1\nGid:\t1 1 1 1")]
    [InlineData("Uid:\t1 1 1 1\r\nGid:\t1 1 1 1\n")]
    [InlineData("canary-without-a-field-separator\nUid:\t1 1 1 1\nGid:\t1 1 1 1\n")]
    public void MissingTruncatedOrMalformedStatusRejects(string text) =>
        Reject(() => LinuxProcessData.ParseStatus(Bytes(text)));

    [Fact]
    public void StatusCountsEncodedBytesBeforeDecodeAndBoundsRows()
    {
        var text = new StringBuilder(Encoding.UTF8.GetString(Status()));
        while (text.Length < LinuxProcessData.MaximumStatusBytes)
        {
            var remaining = LinuxProcessData.MaximumStatusBytes - text.Length;
            var size = Math.Min(8000, remaining);
            Assert.True(size >= 8);
            text.Append("Extra:\t").Append('x', size - 8).Append('\n');
        }
        var exact = Bytes(text.ToString());
        Assert.Equal(LinuxProcessData.MaximumStatusBytes, exact.Length);
        Assert.True(LinuxProcessData.ParseStatus(exact).Uids.AllEqual(65010));
        Reject(() => LinuxProcessData.ParseStatus([.. exact, (byte)'\n']));
        Reject(() => LinuxProcessData.ParseStatus([0xff, (byte)'\n']));
        Reject(() => LinuxProcessData.ParseStatus(Bytes("Uid:\t1 1 1 1\0\nGid:\t1 1 1 1\n")));
        Reject(() => LinuxProcessData.ParseStatus([.. Status(), .. Bytes(string.Concat(
            Enumerable.Repeat("Extra:\t0\n", 512)))]));
    }

    [Fact]
    public void StatUsesFinalClosingParenthesisAndAcceptsSignedUnrelatedFields()
    {
        var bytes = Stat(comm: "private-canary ) name ( nested )\nline", signedOther: true);
        var parsed = LinuxProcessData.ParseStat(bytes);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(new LinuxProcessStat(123, 987654, 'S'), parsed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+123")]
    [InlineData("2147483648")]
    public void StatRejectsInvalidPid(string pid) =>
        Reject(() => LinuxProcessData.ParseStat(Bytes(Encoding.UTF8.GetString(Stat()).Replace("123 (", pid + " ("))));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+987654")]
    [InlineData("18446744073709551616")]
    [InlineData("canary")]
    public void StatRejectsWrongStartTime(string start) =>
        Reject(() => LinuxProcessData.ParseStat(Stat(start: start)));

    [Fact]
    public void StatRejectsIncompleteRecordAndMalformedTailWithoutEchoingComm()
    {
        var complete = Encoding.UTF8.GetString(Stat(comm: "private-canary"));
        Reject(() => LinuxProcessData.ParseStat(Bytes(complete[..^1])));
        Reject(() => LinuxProcessData.ParseStat(Bytes(complete[..complete.LastIndexOf(' ')] + "\n")));
        Reject(() => LinuxProcessData.ParseStat(Bytes(complete.Replace("(private-canary)", "private-canary)"))));
        Reject(() => LinuxProcessData.ParseStat(Bytes(complete.Replace(") S ", ") ? "))));
        Reject(() => LinuxProcessData.ParseStat(Bytes(complete.Replace(") S ", ") S  "))));
        Reject(() => LinuxProcessData.ParseStat(Bytes(complete.Replace(" 0 ", " not-numeric "))));
        Reject(() => LinuxProcessData.ParseStat(new byte[LinuxProcessData.MaximumStatBytes + 1]));
        Reject(() => LinuxProcessData.ParseStat([0xff, (byte)'\n']));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0::/system.slice/worker.service")]
    [InlineData("1:name=systemd:/system.slice/worker.service\n")]
    [InlineData("0::/system.slice/worker.service\n0::/system.slice/worker.service\n")]
    [InlineData("0::/system.slice/worker.service\n1:cpu:/system.slice/worker.service\n")]
    [InlineData("0::/system.slice//worker.service\n")]
    [InlineData("0::/system.slice/../worker.service\n")]
    [InlineData("0::/system.slice/./worker.service\n")]
    [InlineData("0::/system.slice/canary:worker.service\n")]
    [InlineData("0::/system.slice/canary\\worker.service\n")]
    [InlineData("0::/system.slice/worker.service\r\n")]
    public void UnifiedCgroupRequiresOneCompleteCanonicalRow(string text) =>
        Reject(() => LinuxProcessData.ParseCgroup(Bytes(text)));

    [Fact]
    public void ValidWorkerTupleRequiresExactUnitAndEveryUidGidPosition()
    {
        var sample = Sample();
        LinuxProcessData.RequireExpected(sample, 123, 65010, 65011, Worker, LinuxProcessSamplingRole.Worker);
        foreach (var ids in new[]
        {
            new LinuxProcessIds(0, 65010, 65010, 65010), new LinuxProcessIds(65010, 0, 65010, 65010),
            new LinuxProcessIds(65010, 65010, 0, 65010), new LinuxProcessIds(65010, 65010, 65010, 0),
        })
            Reject(() => RequireWorker(sample with { Uids = ids }));
        foreach (var ids in new[]
        {
            new LinuxProcessIds(0, 65011, 65011, 65011), new LinuxProcessIds(65011, 0, 65011, 65011),
            new LinuxProcessIds(65011, 65011, 0, 65011), new LinuxProcessIds(65011, 65011, 65011, 0),
        })
            Reject(() => RequireWorker(sample with { Gids = ids }));
        Reject(() => RequireWorker(sample with { ControlGroup = sample.ControlGroup + "/child" }));
        Reject(() => RequireWorker(sample with { ControlGroup = "/system.slice/" + Owner.Value }));
        Reject(() => RequireWorker(sample with { Stat = sample.Stat with { Pid = 124 } }));
        Reject(() => RequireWorker(sample with { Stat = sample.Stat with { StartTimeTicks = 0 } }));
        Reject(() => LinuxProcessData.ParseCgroup(new byte[LinuxProcessData.MaximumCgroupBytes + 1]));
        Reject(() => LinuxProcessData.ParseCgroup([0xff, (byte)'\n']));
    }

    [Fact]
    public void RootOwnerIsExplicitAndCannotBeSelectedAsWorker()
    {
        var ids = LinuxProcessData.ParseStatus(Status("0 0 0 0", "0 0 0 0"));
        var owner = Sample() with { Uids = ids.Uids, Gids = ids.Gids, ControlGroup = "/system.slice/" + Owner.Value };
        LinuxProcessData.RequireExpected(owner, 123, 0, 0, Owner, LinuxProcessSamplingRole.Owner);
        Reject(() => LinuxProcessData.RequireExpected(owner, 123, 0, 0, Owner, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxProcessData.RequireExpected(owner, 123, 65010, 65011, Owner, LinuxProcessSamplingRole.Owner));
        Reject(() => LinuxProcessData.RequireSelection(123, 0, 65011, Worker, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxProcessData.RequireSelection(123, 65010, 0, Worker, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxProcessData.RequireSelection(0, 65010, 65011, Worker, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxProcessData.RequireSelection(123, uint.MaxValue, 65011,
            Worker, LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxProcessData.RequireSelection(123, 65010, 65011,
            LinuxUnitName.Create(LinuxUnitRole.Producer, RunId), LinuxProcessSamplingRole.Worker));
        Reject(() => LinuxProcessData.RequireSelection(123, 65010, 65011, Worker, (LinuxProcessSamplingRole)42));
    }

    [Theory]
    [InlineData('Z')]
    [InlineData('X')]
    [InlineData('x')]
    [InlineData('?')]
    public void DeadOrUnknownStatesCannotSatisfyLiveSample(char state) =>
        Reject(() => RequireWorker(Sample() with { Stat = new LinuxProcessStat(123, 987654, state) }));

    [Fact]
    public void CoherentSamplesAllowSchedulingChangesButRejectReusedPidOrChangedIdsAndGroup()
    {
        var before = Sample();
        var after = before with { Stat = before.Stat with { State = 'R' } };
        RequireWorker(after);
        LinuxProcessData.RequireSameIdentity(before, after);
        Reject(() => LinuxProcessData.RequireSameIdentity(before, after with
            { Stat = after.Stat with { StartTimeTicks = after.Stat.StartTimeTicks + 1 } }));
        Reject(() => LinuxProcessData.RequireSameIdentity(before, after with { Stat = after.Stat with { Pid = 124 } }));
        Reject(() => LinuxProcessData.RequireSameIdentity(before, after with { Uids = new(1, 1, 1, 1) }));
        Reject(() => LinuxProcessData.RequireSameIdentity(before, after with { Gids = new(1, 1, 1, 1) }));
        Reject(() => LinuxProcessData.RequireSameIdentity(before,
            after with { ControlGroup = before.ControlGroup + "/child" }));
    }

    private static void RequireWorker(LinuxProcessSample sample) =>
        LinuxProcessData.RequireExpected(sample, 123, 65010, 65011, Worker, LinuxProcessSamplingRole.Worker);

    private static LinuxProcessSample Sample()
    {
        var ids = LinuxProcessData.ParseStatus(Status());
        return new(LinuxProcessData.ParseStat(Stat()), ids.Uids, ids.Gids,
            LinuxProcessData.ParseCgroup(Bytes("0::/system.slice/" + Worker.Value + "\n")));
    }

    private static byte[] Status(string uids = "65010 65010 65010 65010", string gids = "65011 65011 65011 65011") =>
        Bytes("Name:\tdotnet\nState:\tS (sleeping)\nUid:\t" + uids + "\nGid:\t" + gids + "\nGroups:\t\n");

    private static byte[] Stat(string comm = "dotnet", string start = "987654", bool signedOther = false)
    {
        var fields = Enumerable.Repeat("0", 50).ToArray();
        fields[0] = "S"; fields[19] = start;
        if (signedOther) fields[5] = "-1";
        return Bytes(123u.ToString(CultureInfo.InvariantCulture)
            + " (" + comm + ") " + string.Join(' ', fields) + "\n");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.ToString());
    }
}
