namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Coordinates process-local pass admission with synchronous shutdown initiation.</summary>
internal sealed class DurableRuntimeAdmissionGate
{
    private readonly object _sync = new();
    private bool _closed;

    /// <summary>Returns whether a new pass may start without acquiring or reserving any separate release handle.</summary>
    /// <remarks>A successful result permits admission only; callers must still serialize active passes independently.</remarks>
    internal bool TryEnter()
    {
        lock (_sync)
        {
            return !_closed;
        }
    }

    /// <summary>Rejects future admissions without waiting for or cancelling an in-flight pass.</summary>
    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }

    /// <summary>Allows future admissions after a controlled drain rollback or recovery decision.</summary>
    internal void Reopen()
    {
        lock (_sync)
        {
            _closed = false;
        }
    }
}
