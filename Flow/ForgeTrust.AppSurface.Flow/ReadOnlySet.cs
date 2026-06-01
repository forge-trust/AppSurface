using System.Collections;

namespace ForgeTrust.AppSurface.Flow;

internal sealed class ReadOnlySet<T> : IReadOnlySet<T>
{
    private readonly HashSet<T> _inner;

    internal ReadOnlySet(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _inner = new HashSet<T>(values);
    }

    public int Count => _inner.Count;

    public bool Contains(T item) => _inner.Contains(item);

    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<T> other) => _inner.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _inner.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => _inner.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _inner.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _inner.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _inner.SetEquals(other);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
