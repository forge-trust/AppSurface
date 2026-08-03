namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Coordinates process-local pass admission with synchronous shutdown initiation.</summary>
internal sealed class DurableRuntimeAdmissionGate
{
    private readonly object _sync = new();
    private bool _closed;

    internal bool TryEnter()
    {
        lock (_sync)
        {
            return !_closed;
        }
    }

    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }

    internal void Reopen()
    {
        lock (_sync)
        {
            _closed = false;
        }
    }
}
