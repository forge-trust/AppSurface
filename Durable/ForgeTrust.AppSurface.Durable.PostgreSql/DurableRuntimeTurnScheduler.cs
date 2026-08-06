using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Maintains process-local fair selection order for bounded runtime Turns.</summary>
internal sealed class DurableRuntimeTurnScheduler
{
    private static readonly DurableRuntimeSurface[] All =
    [
        DurableRuntimeSurface.Work,
        DurableRuntimeSurface.Flow,
        DurableRuntimeSurface.Schedule,
    ];

    private readonly object _sync = new();
    private int _nextIndex;

    internal DurableRuntimeSurface Next(DurableRuntimeSurface selectedSurfaces)
    {
        if (selectedSurfaces == DurableRuntimeSurface.None
            || (selectedSurfaces & ~DurableRuntimeSurface.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedSurfaces));
        }

        lock (_sync)
        {
            for (var offset = 0; offset < All.Length; offset++)
            {
                var index = (_nextIndex + offset) % All.Length;
                var surface = All[index];
                if ((selectedSurfaces & surface) == 0)
                {
                    continue;
                }

                _nextIndex = (index + 1) % All.Length;
                return surface;
            }
        }

        throw new InvalidOperationException("A nonempty durable surface selection contained no defined surface.");
    }
}
