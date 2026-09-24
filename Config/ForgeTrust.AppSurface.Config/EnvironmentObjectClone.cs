using System.Collections;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Copies public values into normally constructed objects while isolating every mutable member.</summary>
/// <remarks>
/// Getter-only aggregates are populated through their existing public members and collection methods.
/// A getter must not expose the source's mutable storage; readonly state must survive public construction unchanged.
/// Cycles, excessive depth, and aggregates that cannot be isolated fail the whole transaction.
/// No compiler-generated fields or private accessors are inspected or rewritten.
/// </remarks>
internal sealed class EnvironmentObjectClone(int maxDepth, CancellationToken cancellationToken)
{
    private readonly Dictionary<object, object> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _visiting = new(ReferenceEqualityComparer.Instance);

    /// <summary>Copies a root using its runtime type and public construction/serialization contracts.</summary>
    internal object Copy(object source, Type type) => CopyValue(source, type, null, 0)!;

    private object? CopyValue(object? source, Type type, object? existing, int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null || (source is not IEnumerable && !EnvironmentBindingPlan.IsComplex(source.GetType())) || source is string) return source;
        if (ReferenceEquals(source, existing)) throw new EnvironmentCloneException();
        if (depth >= maxDepth || _visiting.Contains(source)) throw new EnvironmentCloneException();
        if (_copies.TryGetValue(source, out var copied)) return copied;
        _visiting.Add(source);
        try
        {
            object clone;
            if (source is IEnumerable values && EnvironmentBindingPlan.CollectionElementType(type) is { } elementType)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                foreach (var item in values) list.Add(CopyValue(item, item?.GetType() ?? elementType, null, depth + 1));
                if (EnvironmentBindingPlan.IsMutableList(existing))
                {
                    EnvironmentBindingPlan.ReplaceList((IList)existing!, list);
                    clone = existing!;
                }
                else if (existing is Array array)
                {
                    if (array.Length != list.Count) throw new EnvironmentCloneException();
                    list.CopyTo(array, 0);
                    clone = array;
                }
                // CollectionElementType admits only arrays and contracts assignable from this list.
                else clone = EnvironmentBindingPlan.MaterializeCollection(type, elementType, list)!;
            }
            else if (source is IDictionary dictionary)
            {
                var copy = existing as IDictionary ?? EnvironmentBindingPlan.Create(source.GetType()) as IDictionary
                    ?? throw new EnvironmentCloneException();
                if (ReferenceEquals(copy, source)) throw new EnvironmentCloneException();
                copy.Clear();
                foreach (DictionaryEntry entry in dictionary)
                    copy.Add(CopyValue(entry.Key, entry.Key.GetType(), null, depth + 1)!,
                        CopyValue(entry.Value, entry.Value?.GetType() ?? typeof(object), null, depth + 1));
                clone = copy;
            }
            else
            {
                clone = existing ?? EnvironmentBindingPlan.Create(source.GetType())
                    ?? JsonSerializer.Deserialize(JsonSerializer.Serialize(source, type), type) ?? throw new EnvironmentCloneException();
                if (ReferenceEquals(clone, source)) throw new EnvironmentCloneException();
                foreach (var member in EnvironmentBindingPlan.For(source.GetType()).Members)
                {
                    var value = member.Read(source);
                    if (!member.CanWrite)
                    {
                        var aggregate = member.Read(clone);
                        if (value is not null && aggregate is not null &&
                            (EnvironmentBindingPlan.IsComplex(member.Type) || value is IEnumerable && value is not string))
                        {
                            var copiedAggregate = CopyValue(value, member.Type, aggregate, depth + 1);
                            if (!ReferenceEquals(aggregate, copiedAggregate)) throw new EnvironmentCloneException();
                        }
                        else if (!Equals(value, aggregate))
                        {
                            throw new EnvironmentCloneException();
                        }

                        continue;
                    }

                    member.Write(clone, CopyValue(value, member.Type, null, depth + 1));
                }
            }

            _copies.Add(source, clone);
            return clone;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new EnvironmentCloneException();
        }
        finally { _visiting.Remove(source); }
    }
}

/// <summary>Signals an isolation failure without retaining the input value or a raw exception.</summary>
internal sealed class EnvironmentCloneException : Exception;
