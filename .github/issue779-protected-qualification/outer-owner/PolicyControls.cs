namespace EvidenceHost.CompletedIsolation;

/// <summary>Portable one-use state and path controls; actual containment remains a real Linux CI obligation.</summary>
internal static class PolicyControls
{
    internal static int Run()
    {
        var count = 0;
        var good = new PhaseResult(0, true, false, false, "fixed.service", 1, "/out", "/err");
        var transition = new OwnerTransition();
        transition.Prepared(good); transition.Replayed(good); transition.CustodyVerified();
        transition.RequireCloseAllowed(); transition.Closed(good); count++;
        foreach (var failed in new[] { good with { Settled = false }, good with { ExitCode = 1 },
            good with { TimedOut = true }, good with { OutputOverflow = true } })
        {
            var candidate = new OwnerTransition();
            candidate.Prepared(good);
            Reject(() => candidate.Replayed(failed));
            Reject(candidate.RequireCloseAllowed);
            Reject(candidate.CustodyVerified); count += 3;
        }
        var beforeReplay = new OwnerTransition();
        Reject(beforeReplay.RequireCloseAllowed);
        Reject(() => beforeReplay.Prepared(good)); count += 2;
        var beforeVerification = new OwnerTransition();
        beforeVerification.Prepared(good); beforeVerification.Replayed(good);
        Reject(beforeVerification.RequireCloseAllowed);
        Reject(beforeVerification.CustodyVerified); count += 2;
        var duplicate = new OwnerTransition(); duplicate.Prepared(good);
        Reject(() => duplicate.Prepared(good)); Reject(() => duplicate.Replayed(good)); count += 2;
        var layout = new OwnerLayout("/owned", "/owned/fs", 62000, 62000, OwnerPaths.Assembly, "/evidence");
        if (layout.Host(OwnerPaths.Source) != "/owned/fs" + OwnerPaths.Source) throw new IsolationException("path-control");
        count++;
        foreach (var invalid in new[] { "/", "relative", "/a/../b", "/a/./b", "/a//b", "/a/", "/a\nb", "" })
        {
            try { _ = layout.Host(invalid); }
            catch (InvalidDataException) { count++; continue; }
            throw new IsolationException("path-rejection-control");
        }
        var context = new Dictionary<string, string>
        {
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_EVENT_NAME"] = "workflow_run",
            ["GITHUB_REPOSITORY"] = "forge-trust/AppSurface",
            ["GITHUB_REF"] = "refs/heads/main",
            ["GITHUB_REF_PROTECTED"] = "true",
            ["GITHUB_WORKFLOW_REF"] =
                "forge-trust/AppSurface/.github/workflows/evidencehost-completed-validation.yml@refs/heads/main",
            ["GITHUB_REPOSITORY_ID"] = "1035215851",
            ["GITHUB_SHA"] = new string('a', 40),
            ["GITHUB_WORKFLOW_SHA"] = new string('a', 40),
        };
        var validated = OwnerCompletionPolicy.Validate(context);
        if (validated.Count != OwnerCompletionPolicy.ContextNames.Length) throw new IsolationException("event-policy-control");
        count++;
        foreach (var name in OwnerCompletionPolicy.ContextNames)
        {
            var missing = new Dictionary<string, string>(context); missing.Remove(name);
            Reject(() => OwnerCompletionPolicy.Validate(missing)); count++;
            var wrong = new Dictionary<string, string>(context) { [name] = "canary-invalid" };
            Reject(() => OwnerCompletionPolicy.Validate(wrong)); count++;
        }
        count += NativeParserControls();
        count += ProcMountControls();
        count += GitMountControls();
        count += WritableMountControls();
        count += ProcessIdentityParserControls();
        count += CgroupSettlementControls();
        count += SettlementDiagnosticControls();
        count += ReleaseControls();
        count += ReadOnlyRenamePolicyControls();
        count += SourceIndexControls();
        count += SdkDirectoryControls();
        foreach (var phase in new[] { OwnerPhase.Prepare, OwnerPhase.Replay, OwnerPhase.Close })
        {
            if (BootstrapCommand.Prefix(phase).Count != 0) throw new IsolationException("bootstrap-phase-control");
            count++;
        }
        if (!BootstrapCommand.Prefix(OwnerPhase.Control).SequenceEqual(
                ["/usr/bin/bash", "-c", BootstrapCommand.DiagnosticScript, "isolation-bootstrap"])
            || !BootstrapCommand.DiagnosticScript.EndsWith("exec \"$@\"", StringComparison.Ordinal))
            throw new IsolationException("bootstrap-command-control");
        count++;
        var bootstrapStart = new System.Diagnostics.ProcessStartInfo("/usr/bin/systemd-run");
        BootstrapCommand.ConfigureCapabilities(bootstrapStart);
        if (!bootstrapStart.ArgumentList.SequenceEqual([
                "--property=CapabilityBoundingSet=CAP_SYS_ADMIN CAP_SETUID CAP_SETGID CAP_SETPCAP CAP_SYS_CHROOT",
                "--property=AmbientCapabilities=CAP_SETUID"]))
            throw new IsolationException("bootstrap-capability-control");
        count++;
        if (Program.SafeFailureCode(new IsolationException("proc-numeric-field")) != "proc-numeric-field"
            || Program.SafeFailureCode(new IsolationException("private-credential-canary")) != "closed-runtime-failure"
            || Program.SafeFailureCode(new Exception("private-credential-canary")) != "runtime-failure"
            || Program.SafeFailureCode(new IsolationException("phase-unsettled")) != "phase-unsettled"
            || Program.SafeFailureCode(new IsolationException("phase-unsettled:private-credential-canary")) != "closed-runtime-failure")
            throw new IsolationException("diagnostic-redaction-control");
        count += 5;
        foreach (var code in new[]
        {
            "handoff-unexpected-node", "stage-count", "stage-changed", "input-immutable",
            "host-canary-changed", "tracked-file-changed", "tracked-git-dirty",
            "git-metadata-changed", "root-name-changed",
            "immutable-replace-source-shape", "immutable-replace-source-size",
            "immutable-replace-unexpected-success", "immutable-replace-unexpected-error",
            "immutable-replace-source-changed", "immutable-replace-destination-exists",
            "immutable-replace-destination-stat",
            "token-environment", "host-credential-hidden", "host-credential-readable",
            "manager-hidden", "root-home-hidden", "runner-home-hidden",
            "private-proc", "private-init", "token-process",
            "ready-pid", "unit-cgroup", "unit-process-count", "unit-process-facts", "unit-launcher-ambiguous",
            "unit-launcher-facts", "unit-launcher-security", "unit-bootstrap-security", "unit-bootstrap-argv",
            "unit-root-identity", "unit-namespace-identity", "unit-mount-namespace",
            "unit-readonly-mount", "unit-readwrite-mount", "unit-git-mount", "unit-preparation-mount",
            "unit-sdk-mount", "unit-cgroup-open", "unit-cgroup-type", "unit-cgroup-files",
            "unit-cgroup-initial", "launcher-pidfd", "launcher-pidfd-poll",
            "proc-link", "proc-fields", "proc-fields-format", "mountinfo-format",
            "statx-handle", "statx-path", "native-command-start", "native-input-size",
            "cgroup-depth", "cgroup-path", "cgroup-process-limit", "cgroup-proc-duplicate",
            "cgroup-procs", "cgroup-events-format", "cgroup-read", "cgroup-seek", "cgroup-size"
        })
        {
            if (Program.SafeFailureCode(new IsolationException(code)) != code
                || Program.SafeFailureCode(new IsolationException(code + ":private-credential-canary")) != "closed-runtime-failure"
                || Program.SafeFailureCode(new Exception(code)) != "runtime-failure")
                throw new IsolationException("native-diagnostic-control");
            count += 3;
        }
        Console.WriteLine("ISOLATION_POLICY_CONTROLS_PASSED:" + count);
        return 0;
    }

    private static int NativeParserControls()
    {
        const string status = "Name:\tdotnet\nNSpid:\t41237\t1\nUid:\t62000\t62000\t62000\t62000\n"
            + "Gid:\t62000\t62000\t62000\t62000\nGroups:\t62000\nNoNewPrivs:\t1\n"
            + "CapEff:\t0000000000000000\n";
#pragma warning disable CA1416 // These parser seams are pure wire parsers and run in portable policy controls.
        var parsed = NativeFacts.ParseProcStatusFields(System.Text.Encoding.ASCII.GetBytes(status));
#pragma warning restore CA1416
        if (!parsed.TryGetValue("NSpid", out var nspid) || !nspid.SequenceEqual(["41237", "1"])
            || !parsed.TryGetValue("Uid", out var uid) || uid.Length != 4 || uid[0] != "62000"
            || !parsed.TryGetValue("NoNewPrivs", out var nnp) || !nnp.SequenceEqual(["1"]))
            throw new IsolationException("proc-status-positive-control");

        var events = System.Text.Encoding.ASCII.GetBytes("populated 0\nfrozen 0\n");
#pragma warning disable CA1416 // This parser seam validates fixed cgroup.events text without touching Linux state.
        var cgroup = NativeFacts.ParseCgroupEvents(events);
#pragma warning restore CA1416
        if (!cgroup.TryGetValue("populated", out var populated) || !populated.SequenceEqual(["0"])
            || !cgroup.TryGetValue("frozen", out var frozen) || !frozen.SequenceEqual(["0"]))
            throw new IsolationException("cgroup-events-positive-control");

        RejectProc("Name: dotnet\nNSpid: 12 1\nNSpid: 13 1\n");
        RejectProc("Name: dotnet\nnot-a-status-field\n");
        RejectEvents("populated 0\nfrozen 0\npopulated 0\n");
        RejectEvents("populated 0\nfrozen 2\n");
        RejectEvents("populated 0\nunknown 0\n");
        RejectEvents("populated 0\n");
        return 8;

        static void RejectProc(string value)
        {
#pragma warning disable CA1416 // Pure status parser negative control.
            try { _ = NativeFacts.ParseProcStatusFields(System.Text.Encoding.ASCII.GetBytes(value)); }
            catch (IsolationException) { return; }
#pragma warning restore CA1416
            throw new IsolationException("proc-status-negative-control");
        }

        static void RejectEvents(string value)
        {
#pragma warning disable CA1416 // Pure cgroup-events parser negative control.
            try { _ = NativeFacts.ParseCgroupEvents(System.Text.Encoding.ASCII.GetBytes(value)); }
            catch (IsolationException) { return; }
#pragma warning restore CA1416
            throw new IsolationException("cgroup-events-negative-control");
        }
    }

    private static int ProcMountControls()
    {
        const string hidden = "403 402 0:146 / /proc rw,nosuid,nodev,noexec - proc proc rw\n";
        const string visible = "463 403 0:125 / /proc rw,nosuid,nodev,noexec - proc proc rw\n";
#pragma warning disable CA1416 // This seam parses supplied mountinfo and mount ID facts without touching Linux state.
        if (!NativeFacts.HasVisibleProcMount(System.Text.Encoding.ASCII.GetBytes(visible), 463)
            || !NativeFacts.HasVisibleProcMount(System.Text.Encoding.ASCII.GetBytes(hidden + visible), 463)
            || !NativeFacts.HasVisibleProcMount(System.Text.Encoding.ASCII.GetBytes(visible + hidden), 463))
            throw new IsolationException("proc-mount-positive-control");
        foreach (var (table, id) in new (string, ulong)[]
        {
            (hidden + visible, 0), (hidden + visible, 999), (hidden, 463), ("", 463),
            (hidden + visible.Replace("- proc ", "- tmpfs ", StringComparison.Ordinal), 463),
            (visible.Replace(" /proc ", " /other ", StringComparison.Ordinal), 463),
        })
            if (NativeFacts.HasVisibleProcMount(System.Text.Encoding.ASCII.GetBytes(table), id))
                throw new IsolationException("proc-mount-negative-control");
        foreach (var table in new[]
        {
            hidden + hidden, visible.Replace("463 ", "0 ", StringComparison.Ordinal),
            visible.Replace("463 ", "+463 ", StringComparison.Ordinal),
            visible.Replace("463 ", "18446744073709551616 ", StringComparison.Ordinal),
            "invalid\n"
        })
        {
            try { _ = NativeFacts.HasVisibleProcMount(System.Text.Encoding.ASCII.GetBytes(table), 463); }
            catch (IsolationException) { continue; }
            throw new IsolationException("proc-mount-format-control");
        }
#pragma warning restore CA1416
        return 14;
    }

    private static int GitMountControls()
    {
        var layout = new OwnerLayout("/owned", "/owned/fs", 62000, 62000, OwnerPaths.Assembly, "/evidence");
#pragma warning disable CA1416 // Fixed binding projections and mount parsers do not touch native state.
        string[] commonBindings =
            ["/var/lib/appsurface-completed-dotnet:/usr/share/dotnet",
             "/owned/fs" + OwnerPaths.Source + "/.git:" + OwnerPaths.Source + "/.git",
             "/owned/fs" + OwnerPaths.Native + "/.git:" + OwnerPaths.Native + "/.git"];
        if (!PhaseRunner.ReadOnlyBindings(layout, "/var/lib/appsurface-completed-dotnet", OwnerPhase.Prepare)
            .SequenceEqual(commonBindings))
            throw new IsolationException("git-bindings-control");
        foreach (var phase in new[] { OwnerPhase.Replay, OwnerPhase.Control, OwnerPhase.Close })
            if (!PhaseRunner.ReadOnlyBindings(layout, "/var/lib/appsurface-completed-dotnet", phase)
                .SequenceEqual(commonBindings.Append("/owned/fs" + OwnerPaths.Preparation + ":" + OwnerPaths.Preparation)))
                throw new IsolationException("preparation-bindings-control");
        foreach (var path in new[] { OwnerPaths.Source + "/.git", OwnerPaths.Native + "/.git", OwnerPaths.Preparation })
        {
            var hidden = "10 9 0:1 / " + path + " rw - ext4 /dev/loop0 rw\n";
            var visible = "11 10 0:1 / " + path + " ro - ext4 /dev/loop0 rw\n";
            foreach (var table in new[] { visible, hidden + visible, visible + hidden })
                if (!NativeFacts.HasVisibleReadonlyMount(System.Text.Encoding.ASCII.GetBytes(table), path, 11))
                    throw new IsolationException("git-mount-positive-control");
            foreach (var (table, id) in new (string, ulong)[]
            {
                (hidden + visible, 10), (visible, 0), (visible, 12), ("", 11),
                (visible.Replace(" ro ", " rw ", StringComparison.Ordinal), 11),
                (visible.Replace(path, "/other", StringComparison.Ordinal), 11),
                (visible.Replace(path, path[..path.LastIndexOf('/')], StringComparison.Ordinal), 11),
                (hidden.Replace(" rw -", " ro -", StringComparison.Ordinal)
                    + visible.Replace(" ro -", " rw -", StringComparison.Ordinal), 11)
            })
                if (NativeFacts.HasVisibleReadonlyMount(System.Text.Encoding.ASCII.GetBytes(table), path, id))
                    throw new IsolationException("git-mount-negative-control");
        }
#pragma warning restore CA1416
        return 37;
    }

    private static int WritableMountControls()
    {
        var count = 0;
        var layout = new OwnerLayout("/owned", "/owned/fs", 62000, 62000, OwnerPaths.Assembly, "/evidence");
        var all = new[] { OwnerPaths.Validator, OwnerPaths.Input, OwnerPaths.Source, OwnerPaths.Native,
            OwnerPaths.Source + "/.git", OwnerPaths.Native + "/.git", OwnerPaths.Stage, OwnerPaths.Handoff,
            OwnerPaths.Output, OwnerPaths.Report, "/tmp" };
#pragma warning disable CA1416 // Fixed argument projections and mount parsers exercise no Linux state.
        foreach (var phase in new[] { OwnerPhase.Prepare, OwnerPhase.Replay, OwnerPhase.Control, OwnerPhase.Close })
        {
            var writable = phase == OwnerPhase.Close
                ? new[] { OwnerPaths.Report, "/tmp" }
                : phase == OwnerPhase.Prepare
                    ? new[] { OwnerPaths.Source, OwnerPaths.Native, OwnerPaths.Stage, OwnerPaths.Handoff, OwnerPaths.Output, "/tmp" }
                    : new[] { OwnerPaths.Source, OwnerPaths.Native, OwnerPaths.Handoff, OwnerPaths.Output, "/tmp" };
            var readonlyPaths = all.Except(writable).ToList();
            if (phase != OwnerPhase.Prepare) readonlyPaths.Add(OwnerPaths.Preparation);
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/systemd-run");
            PhaseRunner.ConfigurePathAccess(start, layout, phase);
            if (!start.ArgumentList.SequenceEqual([
                    "--property=ReadWritePaths=" + string.Join(' ', writable.Select(path => "+" + path)),
                    "--property=ReadOnlyPaths=" + string.Join(' ', readonlyPaths.Select(path => "+" + path)),
                    "--property=BindPaths=" + string.Join(' ', writable.Select(path => "/owned/fs" + path + ":" + path))]))
                throw new IsolationException("phase-path-access-control");
            count++;
            foreach (var path in writable)
            {
                var hidden = "10 9 0:1 / " + path + " ro - ext4 /dev/loop0 rw\n";
                var visible = "11 10 0:1 / " + path + " rw - ext4 /dev/loop0 rw\n";
                foreach (var table in new[] { visible, hidden + visible, visible + hidden })
                {
                    if (!NativeFacts.HasVisibleReadwriteMount(System.Text.Encoding.ASCII.GetBytes(table), path, 11))
                        throw new IsolationException("writable-mount-positive-control");
                    count++;
                }
                foreach (var (table, id) in new (string, ulong)[]
                {
                    (hidden + visible, 10), (visible, 0), (visible, 12), ("", 11),
                    (visible.Replace(" rw -", " ro -", StringComparison.Ordinal), 11),
                    (visible.Replace(path, "/other", StringComparison.Ordinal), 11),
                    (hidden.Replace(" ro -", " rw -", StringComparison.Ordinal)
                        + visible.Replace(" rw -", " ro -", StringComparison.Ordinal), 11)
                })
                {
                    if (NativeFacts.HasVisibleReadwriteMount(System.Text.Encoding.ASCII.GetBytes(table), path, id))
                        throw new IsolationException("writable-mount-negative-control");
                    count++;
                }
            }
        }
#pragma warning restore CA1416
        return count;
    }

    private static int ProcessIdentityParserControls()
    {
        const string tabSeparated = "Name:\tdotnet\nUid:\t62000\t62000\t62000\t62000\n"
            + "Gid:\t62000\t62000\t62000\t62000\nGroups:\t62000\t62001\n";
        if (!OwnerCustody.ContainsId(tabSeparated, 62000) || OwnerCustody.ContainsId(tabSeparated, 62002))
            throw new IsolationException("process-id-positive-control");

        const string mixedWhitespace = "Uid: 62001\t62002 62003\t62004\n"
            + "Gid:\t62001 62002\t62003 62004\nGroups:\t62005\t62006\n";
        if (!OwnerCustody.ContainsId(mixedWhitespace, 62006) || OwnerCustody.ContainsId(mixedWhitespace, 62000))
            throw new IsolationException("process-id-whitespace-control");

        RejectProcessStatus("Uid:\t62000\t62000\t62000\t62000\nGid:\t62000\t62000\t62000\t62000\n");
        RejectProcessStatus("Uid:\t62000\t62000\t62000\t62000\nGid:\t62000\t62000\t62000\t62000\nGroups:\t\nGroups:\t62000\n");
        RejectProcessStatus("Uid:\t+62000\t62000\t62000\t62000\nGid:\t62000\t62000\t62000\t62000\nGroups:\t62000\n");
        RejectProcessStatus("Uid:\t4294967296\t62000\t62000\t62000\nGid:\t62000\t62000\t62000\t62000\nGroups:\t62000\n");
        RejectProcessStatus("Uid:\t62000\t62000\t62000\t62000\nGid:\t62000\t62000\t62000\t62000\nGroups:\t62000\tinvalid\n");
        return 7;

        static void RejectProcessStatus(string status)
        {
            try { _ = OwnerCustody.ContainsId(status, 62000); }
            catch (IsolationException) { return; }
            throw new IsolationException("process-id-negative-control");
        }
    }

    private static int CgroupSettlementControls()
    {
#pragma warning disable CA1416 // The decision function is pure and tests portable representations of Linux cgroup facts.
        if (!NativeFacts.HasStatxMountId(0x17ff) || NativeFacts.HasStatxMountId(0x7ff))
            throw new IsolationException("statx-mount-id-mask-control");
        const string visibleCgroupMount = "41 30 0:42 / /sys/fs/cgroup rw,nosuid,nodev - cgroup2 cgroup rw\n";
        if (!NativeFacts.HasVisibleCgroup2Mount(System.Text.Encoding.ASCII.GetBytes(visibleCgroupMount), 41)
            || NativeFacts.HasVisibleCgroup2Mount(System.Text.Encoding.ASCII.GetBytes(visibleCgroupMount), 42)
            || NativeFacts.HasVisibleCgroup2Mount(System.Text.Encoding.ASCII.GetBytes(
                "41 30 0:42 / /sys/fs/cgroup rw - tmpfs tmpfs rw\n"), 41)
            || NativeFacts.HasVisibleCgroup2Mount(System.Text.Encoding.ASCII.GetBytes(
                "41 30 0:42 / /elsewhere rw - cgroup2 cgroup rw\n"), 41))
            throw new IsolationException("cgroup-root-mount-controls");
        var expected = new NativeFacts.CgroupIdentity(8, 21, 34, 2, IsDirectory: true);
        var linkedPath = new NativeFacts.CgroupPathIdentity(ErrorFree: true, Missing: false, expected);
        var removedPath = new NativeFacts.CgroupPathIdentity(ErrorFree: true, Missing: true, default);
        var replacementPath = new NativeFacts.CgroupPathIdentity(ErrorFree: true, Missing: false,
            new(8, 55, 34, 2, IsDirectory: true));
        var badPath = new NativeFacts.CgroupPathIdentity(ErrorFree: false, Missing: false, default);
        if (!NativeFacts.DecideCgroupSettlement(expected, linkedPath, expected, launcherExited: true,
                eventEmpty: true, noProcesses: true))
            throw new IsolationException("cgroup-linked-settlement-control");
        if (!NativeFacts.DecideCgroupSettlement(expected with { Links = 0 }, removedPath, expected,
                launcherExited: true, eventEmpty: false, noProcesses: false))
            throw new IsolationException("cgroup-removed-settlement-control");

        const string removedHostPath = "/sys/fs/cgroup/system.slice/appsurface-completed-replay-0123456789abcdef0123456789abcdef.service";
        var removedObservation = Observe(expected, removedPath, removedHostPath);
        if (!NativeFacts.DecideRemovedCgroupSettlement(expected, removedHostPath, removedHostPath,
                removedObservation, removedObservation))
            throw new IsolationException("cgroup-deleted-dentry-settlement-control");

        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected, replacementPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected, badPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected with { Inode = 99 }, removedPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected with { MountId = 35 }, removedPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected with { IsDirectory = false }, removedPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected with { Links = 0 }, removedPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath, Observe(expected, linkedPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath, descriptorPath: "/tmp/foreign"), removedObservation);
        RejectDeleted(expected, removedHostPath, "/tmp/foreign-capture", removedObservation, removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath, descriptorPath: removedHostPath + " (deleted) extra"), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath, launcherExited: false), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath, trustedProcfs: false), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath, originalCgroupMountStillVisible: false), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected with { Device = 9 }, removedPath, removedHostPath), removedObservation);
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath), Observe(expected, replacementPath, removedHostPath));
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath), Observe(expected, badPath, removedHostPath));
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath), Observe(expected, removedPath, removedHostPath, launcherExited: false));
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath), Observe(expected with { Inode = 99 }, removedPath, removedHostPath));
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath), Observe(expected, removedPath, removedHostPath, trustedProcfs: false));
        RejectDeleted(expected, removedHostPath, removedHostPath,
            Observe(expected, removedPath, removedHostPath), Observe(expected, removedPath, removedHostPath, originalCgroupMountStillVisible: false));
        RejectDeleted(expected, "/sys/fs/cgroup/system.slice/foreign.service", "/sys/fs/cgroup/system.slice/foreign.service",
            removedObservation, removedObservation);
        const string childPath = "/sys/fs/cgroup/system.slice/appsurface-completed-replay-0123456789abcdef0123456789abcdef.service/child";
        RejectDeleted(expected, childPath, childPath, removedObservation, removedObservation);
        const string invalidGuidPath = "/sys/fs/cgroup/system.slice/appsurface-completed-replay-0123456789abcdef0123456789abcdeg.service";
        RejectDeleted(expected, invalidGuidPath, invalidGuidPath,
            Observe(expected, removedPath, invalidGuidPath), Observe(expected, removedPath, invalidGuidPath));

        RejectSettlement(expected, linkedPath, launcherExited: false, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected, linkedPath, launcherExited: true, eventEmpty: false, noProcesses: true);
        RejectSettlement(expected, linkedPath, launcherExited: true, eventEmpty: true, noProcesses: false);
        RejectSettlement(expected, replacementPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Links = 0 }, linkedPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Links = 0 }, removedPath, launcherExited: false, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Inode = 99, Links = 0 }, removedPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Links = 0 }, badPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        return 40;

        static void RejectSettlement(NativeFacts.CgroupIdentity held, NativeFacts.CgroupPathIdentity path,
            bool launcherExited, bool eventEmpty, bool noProcesses)
        {
            if (NativeFacts.DecideCgroupSettlement(held, path,
                    new NativeFacts.CgroupIdentity(8, 21, 34, 2, IsDirectory: true),
                    launcherExited, eventEmpty, noProcesses))
                throw new IsolationException("cgroup-settlement-negative-control");
        }

        static NativeFacts.CgroupRemovalObservation Observe(NativeFacts.CgroupIdentity held,
            NativeFacts.CgroupPathIdentity path, string hostPath, bool launcherExited = true,
            bool trustedProcfs = true, bool originalCgroupMountStillVisible = true, string? descriptorPath = null) =>
            new(held, path, launcherExited, descriptorPath ?? hostPath + " (deleted)", trustedProcfs,
                originalCgroupMountStillVisible);

        static void RejectDeleted(NativeFacts.CgroupIdentity expected, string hostPath, string capturedPath,
            NativeFacts.CgroupRemovalObservation first, NativeFacts.CgroupRemovalObservation second)
        {
            if (NativeFacts.DecideRemovedCgroupSettlement(expected, hostPath, capturedPath, first, second))
                throw new IsolationException("cgroup-deleted-dentry-negative-control");
        }
#pragma warning restore CA1416
    }

    private static int SettlementDiagnosticControls()
    {
#pragma warning disable CA1416 // These controls exercise pure bounded diagnostic formatting, not Linux facts.
        var expected = new NativeFacts.CgroupIdentity(8, 21, 34, 2, IsDirectory: true);
        const string unknown = "launcher=unknown;held=unknown;links=unknown;path=unknown;events=unknown;processes=unknown;"
            + "descriptor=unknown;procfs=unknown;cgroup-root=unknown;error=none";
        var diagnostic = new SettlementDiagnostic();
        diagnostic.Launcher(exited: true);
        diagnostic.Held(expected, expected);
        diagnostic.Path(new NativeFacts.CgroupPathIdentity(ErrorFree: true, Missing: false, expected), expected);
        diagnostic.Events(empty: true);
        diagnostic.Processes(empty: true);
        diagnostic.Error(new IOException("private-credential-canary"));
        if (diagnostic.Format() != unknown)
            throw new IsolationException("settlement-diagnostic-inactive-control");

        diagnostic.Begin();
        diagnostic.Launcher(exited: true);
        diagnostic.Held(expected, expected);
        diagnostic.Path(new NativeFacts.CgroupPathIdentity(ErrorFree: true, Missing: false, expected), expected);
        diagnostic.Events(empty: false);
        diagnostic.Processes(empty: false);
        diagnostic.Error(new IOException("private-credential-canary"));
        if (diagnostic.Format() != "launcher=exited;held=matching;links=nonzero;path=same;events=populated;processes=present;"
            + "descriptor=unknown;procfs=unknown;cgroup-root=unknown;error=io-failure"
            || diagnostic.Format().Contains("private-credential-canary", StringComparison.Ordinal))
            throw new IsolationException("settlement-diagnostic-populated-control");

        diagnostic.Begin();
        if (diagnostic.Format() != unknown)
            throw new IsolationException("settlement-diagnostic-reset-control");

        diagnostic.Begin();
        diagnostic.Launcher(exited: true);
        diagnostic.Held(expected, expected);
        diagnostic.Path(new NativeFacts.CgroupPathIdentity(true, true, default), expected);
        diagnostic.RemovalEvidence(trustedProcfs: true, capturedPathExact: true, deletedPathExact: true);
        diagnostic.CgroupRoot(matches: true);
        if (diagnostic.Format() != "launcher=exited;held=matching;links=nonzero;path=missing;events=unknown;processes=unknown;"
            + "descriptor=deleted-exact;procfs=trusted;cgroup-root=matching;error=none"
            || diagnostic.Format().Contains("/sys/fs/cgroup", StringComparison.Ordinal))
            throw new IsolationException("settlement-diagnostic-deleted-control");

        var mismatchRows = new[]
        {
            (Held: expected with { Inode = 99 }, Path: new NativeFacts.CgroupPathIdentity(true, false, expected),
                Launcher: false, EventEmpty: false, ProcessesEmpty: true,
                Expected: "launcher=live;held=mismatch;links=nonzero;path=same;events=populated;processes=empty;descriptor=unknown;procfs=unknown;cgroup-root=unknown;error=none"),
            (Held: expected with { IsDirectory = false, Links = 0 }, Path: new NativeFacts.CgroupPathIdentity(true, true, default),
                Launcher: true, EventEmpty: true, ProcessesEmpty: false,
                Expected: "launcher=exited;held=mismatch;links=zero;path=missing;events=empty;processes=present;descriptor=unknown;procfs=unknown;cgroup-root=unknown;error=none"),
            (Held: expected with { Links = 0 }, Path: new NativeFacts.CgroupPathIdentity(false, false, default),
                Launcher: false, EventEmpty: false, ProcessesEmpty: false,
                Expected: "launcher=live;held=matching;links=zero;path=error;events=populated;processes=present;descriptor=unknown;procfs=unknown;cgroup-root=unknown;error=none"),
            (Held: expected, Path: new NativeFacts.CgroupPathIdentity(true, false, expected with { Inode = 99 }),
                Launcher: true, EventEmpty: true, ProcessesEmpty: true,
                Expected: "launcher=exited;held=matching;links=nonzero;path=replaced;events=empty;processes=empty;descriptor=unknown;procfs=unknown;cgroup-root=unknown;error=none"),
        };
        foreach (var row in mismatchRows)
        {
            diagnostic.Begin();
            diagnostic.Launcher(row.Launcher);
            diagnostic.Held(row.Held, expected);
            diagnostic.Path(row.Path, expected);
            diagnostic.Events(row.EventEmpty);
            diagnostic.Processes(row.ProcessesEmpty);
            if (diagnostic.Format() != row.Expected)
                throw new IsolationException("settlement-diagnostic-state-control");
        }

        var errorRows = new (Exception Error, string Expected)[]
        {
            (new IOException("private-credential-canary"), "io-failure"),
            (new UnauthorizedAccessException("private-path-canary"), "access-denied"),
            (new System.ComponentModel.Win32Exception("private-process-canary"), "process-start-failure"),
            (new EntryPointNotFoundException("private-entrypoint-canary"), "interop-entrypoint-missing"),
            (new IsolationException("phase-unsettled"), "phase-unsettled"),
            (new IsolationException("phase-unsettled:private-token-canary"), "closed-runtime-failure"),
            (new Exception("private-exception-canary"), "runtime-failure"),
        };
        foreach (var row in errorRows)
        {
            diagnostic.Begin();
            diagnostic.Error(row.Error);
            var formatted = diagnostic.Format();
            if (!formatted.EndsWith("error=" + row.Expected, StringComparison.Ordinal)
                || formatted.Contains("private-", StringComparison.Ordinal))
                throw new IsolationException("settlement-diagnostic-error-control");
        }
        return 15;
#pragma warning restore CA1416
    }

    private static int ReleaseControls()
    {
        var context = "\"context\":{" + string.Join(',', OwnerCompletionPolicy.ContextNames
            .Select(name => System.Text.Json.JsonSerializer.Serialize(name) + ":\"valid\"")) + "}";
        var prepare = "{\"phase\":\"prepare\",\"token\":\"synthetic-token\",\"control\":null," + context + "}\n";
        var close = prepare.Replace("prepare", "close", StringComparison.Ordinal);
        var replay = "{\"phase\":\"replay\",\"token\":null,\"control\":null,\"context\":null}\n";
        var control = "{\"phase\":\"control\",\"token\":null,\"control\":\"baseline\",\"context\":null}\n";
        Accept(prepare, OwnerPhase.Prepare, token: "synthetic-token");
        Accept(close, OwnerPhase.Close, token: "synthetic-token");
        Accept(replay, OwnerPhase.Replay);
        Accept(control, OwnerPhase.Control, control: "baseline");

        RejectRelease(prepare.Replace("\"phase\":\"prepare\",", "\"phase\":\"prepare\",\"phase\":\"prepare\",", StringComparison.Ordinal), OwnerPhase.Prepare);
        RejectRelease(prepare.Replace("\"context\":{", "\"unknown\":null,\"context\":{", StringComparison.Ordinal), OwnerPhase.Prepare);
        RejectRelease("{\"phase\":\"prepare\",\"token\":\"synthetic-token\",\"control\":null}\n", OwnerPhase.Prepare);
        RejectRelease(replay.Replace("\"token\":null", "\"token\":\"leak\"", StringComparison.Ordinal), OwnerPhase.Replay);
        RejectRelease(control.Replace("\"context\":null", context, StringComparison.Ordinal), OwnerPhase.Control);
        RejectRelease(control.Replace("baseline", "arbitrary-command", StringComparison.Ordinal), OwnerPhase.Control);
        RejectRelease(prepare, OwnerPhase.Close);
        RejectRelease(replay[..^1] + "x\n", OwnerPhase.Replay);
        RejectRelease(replay.Replace("}\n", "}\r\n", StringComparison.Ordinal), OwnerPhase.Replay);
        RejectRelease(replay.Replace("\"token\":null", "\"token\":7", StringComparison.Ordinal), OwnerPhase.Replay);
        RejectRelease(replay + new string(' ', 32 * 1024), OwnerPhase.Replay);
        RejectRelease(prepare.Replace("synthetic-token", new string('x', 8193), StringComparison.Ordinal), OwnerPhase.Prepare);
        RejectRelease(prepare.Replace("\"context\":{", "\"context\":{\"EXTRA\":\"invalid\",", StringComparison.Ordinal), OwnerPhase.Prepare);
        RejectRelease(prepare.Replace(",\"GITHUB_SHA\":\"valid\"", "", StringComparison.Ordinal), OwnerPhase.Prepare);
        return 18;

        static void Accept(string frame, OwnerPhase phase, string? token = null, string? control = null)
        {
#pragma warning disable CA1416 // ParseRelease is a pure wire parser; the platform annotation comes from its containing launcher.
            var parsed = PhaseLauncher.ParseRelease(System.Text.Encoding.UTF8.GetBytes(frame), phase);
#pragma warning restore CA1416
            if (parsed.Phase != phase.ToString().ToLowerInvariant() || parsed.Token != token || parsed.Control != control)
                throw new IsolationException("release-positive-control");
        }

        static void RejectRelease(string frame, OwnerPhase phase)
        {
#pragma warning disable CA1416 // ParseRelease is a pure wire parser; the platform annotation comes from its containing launcher.
            try { _ = PhaseLauncher.ParseRelease(System.Text.Encoding.UTF8.GetBytes(frame), phase); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return; }
#pragma warning restore CA1416
            throw new IsolationException("release-negative-control");
        }
    }

    private static int ReadOnlyRenamePolicyControls()
    {
        foreach (var errno in new[] { 13, 16, 30 })
        {
            if (!ReadOnlyRenameProbe.IsExpectedDenial(errno))
                throw new IsolationException("readonly-rename-denial-control");
        }
        foreach (var errno in new[] { 0, 1, 2, 5, 17, 18, 20, 22, 38, 95, -1 })
        {
            if (ReadOnlyRenameProbe.IsExpectedDenial(errno))
                throw new IsolationException("readonly-rename-unexpected-errno-control");
        }
        return 14;
    }

    private static int SourceIndexControls()
    {
        const string blob = "0123456789abcdef0123456789abcdef01234567";
        var rows = System.Text.Encoding.ASCII.GetBytes($"100644 {blob} 0\tregular.cs\0"
            + $"100755 {blob} 0\texecutable.sh\0");
        var parsed = OwnerCustody.ParseIndex(rows);
        if (!parsed.TryGetValue("regular.cs", out var regular) || regular.Blob != blob || regular.Mode != "0644"
            || !parsed.TryGetValue("executable.sh", out var executable) || executable.Blob != blob
            || executable.Mode != "0755")
            throw new IsolationException("source-index-mode-control");

        RejectIndex($"120000 {blob} 0\tsymlink\0");
        RejectIndex($"100644 {blob} 1\tnonzero-stage.cs\0");
        RejectIndex("malformed-row-without-tab-or-header\0");
        return 5;

        static void RejectIndex(string row)
        {
            try { _ = OwnerCustody.ParseIndex(System.Text.Encoding.ASCII.GetBytes(row)); }
            catch (IsolationException) { return; }
            throw new IsolationException("source-index-negative-control");
        }
    }

    private static int SdkDirectoryControls()
    {
#pragma warning disable CA1416 // The SDK directory decision seam consumes synthetic metadata and performs no native calls.
        var secureRoot = NativeFacts.IsTrustedSdkDirectoryFact("/var/lib", OwnerWorkspace.OwnedSdkDirectory,
            uid: 0, mode: 0x1ed, isDirectory: true);
        var oldWritableRoot = NativeFacts.IsTrustedSdkDirectoryFact("/opt", "/opt/appsurface-completed-dotnet",
            uid: 0, mode: 0x1ff, isDirectory: true);
        var writableSdkRoot = NativeFacts.IsTrustedSdkDirectoryFact(OwnerWorkspace.OwnedSdkDirectory,
            OwnerWorkspace.OwnedSdkDirectory, uid: 0, mode: 0x1ff, isDirectory: true);
        var nonRoot = NativeFacts.IsTrustedSdkDirectoryFact("/var/lib", OwnerWorkspace.OwnedSdkDirectory,
            uid: 1000, mode: 0x1ed, isDirectory: true);
        var symlink = NativeFacts.IsTrustedSdkDirectoryFact("/var/lib", OwnerWorkspace.OwnedSdkDirectory,
            uid: 0, mode: 0x41ed, isDirectory: false);
        var unrelated = NativeFacts.IsTrustedSdkDirectoryFact("/usr/share", OwnerWorkspace.OwnedSdkDirectory,
            uid: 0, mode: 0x1ed, isDirectory: true);
#pragma warning restore CA1416
        if (!secureRoot || oldWritableRoot || writableSdkRoot || nonRoot || symlink || unrelated)
            throw new IsolationException("sdk-directory-policy-control");

        OwnerWorkspace.MarkSdkValidationStage("sdk-path-validation");
        if (OwnerWorkspace.SetupStage != "sdk-path-validation"
            || Program.SafeFailureCode(new IsolationException("sdk-host-directory")) != "sdk-host-directory"
            || Program.SafeFailureCode(new IsolationException("sdk-host-path")) != "sdk-host-path"
            || Program.SafeFailureCode(new IsolationException("private-sdk-path")) != "closed-runtime-failure")
            throw new IsolationException("sdk-diagnostic-control");
        var rejectedStage = false;
        try { OwnerWorkspace.MarkSdkValidationStage("path=" + OwnerWorkspace.OwnedSdkDirectory); }
        catch (IsolationException) { rejectedStage = true; }
        if (!rejectedStage) throw new IsolationException("sdk-stage-closed-control");
        return 4;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (IsolationException) { return; }
        throw new IsolationException("transition-negative-control");
    }
}
