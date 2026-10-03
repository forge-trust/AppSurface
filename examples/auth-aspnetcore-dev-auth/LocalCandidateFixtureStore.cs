// docs:snippet devauth-persona-fixture-store:start
namespace AuthAspNetCoreDevAuthExample;

/// <summary>
/// Holds one synthetic, process-local scenario independently of the selected persona.
/// </summary>
/// <remarks>
/// All transitions use a short synchronous lock. Activation and product actions only move readiness or completed
/// work forward; reads never initialize data. Restarting the host resets the scenario. This sample supplies neither
/// durable storage nor cross-process idempotence; a consuming host must choose those policies itself.
/// </remarks>
internal sealed class LocalCandidateFixtureStore
{
    /// <summary>The fixed fixture identity shared by both operator roles.</summary>
    internal const string ScenarioKey = "candidate-review-demo-v1";

    private readonly object _gate = new();
    private LocalCandidateSnapshot? _candidate;

    // missing --Ensure--> one candidate --MarkReady(role)--> role ready --TryComplete(role)--> completed
    // Each arrow and Read holds only _gate; no logging, rendering, callbacks or await occurs inside it.
    // Returning immutable records keeps earlier reads stable after later forward-only transitions.

    /// <summary>Returns an immutable coherent snapshot, or null without creating a candidate.</summary>
    internal LocalCandidateSnapshot? Read()
    {
        lock (_gate)
        {
            return _candidate;
        }
    }

    /// <summary>Atomically ensures the shared candidate while preserving all existing readiness and work.</summary>
    internal LocalCandidateSnapshot Ensure()
    {
        lock (_gate)
        {
            return _candidate ??= new LocalCandidateSnapshot("synthetic-candidate-001", false, false, false, false);
        }
    }

    /// <summary>Marks an already ensured candidate ready for one configured operator; never creates data.</summary>
    /// <param name="personaId">The validated configured labeler or reviewer ID.</param>
    /// <exception cref="InvalidOperationException">No candidate exists, or the operator is unknown.</exception>
    internal void MarkReady(string personaId)
    {
        lock (_gate)
        {
            var candidate = _candidate ?? throw new InvalidOperationException("Ensure the scenario before marking readiness.");
            _candidate = personaId switch
            {
                "labeler" => candidate with { LabelerReady = true },
                "reviewer" => candidate with { ReviewerReady = true },
                _ => throw new InvalidOperationException("Unknown scenario operator."),
            };
        }
    }

    /// <summary>Completes only the ready operator's work; repeated completion is idempotent.</summary>
    /// <param name="personaId">The configured operator ID chosen by the authorized host route.</param>
    /// <returns>False for missing data, an unknown role or a role that is not ready. No data is ensured.</returns>
    internal bool TryComplete(string personaId)
    {
        lock (_gate)
        {
            if (_candidate is not { } candidate || !candidate.IsReady(personaId))
            {
                return false;
            }

            _candidate = personaId == "labeler"
                ? candidate with { LabelingCompleted = true }
                : candidate with { ReviewCompleted = true };
            return true;
        }
    }
}

/// <summary>An immutable point-in-time view of one shared candidate and its independent role state.</summary>
/// <param name="Id">Stable synthetic candidate ID.</param>
/// <param name="LabelerReady">Whether labeler activation has finished.</param>
/// <param name="ReviewerReady">Whether reviewer activation has finished.</param>
/// <param name="LabelingCompleted">Whether labeling work has completed.</param>
/// <param name="ReviewCompleted">Whether review work has completed.</param>
internal sealed record LocalCandidateSnapshot(
    string Id,
    bool LabelerReady,
    bool ReviewerReady,
    bool LabelingCompleted,
    bool ReviewCompleted)
{
    /// <summary>Checks readiness for a configured role; unknown roles are never ready.</summary>
    internal bool IsReady(string personaId) => personaId switch
    {
        "labeler" => LabelerReady,
        "reviewer" => ReviewerReady,
        _ => false,
    };
}
// docs:snippet devauth-persona-fixture-store:end
