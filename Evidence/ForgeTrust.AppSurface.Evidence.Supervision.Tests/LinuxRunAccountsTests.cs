namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Portable account command/reservation/identity data controls; no NSS, root utility or lease is exercised.</summary>
public sealed class LinuxRunAccountsTests
{
    private static readonly Guid Run = Guid.Parse("a1234567-89ab-cdef-0123-456789abcdef");

    [Fact]
    public void NamesAreDerivedBoundedAndSeparateWithoutSelectingNumericIdentities()
    {
        var names = LinuxRunAccountNames.Create(Run);
        Assert.Equal("evwa123456789abcdef0123456789ab", names.Worker);
        Assert.Equal("evsa123456789abcdef0123456789ab", names.Subject);
        Assert.Equal("evra123456789abcdef0123456789ab", names.Results);
        Assert.Equal("evaa123456789abcdef0123456789ab", names.Application);
        Assert.Equal("evga123456789abcdef0123456789ab", names.ResourceAccess);
        Assert.Equal(new[] { names.Worker, names.Subject, names.Results }, names.ReservedNames(includeApplication: false));
        Assert.All(names.ReservedNames(includeApplication: true), name =>
        {
            Assert.Equal(31, name.Length);
            Assert.True(LinuxRunAccountNames.IsGenerated(name));
        });
        Assert.Equal(names.Worker, LinuxRunAccountNames.Create(Run).Worker);
        Assert.NotEqual(names.Worker, LinuxRunAccountNames.Create(Guid.Parse("b1234567-89ab-cdef-0123-456789abcdef")).Worker);
        AssertClosed(() => LinuxRunAccountNames.Create(Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("root")]
    [InlineData("--system")]
    [InlineData("evw../../canary")]
    [InlineData("evwA123456789abcdef0123456789ab")]
    [InlineData("evwa123456789abcdef0123456789ag")]
    [InlineData("evwa123456789abcdef0123456789ab\n")]
    [InlineData("evwa123456789abcdef0123456789a")]
    [InlineData("evwa123456789abcdef0123456789abc")]
    public void NamesRejectOptionsTraversalControlsUppercaseAndWrongLengths(string? name)
    {
        Assert.False(LinuxRunAccountNames.IsGenerated(name));
        if (name is not null)
            AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteGroup, name));
        Assert.True(LinuxRunAccountNames.IsGenerated(LinuxRunAccountNames.Create(Run).Worker));
    }

    [Fact]
    public void CreateCommandsRetainTheExistingNoLoginNoHomePolicyAndFixedEnvironment()
    {
        var names = LinuxRunAccountNames.Create(Run);
        foreach (var name in new[] { names.Worker, names.Subject })
        {
            var command = LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateUser, name);
            Assert.Equal("/usr/sbin/useradd", command.Executable);
            Assert.Equal(new[] { "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", name },
                command.Arguments);
            Assert.DoesNotContain("--uid", command.Arguments);
            Assert.DoesNotContain("--gid", command.Arguments);
            Assert.DoesNotContain("--groups", command.Arguments);
        }
        var application = LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateUser, names.Application);
        Assert.Equal("/usr/sbin/useradd", application.Executable);
        Assert.Equal(new[] { "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", names.Application },
            application.Arguments);
        Assert.DoesNotContain("--uid", application.Arguments);
        Assert.DoesNotContain("--gid", application.Arguments);
        Assert.DoesNotContain("--groups", application.Arguments);
        var results = LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateResultsGroup, names.Results);
        Assert.Equal("/usr/sbin/groupadd", results.Executable);
        Assert.Equal(new[] { "--system", names.Results }, results.Arguments);
        var resourceAccess = LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateResourceAccessGroup,
            names.ResourceAccess);
        Assert.Equal("/usr/sbin/groupadd", resourceAccess.Executable);
        Assert.Equal(new[] { "--system", names.ResourceAccess }, resourceAccess.Arguments);
        Assert.Equal(new[] { "PATH=/usr/sbin:/usr/bin:/sbin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8" },
            LinuxRunAccountCommand.Environment);
    }

    [Fact]
    public void ClosedOperationsRejectCrossRoleOrUnknownCommandsWithoutChangingPositiveCommands()
    {
        var names = LinuxRunAccountNames.Create(Run);
        AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateUser, names.Results));
        AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, names.Results));
        AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateResultsGroup, names.Worker));
        AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateResultsGroup, names.ResourceAccess));
        AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateResourceAccessGroup, names.Results));
        AssertClosed(() => LinuxRunAccountCommand.Create(LinuxRunAccountOperation.CreateUser, names.ResourceAccess));
        AssertClosed(() => LinuxRunAccountCommand.Create((LinuxRunAccountOperation)999, names.Worker));
        Assert.Equal("/usr/sbin/userdel", LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, names.Subject).Executable);
        Assert.Equal("/usr/sbin/groupdel", LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteGroup, names.Results).Executable);
    }

    [Fact]
    public void FirstReservationContainsUserAndImplicitGroupBeforeAnyDispatchCanOccur()
    {
        var names = LinuxRunAccountNames.Create(Run);
        var pending = new LinuxRunAccountReservations(names);
        Assert.Empty(pending.ReverseCleanup());
        var create = pending.ReserveNext();
        Assert.Equal(names.Worker, create.Arguments[^1]);
        var cleanup = pending.ReverseCleanup();
        Assert.Equal(2, cleanup.Length);
        Assert.Equal("/usr/sbin/userdel", cleanup[0].Executable);
        Assert.Equal("/usr/sbin/groupdel", cleanup[1].Executable);
        Assert.All(cleanup, command => Assert.Equal(new[] { names.Worker }, command.Arguments));
        pending.MarkFailed(); // Models an ambiguous dispatch outcome, not a native accepted create.
        Assert.True(pending.Failed);
        AssertClosed(() => pending.ReserveNext());
        Assert.Equal(2, pending.ReverseCleanup().Length);
    }

    [Fact]
    public void ReverseObligationsRetainAllFiveNamesAndCannotBeMutatedOrReplayed()
    {
        var names = LinuxRunAccountNames.Create(Run);
        var pending = new LinuxRunAccountReservations(names);
        Assert.Equal(names.Worker, pending.ReserveNext().Arguments[^1]);
        var earlier = pending.ReverseCleanup();
        Assert.Equal(names.Subject, pending.ReserveNext().Arguments[^1]);
        Assert.Equal(names.Results, pending.ReserveNext().Arguments[^1]);
        var cleanup = pending.ReverseCleanup();
        Assert.Equal(new[] { "/usr/sbin/groupdel", "/usr/sbin/userdel", "/usr/sbin/groupdel",
            "/usr/sbin/userdel", "/usr/sbin/groupdel" }, cleanup.Select(command => command.Executable));
        Assert.Equal(new[] { names.Results, names.Subject, names.Subject, names.Worker, names.Worker },
            cleanup.Select(command => command.Arguments.Single()));
        Assert.Equal(2, earlier.Length);
        var changedCopy = cleanup.SetItem(0, cleanup[^1]);
        Assert.Equal(names.Worker, changedCopy[0].Arguments.Single());
        Assert.Equal(names.Results, pending.ReverseCleanup()[0].Arguments.Single());
        AssertClosed(() => pending.ReserveNext());
        pending.MarkFailed();
        pending.MarkFailed();
        Assert.True(pending.Failed);
        Assert.Equal(5, pending.ReverseCleanup().Length);
    }

    [Fact]
    public void OptionalApplicationProfileReservesItsUserAndResourceGroupInOrderBeforeDispatch()
    {
        var names = LinuxRunAccountNames.Create(Run);
        var pending = new LinuxRunAccountReservations(names, includeApplication: true);
        var creates = new[]
        {
            pending.ReserveNext(), pending.ReserveNext(), pending.ReserveNext(),
            pending.ReserveNext(), pending.ReserveNext(),
        };

        Assert.Equal(new[] { names.Worker, names.Subject, names.Results, names.Application, names.ResourceAccess },
            creates.Select(command => command.Arguments[^1]));
        Assert.Equal(new[]
        {
            LinuxRunAccountOperation.CreateUser,
            LinuxRunAccountOperation.CreateUser,
            LinuxRunAccountOperation.CreateResultsGroup,
            LinuxRunAccountOperation.CreateUser,
            LinuxRunAccountOperation.CreateResourceAccessGroup,
        }, creates.Select(command => command.Operation));
        Assert.Equal(new[]
        {
            names.ResourceAccess, names.Application, names.Application, names.Results,
            names.Subject, names.Subject, names.Worker, names.Worker,
        }, pending.ReverseCleanup().Select(command => command.Arguments.Single()));
        Assert.Equal(new[]
        {
            LinuxRunAccountOperation.DeleteGroup,
            LinuxRunAccountOperation.DeleteUser,
            LinuxRunAccountOperation.DeleteGroup,
            LinuxRunAccountOperation.DeleteGroup,
            LinuxRunAccountOperation.DeleteUser,
            LinuxRunAccountOperation.DeleteGroup,
            LinuxRunAccountOperation.DeleteUser,
            LinuxRunAccountOperation.DeleteGroup,
        }, pending.ReverseCleanup().Select(command => command.Operation));
        pending.MarkFailed(); // Every attempted extra identity remains an obligation after uncertain dispatch.
        Assert.Equal(8, pending.ReverseCleanup().Length);
        AssertClosed(() => pending.ReserveNext());
    }

    [Fact]
    public void ValidIdentityDataNeedsBothNamedPrivateGroupsAndASeparateResultsGroup()
    {
        var data = Data();
        var snapshot = Parse(data);
        Assert.Equal(65010u, snapshot.WorkerUid);
        Assert.Equal(65011u, snapshot.WorkerGid);
        Assert.Equal(610u, snapshot.SubjectUid);
        Assert.Equal(611u, snapshot.SubjectGid);
        Assert.Equal(612u, snapshot.ResultsGid);
        Assert.Equal(0u, snapshot.ApplicationUid);
        Assert.Equal(0u, snapshot.ApplicationGid);
        Assert.Equal(0u, snapshot.ResourceAccessGid);
        var legacySnapshot = new LinuxRunAccountSnapshot(65010, 65011, 610, 611, 612);
        Assert.Equal(0u, legacySnapshot.ApplicationUid);
        Assert.Equal(0u, legacySnapshot.ApplicationGid);
        Assert.Equal(0u, legacySnapshot.ResourceAccessGid);
        // These fixture numbers are just data; a second consistent mapping is also valid.
        Assert.Equal(710u, Parse(data with { Worker = data.Worker with { Uid = 710 } }).WorkerUid);
    }

    [Fact]
    public void ApplicationSnapshotRequiresCompleteRecordsAndFiveDistinctGroupsAndThreeDistinctUsers()
    {
        var data = Data(includeApplication: true);
        var snapshot = Parse(data);
        Assert.Equal(620u, snapshot.ApplicationUid);
        Assert.Equal(621u, snapshot.ApplicationGid);
        Assert.Equal(613u, snapshot.ResourceAccessGid);
        Assert.Equal(3, new[] { snapshot.WorkerUid, snapshot.SubjectUid, snapshot.ApplicationUid }.Distinct().Count());
        Assert.Equal(5, new[] { snapshot.WorkerGid, snapshot.SubjectGid, snapshot.ApplicationGid,
            snapshot.ResultsGid, snapshot.ResourceAccessGid }.Distinct().Count());

        AssertClosed(() => LinuxRunAccountSnapshot.Parse(data.Names, data.Worker, data.Subject,
            data.WorkerGroup, data.SubjectGroup, data.Results, data.Application));
        AssertClosed(() => Parse(data with { Application = data.Application! with { Uid = data.Worker.Uid } }));
        AssertClosed(() => Parse(data with
        {
            Application = data.Application! with { Uid = data.Subject.Uid },
        }));
        var otherNames = LinuxRunAccountNames.Create(Guid.Parse("b1234567-89ab-cdef-0123-456789abcdef"));
        AssertClosed(() => Parse(data with
        {
            Application = data.Application! with { Name = data.Names.Worker },
        }));
        AssertClosed(() => Parse(data with
        {
            ApplicationGroup = data.ApplicationGroup! with { Name = data.Names.Subject },
        }));
        AssertClosed(() => Parse(data with
        {
            ResourceAccessGroup = data.ResourceAccessGroup! with { Name = otherNames.ResourceAccess },
        }));
        AssertClosed(() => Parse(data with
        {
            Application = data.Application! with { Gid = data.Worker.Gid },
            ApplicationGroup = data.ApplicationGroup! with { Gid = data.Worker.Gid },
        }));
        AssertClosed(() => Parse(data with
        {
            Application = data.Application! with { Gid = data.Subject.Gid },
            ApplicationGroup = data.ApplicationGroup! with { Gid = data.Subject.Gid },
        }));
        AssertClosed(() => Parse(data with
        {
            Application = data.Application! with { Gid = data.Results.Gid },
            ApplicationGroup = data.ApplicationGroup! with { Gid = data.Results.Gid },
        }));
        AssertClosed(() => Parse(data with
        {
            ResourceAccessGroup = data.ResourceAccessGroup! with { Gid = data.Results.Gid },
        }));
        AssertClosed(() => Parse(data with
        {
            ResourceAccessGroup = data.ResourceAccessGroup! with { Gid = data.Worker.Gid },
        }));
        AssertClosed(() => Parse(data with
        {
            ResourceAccessGroup = data.ResourceAccessGroup! with { Gid = data.Subject.Gid },
        }));
        AssertClosed(() => Parse(data with
        {
            ResourceAccessGroup = data.ResourceAccessGroup! with { Gid = data.Application!.Gid },
        }));
    }

    [Fact]
    public void ApplicationAndResourceGroupsRejectRegisteredMembersAndPrimaryGroupMustMatch()
    {
        var data = Data(includeApplication: true);
        AssertClosed(() => Parse(data with
        {
            ApplicationGroup = data.ApplicationGroup! with { Members = [data.Names.Subject] },
        }));
        AssertClosed(() => Parse(data with
        {
            ResourceAccessGroup = data.ResourceAccessGroup! with { Members = [data.Names.Application] },
        }));
        AssertClosed(() => Parse(data with
        {
            ApplicationGroup = data.ApplicationGroup! with { Gid = 999 },
        }));
        Assert.Equal(613u, Parse(data).ResourceAccessGid);
    }

    [Theory]
    [InlineData(0u, 65011u)]
    [InlineData(65010u, 0u)]
    [InlineData(uint.MaxValue, 65011u)]
    [InlineData(65010u, uint.MaxValue)]
    public void UserParserRejectsRootAndSentinelIdentities(uint uid, uint gid)
    {
        var name = LinuxRunAccountNames.Create(Run).Worker;
        AssertClosed(() => LinuxRunAccountUser.Parse(name, uid, gid, "/nonexistent", "/usr/sbin/nologin"));
        Assert.Equal(name, LinuxRunAccountUser.Parse(name, 65010, 65011, "/nonexistent", "/usr/sbin/nologin").Name);
    }

    [Fact]
    public void IdentityParserRejectsLoginShellBadHomeAndRegisteredGroupMembers()
    {
        var names = LinuxRunAccountNames.Create(Run);
        AssertClosed(() => LinuxRunAccountUser.Parse(names.Worker, 600, 601, "/nonexistent", "/bin/sh"));
        AssertClosed(() => LinuxRunAccountUser.Parse(names.Worker, 600, 601, "relative", "/usr/sbin/nologin"));
        AssertClosed(() => LinuxRunAccountUser.Parse(names.Worker, 600, 601, "/private-canary\n", "/usr/sbin/nologin"));
        AssertClosed(() => LinuxRunAccountGroup.Parse(names.Worker, 601, default));
        AssertClosed(() => LinuxRunAccountGroup.Parse(names.Results, 602, [names.Worker]));
        AssertClosed(() => LinuxRunAccountGroup.Parse(names.Results, 0, []));
        AssertClosed(() => LinuxRunAccountGroup.Parse(names.Results, uint.MaxValue, []));
        Assert.Empty(LinuxRunAccountGroup.Parse(names.Results, 602, []).Members);
    }

    [Fact]
    public void CompleteSnapshotRevalidatesConstructedRecordsInsteadOfTrustingDataConstructors()
    {
        var data = Data();
        AssertClosed(() => Parse(data with { Worker = data.Worker with { Uid = 0 } }));
        AssertClosed(() => Parse(data with { Subject = data.Subject with { Shell = "/bin/sh" } }));
        AssertClosed(() => Parse(data with { Results = data.Results with { Members = [data.Names.Worker] } }));
        Assert.Equal(612u, Parse(data).ResultsGid);
    }

    [Fact]
    public void SnapshotRejectsUidAndEveryGidCollisionWithAdjacentValidData()
    {
        var data = Data();
        AssertClosed(() => Parse(data with { Subject = data.Subject with { Uid = data.Worker.Uid } }));
        AssertClosed(() => Parse(data with
        {
            Subject = data.Subject with { Gid = data.Worker.Gid },
            SubjectGroup = data.SubjectGroup with { Gid = data.Worker.Gid },
        }));
        AssertClosed(() => Parse(data with { Results = data.Results with { Gid = data.Worker.Gid } }));
        AssertClosed(() => Parse(data with { Results = data.Results with { Gid = data.Subject.Gid } }));
        Assert.Equal(65011u, Parse(data).WorkerGid);
    }

    [Fact]
    public void SnapshotRejectsCrossRunNamesAndPrimaryGroupMismatch()
    {
        var data = Data();
        var other = LinuxRunAccountNames.Create(Guid.Parse("b1234567-89ab-cdef-0123-456789abcdef"));
        AssertClosed(() => Parse(data with { Worker = data.Worker with { Name = other.Worker } }));
        AssertClosed(() => Parse(data with { Subject = data.Subject with { Name = data.Names.Worker } }));
        AssertClosed(() => Parse(data with { WorkerGroup = data.WorkerGroup with { Name = data.Names.Subject } }));
        AssertClosed(() => Parse(data with { Results = data.Results with { Name = other.Results } }));
        AssertClosed(() => Parse(data with { WorkerGroup = data.WorkerGroup with { Gid = 777 } }));
        AssertClosed(() => Parse(data with { SubjectGroup = data.SubjectGroup with { Gid = 777 } }));
        Assert.Equal(610u, Parse(data).SubjectUid);
    }

    [Fact]
    public void InvalidInputRetainsNoRawNameOrInnerException()
    {
        var error = Assert.Throws<LinuxRunAccountException>(() =>
            LinuxRunAccountCommand.Create(LinuxRunAccountOperation.DeleteUser, "private-account-canary"));
        Assert.Equal(LinuxRunAccountFailure.InvalidData, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-account-canary", error.ToString());
    }

    [Fact]
    public void FactoryCannotAcceptMissingActualOwnerToCreateADataLease()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = LinuxRunAccounts.CreateAsync(null!, default); });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = LinuxRunAccounts.CreateAsync(null!, default, includeApplication: true);
        });
        Assert.Throws<ArgumentNullException>(() => new LinuxRunAccountReservations(null!));
    }

    /// <summary>An unknown prefix is rejected even when the generated hex suffix and full length remain unchanged.</summary>
    [Fact]
    public void GeneratedAccountNameRejectsUnknownPrefixWithTheSameHexSuffixAndLength()
    {
        var valid = LinuxRunAccountNames.Create(Run).Worker;
        Assert.True(LinuxRunAccountNames.IsGenerated(valid));
        var changed = "evq" + valid[3..];
        Assert.Equal(valid.Length, changed.Length);
        Assert.Equal(valid[3..], changed[3..]);
        Assert.False(LinuxRunAccountNames.IsGenerated(changed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void UserHomeRejectsBothLengthBoundariesWithAnAdjacentMaximumLengthNeighbor(int length)
    {
        var name = LinuxRunAccountNames.Create(Run).Worker;
        var home = length == 0 ? string.Empty : "/" + new string('a', length - 1);
        var error = Assert.Throws<LinuxRunAccountException>(() =>
            LinuxRunAccountUser.Parse(name, 600, 601, home, "/usr/sbin/nologin"));
        Assert.Equal(LinuxRunAccountFailure.InvalidData, error.Failure);
        Assert.Null(error.InnerException);
        var maximum = "/" + new string('a', 4095);
        Assert.Equal(maximum, LinuxRunAccountUser.Parse(name, 600, 601, maximum, "/usr/sbin/nologin").Home);
        Assert.Equal("/", LinuxRunAccountUser.Parse(name, 600, 601, "/", "/usr/sbin/nologin").Home);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MissingOptionalGroupOrUserCannotFallBackToTheDefaultProfile(int present)
    {
        var data = Data(includeApplication: true);
        var incomplete = data with
        {
            Application = (present & 1) == 0 ? null : data.Application,
            ApplicationGroup = (present & 2) == 0 ? null : data.ApplicationGroup,
            ResourceAccessGroup = (present & 4) == 0 ? null : data.ResourceAccessGroup,
        };
        var error = Assert.Throws<LinuxRunAccountException>(() => Parse(incomplete));
        Assert.Equal(LinuxRunAccountFailure.IdentityMismatch, error.Failure);
        Assert.Null(error.InnerException);
        Assert.Equal(620u, Parse(data).ApplicationUid);
        Assert.Equal(0u, Parse(Data()).ApplicationUid);
    }

    [Fact]
    public void SnapshotRejectsAValidForeignSubjectGroupRatherThanOnlyMalformedGroupData()
    {
        var data = Data();
        var other = LinuxRunAccountNames.Create(Guid.Parse("b1234567-89ab-cdef-0123-456789abcdef"));
        var foreign = LinuxRunAccountGroup.Parse(other.Subject, data.Subject.Gid, []);
        var error = Assert.Throws<LinuxRunAccountException>(() => Parse(data with { SubjectGroup = foreign }));
        Assert.Equal(LinuxRunAccountFailure.IdentityMismatch, error.Failure);
        Assert.Equal(data.Subject.Gid, Parse(data).SubjectGid);
    }

    [Fact]
    public void NumericUserAndGroupDomainsMayOverlapWhileEachDomainStillRequiresUniqueMembers()
    {
        var data = Data(includeApplication: true);
        var crossDomain = data with
        {
            Worker = data.Worker with { Uid = data.SubjectGroup.Gid },
            Subject = data.Subject with { Uid = data.Results.Gid },
            Application = data.Application! with { Uid = data.ResourceAccessGroup!.Gid },
        };
        var snapshot = Parse(crossDomain);
        Assert.Equal(snapshot.SubjectGid, snapshot.WorkerUid);
        Assert.Equal(snapshot.ResultsGid, snapshot.SubjectUid);
        Assert.Equal(snapshot.ResourceAccessGid, snapshot.ApplicationUid);
        var error = Assert.Throws<LinuxRunAccountException>(() => Parse(crossDomain with
        {
            Application = crossDomain.Application! with { Uid = crossDomain.Subject.Uid },
        }));
        Assert.Equal(LinuxRunAccountFailure.IdentityMismatch, error.Failure);
    }

    [Fact]
    public void RevalidatedOptionalRecordsRejectAnInvalidUserAndEachInvalidGroupIndependently()
    {
        var data = Data(includeApplication: true);
        var invalid = new[]
        {
            data with { Application = data.Application! with { Shell = "/bin/sh" } },
            data with { ApplicationGroup = data.ApplicationGroup! with { Members = default } },
            data with { ResourceAccessGroup = data.ResourceAccessGroup! with { Gid = uint.MaxValue } },
        };
        foreach (var changed in invalid)
        {
            var error = Assert.Throws<LinuxRunAccountException>(() => Parse(changed));
            Assert.Equal(LinuxRunAccountFailure.InvalidData, error.Failure);
            Assert.Null(error.InnerException);
        }
        Assert.Equal(613u, Parse(data).ResourceAccessGid);
    }

    private static IdentityData Data(bool includeApplication = false)
    {
        var names = LinuxRunAccountNames.Create(Run);
        return new(names,
            LinuxRunAccountUser.Parse(names.Worker, 65010, 65011, "/nonexistent", "/usr/sbin/nologin"),
            LinuxRunAccountUser.Parse(names.Subject, 610, 611, "/nonexistent", "/usr/sbin/nologin"),
            LinuxRunAccountGroup.Parse(names.Worker, 65011, []),
            LinuxRunAccountGroup.Parse(names.Subject, 611, []),
            LinuxRunAccountGroup.Parse(names.Results, 612, []),
            includeApplication ? LinuxRunAccountUser.Parse(names.Application, 620, 621, "/nonexistent", "/usr/sbin/nologin") : null,
            includeApplication ? LinuxRunAccountGroup.Parse(names.Application, 621, []) : null,
            includeApplication ? LinuxRunAccountGroup.Parse(names.ResourceAccess, 613, []) : null);
    }

    private static LinuxRunAccountSnapshot Parse(IdentityData data) => LinuxRunAccountSnapshot.Parse(data.Names,
        data.Worker, data.Subject, data.WorkerGroup, data.SubjectGroup, data.Results, data.Application,
        data.ApplicationGroup, data.ResourceAccessGroup);

    private static void AssertClosed(Action action)
    {
        var error = Assert.Throws<LinuxRunAccountException>(action);
        Assert.Null(error.InnerException);
    }

    private sealed record IdentityData(LinuxRunAccountNames Names, LinuxRunAccountUser Worker,
        LinuxRunAccountUser Subject, LinuxRunAccountGroup WorkerGroup, LinuxRunAccountGroup SubjectGroup,
        LinuxRunAccountGroup Results, LinuxRunAccountUser? Application, LinuxRunAccountGroup? ApplicationGroup,
        LinuxRunAccountGroup? ResourceAccessGroup);
}
