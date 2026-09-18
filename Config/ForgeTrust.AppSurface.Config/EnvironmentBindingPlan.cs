using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Immutable per-type public binding metadata; contains no environment state or values.</summary>
internal sealed class EnvironmentBindingPlan
{
    private static readonly ConcurrentDictionary<Type, EnvironmentBindingPlan> Cache = new();

    private EnvironmentBindingPlan(Type type)
    {
        Members = Array.AsReadOnly(type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => new EnvironmentBindingMember(property))
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Select(field => new EnvironmentBindingMember(field)))
            .OrderBy(member => member.Name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets non-indexed public properties and fields, including readable aggregates without setters.</summary>
    internal IReadOnlyList<EnvironmentBindingMember> Members { get; }
    /// <summary>Gets one immutable plan, safely shared by concurrent operations.</summary>
    internal static EnvironmentBindingPlan For(Type type) => Cache.GetOrAdd(type, static value => new(value));

    /// <summary>Gets the supported sequence element type; dictionaries bind as complete JSON values.</summary>
    internal static Type? CollectionElementType(Type type)
    {
        if (type.IsArray) return type.GetArrayRank() == 1 ? type.GetElementType() : null;
        if (!type.IsGenericType) return null;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>)
            || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(ICollection<>) ? type.GetGenericArguments()[0] : null;
    }

    /// <summary>Gets whether a type supports recursive public-member patching.</summary>
    internal static bool IsComplex(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal)
            && type != typeof(DateTime) && type != typeof(DateTimeOffset) && type != typeof(TimeSpan)
            && type != typeof(Guid) && !typeof(IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>Constructs an ordinary public instance, or reports an unsupported construction path.</summary>
    internal static object? Create(Type type)
    {
        if (type.IsAbstract || type.IsInterface) return null;
        try { return Activator.CreateInstance(type); }
        catch (Exception exception) when (exception is MissingMethodException or MemberAccessException or TargetInvocationException or NotSupportedException or ArgumentException)
        { return null; }
    }

    /// <summary>Builds a supported array or assignable list without invoking private setters.</summary>
    internal static object? MaterializeCollection(Type type, Type elementType, IList list)
    {
        if (type.IsArray)
        {
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        return type.IsInstanceOfType(list) ? list : null;
    }

    /// <summary>Checks whether public list operations can replace a getter-only collection.</summary>
    internal static bool IsMutableList(object? value) => value is IList { IsReadOnly: false, IsFixedSize: false };

    /// <summary>Replaces an isolated mutable list through its public collection contract.</summary>
    internal static void ReplaceList(IList target, IEnumerable values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    /// <summary>Obtains prior collection presence without assuming a concrete implementation.</summary>
    internal static int? CollectionCount(object? value)
    {
        if (value is ICollection collection) return collection.Count;
        var contract = value?.GetType().GetInterfaces().FirstOrDefault(type => type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>));
        return contract?.GetProperty(nameof(IReadOnlyCollection<object>.Count))?.GetValue(value) as int?;
    }
}

/// <summary>One public patchable field/property and its exact native segment.</summary>
internal sealed class EnvironmentBindingMember
{
    private readonly PropertyInfo? _property;
    private readonly FieldInfo? _field;

    internal EnvironmentBindingMember(PropertyInfo property)
    {
        _property = property;
        Name = property.Name;
        Type = property.PropertyType;
        CanWrite = property.SetMethod?.IsPublic == true;
    }

    internal EnvironmentBindingMember(FieldInfo field)
    {
        _field = field;
        Name = field.Name;
        Type = field.FieldType;
        CanWrite = !field.IsInitOnly;
    }

    internal string Name { get; }
    internal Type Type { get; }
    internal bool CanWrite { get; }
    internal string NativeName => Name.ToUpperInvariant();
    internal object? Read(object target) => _field is not null ? _field.GetValue(target)
        : _property!.GetMethod?.IsPublic == true ? _property.GetValue(target) : null;
    internal void Write(object target, object? value)
    {
        if (_field is not null) _field.SetValue(target, value);
        else _property!.SetValue(target, value);
    }
}
