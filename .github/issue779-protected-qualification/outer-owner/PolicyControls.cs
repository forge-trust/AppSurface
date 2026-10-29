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
        count += ProcessIdentityParserControls();
        count += CgroupSettlementControls();
        count += ReleaseControls();
        count += SourceIndexControls();
        count += SdkDirectoryControls();
        if (Program.SafeFailureCode(new IsolationException("proc-numeric-field")) != "proc-numeric-field"
            || Program.SafeFailureCode(new IsolationException("private-credential-canary")) != "closed-runtime-failure"
            || Program.SafeFailureCode(new Exception("private-credential-canary")) != "runtime-failure")
            throw new IsolationException("diagnostic-redaction-control");
        count += 3;
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

        RejectSettlement(expected, linkedPath, launcherExited: false, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected, linkedPath, launcherExited: true, eventEmpty: false, noProcesses: true);
        RejectSettlement(expected, linkedPath, launcherExited: true, eventEmpty: true, noProcesses: false);
        RejectSettlement(expected, replacementPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Links = 0 }, linkedPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Links = 0 }, removedPath, launcherExited: false, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Inode = 99, Links = 0 }, removedPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        RejectSettlement(expected with { Links = 0 }, badPath, launcherExited: true, eventEmpty: true, noProcesses: true);
        return 10;

        static void RejectSettlement(NativeFacts.CgroupIdentity held, NativeFacts.CgroupPathIdentity path,
            bool launcherExited, bool eventEmpty, bool noProcesses)
        {
            if (NativeFacts.DecideCgroupSettlement(held, path,
                    new NativeFacts.CgroupIdentity(8, 21, 34, 2, IsDirectory: true),
                    launcherExited, eventEmpty, noProcesses))
                throw new IsolationException("cgroup-settlement-negative-control");
        }
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
