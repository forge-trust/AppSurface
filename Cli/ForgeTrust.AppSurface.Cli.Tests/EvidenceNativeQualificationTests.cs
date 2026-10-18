using System.Reflection.Metadata;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Build-selection and original callback controls; no native owner, lease or accepted proof.</summary>
public sealed class EvidenceNativeQualificationTests
{
    /// <summary>Reads finished metadata without invoking the deliberate private stall.</summary>
    [Fact]
    public void SynchronousNativeStallIsAbsentFromOrdinaryProductImages()
    {
        using var file = File.OpenRead(Path.Join(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Evidence.Cli.dll"));
        using var image = new System.Reflection.PortableExecutable.PEReader(file);
        var reader = image.GetMetadataReader();
        var holder = Assert.Single(reader.TypeDefinitions.Select(reader.GetTypeDefinition),
            type => reader.GetString(type.Name) == "EvidenceFixedSynchronousInputFactory");
        var methods = holder.GetMethods().Select(handle => reader.GetString(reader.GetMethodDefinition(handle).Name)).ToArray();
#if EVIDENCE_PRIVATE_N11
        Assert.Contains("StallBeforeReturningTask", methods);
        Assert.NotNull(EvidenceFixedSynchronousInputFactory.CreateForProtectedRole());
#else
        Assert.DoesNotContain("StallBeforeReturningTask", methods);
        Assert.Null(EvidenceFixedSynchronousInputFactory.CreateForProtectedRole());
#endif
        Assert.Contains("WriteInputFactoryEnteredMarker", methods);
        Assert.Contains("RegisterDisposer", methods);
    }

#if !EVIDENCE_PRIVATE_N04
    [Fact]
    public async Task OrdinaryChangedPeerRejectionDoesNotWaitForAnOpenSocketOrSendRequestBytes()
    {
        // This negative operation seam grants no root authentication, supervisor or admission.
        // A live peer deliberately keeps its write side open; ordinary rejection must not read it.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        var accepted = listener.AcceptSocketAsync(deadline.Token).AsTask();
        await client.ConnectAsync(listener.LocalEndpoint, deadline.Token);
        using var peer = await accepted;

        var rejection = EvidenceLinuxWorkerSupervisor.RejectChangedRootPeerAsync(client, 1, deadline.Token);
        Assert.True(rejection.IsCompleted, "Ordinary peer rejection must not wait for replacement socket input.");
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => rejection.AsTask());
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.Contains("The protected broker identity changed.", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, peer.Available);
        Assert.False(peer.Poll(0, System.Net.Sockets.SelectMode.SelectRead));
    }

    [Fact]
    public void OrdinaryChangedPeerRejectionRequiresItsOriginalSocket()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = EvidenceLinuxWorkerSupervisor.RejectChangedRootPeerAsync(null!, 1, CancellationToken.None);
        });
        Assert.Equal("socket", error.ParamName);
    }
#endif

#if EVIDENCE_PRIVATE_ACCEPTED_WORK
    [Fact]
    public void PrivateAcceptancePhaseIsOnlyDetachedDataWithAnExactClosedShape()
    {
        using var frame = System.Text.Json.JsonDocument.Parse("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":true}");
        EvidenceLinuxWorkerSupervisor.ValidateAcceptedBlockedWorkPhase(frame.RootElement);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"phase\":\"work-accepted\",\"body_blocked\":true}")]
    [InlineData("{\"ok\":true,\"phase\":\"canary-accepted\",\"body_blocked\":true}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":false}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":\"canary\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":true,\"callback\":\"canary\"}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"body_blocked\":true,\"body_blocked\":true}")]
    [InlineData("{\"ok\":true,\"phase\":\"work-accepted\",\"Body_blocked\":true}")]
    public void PrivateAcceptancePhaseRejectsIncompleteUnclosedAndUnblockedData(string input)
    {
        using var frame = System.Text.Json.JsonDocument.Parse(input);
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ValidateAcceptedBlockedWorkPhase(frame.RootElement));
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message);
    }
#endif

    [Fact]
    public async Task CompiledImageOwnsOnlyItsSelectedCheckpoint()
    {
#if EVIDENCE_PRIVATE_N04
        var expected = EvidenceNativeQualificationKind.PeerReplacement;
#elif EVIDENCE_PRIVATE_N05
        var expected = EvidenceNativeQualificationKind.OccupiedWorkerOutputSlot;
#elif EVIDENCE_PRIVATE_N06
        var expected = EvidenceNativeQualificationKind.SymlinkWorkerOutputSlot;
#elif EVIDENCE_PRIVATE_N07
        var expected = EvidenceNativeQualificationKind.ParentIdentitySubstitution;
#elif EVIDENCE_PRIVATE_N08
        var expected = EvidenceNativeQualificationKind.CancellationBeforeAllocation;
#elif EVIDENCE_PRIVATE_N09
        var expected = EvidenceNativeQualificationKind.CancellationBeforeActivation;
#elif EVIDENCE_PRIVATE_N10
        var expected = EvidenceNativeQualificationKind.PendingStartRace;
#elif EVIDENCE_PRIVATE_N11
        var expected = EvidenceNativeQualificationKind.SynchronousWorkerStall;
#elif EVIDENCE_PRIVATE_N12
        var expected = EvidenceNativeQualificationKind.LeaderExitWithDescendant;
#elif EVIDENCE_PRIVATE_N15
        var expected = EvidenceNativeQualificationKind.PendingStartOwnerDeath;
#elif EVIDENCE_PRIVATE_N16
        var expected = EvidenceNativeQualificationKind.AcceptedBlockedWork;
#elif EVIDENCE_PRIVATE_C2_ARTIFACT_MUTATION
        var expected = EvidenceNativeQualificationKind.C2ArtifactMutation;
#elif EVIDENCE_PRIVATE_N13
        var expected = EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork;
#elif EVIDENCE_PRIVATE_N14
        var expected = EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork;
#else
        var expected = EvidenceNativeQualificationKind.None;
#endif
        Assert.Equal(expected, EvidenceNativeQualification.Current);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PeerReplacement,
            EvidenceNativeQualification.PeerReplacementEnabled);
        Assert.Equal(expected is EvidenceNativeQualificationKind.OccupiedWorkerOutputSlot
            or EvidenceNativeQualificationKind.SymlinkWorkerOutputSlot, EvidenceNativeQualification.SlotNegativeEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.ParentIdentitySubstitution,
            EvidenceNativeQualification.ParentReplacementEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.LeaderExitWithDescendant,
            EvidenceNativeQualification.DescendantEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PendingStartRace,
            EvidenceNativeQualification.PendingStartRaceEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.SynchronousWorkerStall,
            EvidenceNativeQualification.WorkerStallEnabled);
        Assert.Equal(expected is EvidenceNativeQualificationKind.AcceptedBlockedWork
                or EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork
                or EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork,
            EvidenceNativeQualification.AcceptedBlockedWorkEnabled);
        Assert.Equal(expected is EvidenceNativeQualificationKind.OwnerKilledDuringAcceptedWork
                or EvidenceNativeQualificationKind.OwnerStoppedDuringAcceptedWork,
            EvidenceNativeQualification.OwnerDeathAcceptedWorkEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.AcceptedBlockedWork,
            EvidenceNativeQualification.N16StopWaitEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.PendingStartOwnerDeath,
            EvidenceNativeQualification.PendingStartOwnerDeathEnabled);
        Assert.Equal(expected == EvidenceNativeQualificationKind.C2ArtifactMutation,
            EvidenceNativeQualification.C2ArtifactMutationEnabled);
        Assert.Equal(EvidenceNativeQualification.WorkerStallEnabled,
            EvidenceFixedSynchronousInputFactory.CreateForProtectedRole() is not null);
        var checkpoint = EvidenceNativeQualification.CreateCancellationCheckpoint();
        if (expected is not (EvidenceNativeQualificationKind.CancellationBeforeAllocation
            or EvidenceNativeQualificationKind.CancellationBeforeActivation))
        {
            Assert.False(EvidenceNativeQualification.CancellationEnabled);
            Assert.Null(checkpoint); // Ordinary worker creates no wait, marker or signal prerequisite.
            return;
        }

        Assert.True(EvidenceNativeQualification.CancellationEnabled);
        Assert.NotNull(checkpoint);
        var phase = expected == EvidenceNativeQualificationKind.CancellationBeforeAllocation
            ? EvidenceOriginalCancellationPhase.BeforeAllocation : EvidenceOriginalCancellationPhase.BeforeActivation;
        Assert.Equal(EvidenceOriginalCancellationCheckpoint.SelectedPhase, phase);
        using var caller = new CancellationTokenSource();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var error = new MemoryStream();
        var callback = checkpoint.WaitAtAsync(phase, error, stage.Token, caller.Token).AsTask();
        try
        {
            Assert.False(callback.IsCompleted);
            Assert.Equal(EvidenceOriginalCancellationCheckpoint.Frame, error.ToArray());
            caller.Cancel();
        }
        finally
        {
            caller.Cancel();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.True(caller.IsCancellationRequested);
    }
}
