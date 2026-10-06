using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Irreversible contention/replay bookkeeping, never an authenticated owner or execution capability.</summary>
/// <remarks>
/// An actual owner retains this private latch for its entire lifetime. Claim before any asynchronous
/// operation or external side effect. There is no release, retry or successful-failure reset. Constructing
/// this data primitive cannot construct an owner, account lease or admission; portable tests exercise only
/// atomic bookkeeping. The authenticated owner supplies all native identity/deadline checks separately.
/// </remarks>
internal sealed class SupervisionSingleAttempt
{
    private int _claimed;

    /// <summary>Irreversibly claims the only attempt; concurrent and subsequent calls reject before dispatch.</summary>
    internal void Claim()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0)
            throw new EvidenceAdmissionException("ASEVD410", "The protected single-use operation was already claimed.");
    }
}
