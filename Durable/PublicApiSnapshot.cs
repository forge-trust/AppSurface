using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace ForgeTrust.AppSurface.Durable.Tests.Support;

/// <summary>Creates deterministic text snapshots of public API declarations for checked-in contract review.</summary>
internal static class PublicApiSnapshot
{
    private static readonly NullabilityInfoContext Nullability = new();

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Describes every visible type in <paramref name="assembly"/> in ordinal order.</summary>
    /// <param name="assembly">Assembly whose public and protected contract is inspected.</param>
    /// <returns>Deterministically ordered declaration lines.</returns>
    internal static string[] Create(Assembly assembly) => assembly
        .GetTypes()
        .Where(IsVisibleType)
        .OrderBy(FormatType, StringComparer.Ordinal)
        .SelectMany(DescribeType)
        .ToArray();

    /// <summary>Asserts the generated snapshot matches its copied baseline, or updates the trusted source baseline.</summary>
    /// <param name="assembly">Assembly whose contract is inspected.</param>
    /// <param name="copiedBaselineName">Baseline copied into the test output directory.</param>
    /// <param name="sourceRelativePath">Trusted repository-relative baseline path used only in explicit update mode.</param>
    /// <remarks>Set <c>APPSURFACE_UPDATE_PUBLIC_API_BASELINES=1</c> only for an intentional reviewed API update.</remarks>
    /// <exception cref="IOException">Thrown when a baseline cannot be read or written.</exception>
    /// <exception cref="Xunit.Sdk.EqualException">Thrown when the reviewed and generated snapshots differ.</exception>
    internal static void AssertMatches(Assembly assembly, string copiedBaselineName, string sourceRelativePath)
    {
        var actual = Create(assembly);
        if (string.Equals(
            Environment.GetEnvironmentVariable("APPSURFACE_UPDATE_PUBLIC_API_BASELINES"),
            "1",
            StringComparison.Ordinal))
        {
            File.WriteAllLines(Path.Join(FindRepositoryRoot(), sourceRelativePath), actual);
            return;
        }

        var expected = File.ReadAllLines(Path.Join(AppContext.BaseDirectory, copiedBaselineName));
        Assert.Equal(expected, actual);
    }

    /// <summary>Describes one visible type declaration and its visible declared members.</summary>
    /// <param name="type">Type to inspect.</param>
    /// <returns>The type declaration followed by ordinally sorted member declarations.</returns>
    internal static string[] DescribeType(Type type)
    {
        var lines = new List<string> { DescribeTypeDeclaration(type) };
        lines.AddRange(DescribeMembers(type).Order(StringComparer.Ordinal).Select(static member => $"  {member}"));
        return lines.ToArray();
    }

    private static IEnumerable<string> DescribeMembers(Type type)
    {
        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type).Order(StringComparer.Ordinal))
            {
                var value = Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
                yield return $"enum-member {name} = {Convert.ToString(value, CultureInfo.InvariantCulture)}";
            }

            yield break;
        }

        foreach (var constructor in type.GetConstructors(DeclaredMembers).Where(IsVisible))
        {
            yield return $"{FormatApiAttributes(constructor)}constructor {Accessibility(constructor)} {FormatType(type)}({FormatParameters(constructor.GetParameters())})";
        }

        foreach (var field in type.GetFields(DeclaredMembers).Where(IsVisible))
        {
            var modifiers = field.IsLiteral
                ? " const"
                : field.IsStatic
                    ? " static"
                    : string.Empty;
            var readOnly = field.IsInitOnly ? " readonly" : string.Empty;
            var value = field.IsLiteral ? $" = {FormatDefaultValue(field.GetRawConstantValue(), field.FieldType)}" : string.Empty;
            yield return $"{FormatApiAttributes(field)}field {Accessibility(field)}{modifiers}{readOnly} {FormatType(Nullability.Create(field), CreateNullableCursor(field.CustomAttributes, field))} {field.Name}{value}";
        }

        foreach (var property in type.GetProperties(DeclaredMembers).Where(IsVisible))
        {
            var accessors = new List<string>(2);
            if (property.GetMethod is { } getter && IsVisible(getter))
            {
                accessors.Add($"get: {Accessibility(getter)}");
            }

            if (property.SetMethod is { } setter && IsVisible(setter))
            {
                var accessorKind = setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit))
                    ? "init"
                    : "set";
                accessors.Add($"{accessorKind}: {Accessibility(setter)}");
            }

            var index = property.GetIndexParameters();
            var name = index.Length == 0 ? property.Name : $"this[{FormatParameters(index)}]";
            var propertyModifiers = FormatAccessorModifiers(property.GetMethod ?? property.SetMethod!);
            yield return $"{FormatApiAttributes(property)}property {Accessibility(property)}{propertyModifiers} {FormatType(Nullability.Create(property), CreateNullableCursor(property.CustomAttributes, property))} {name} {{ {string.Join("; ", accessors)}; }}";
        }

        foreach (var eventInfo in type.GetEvents(DeclaredMembers).Where(IsVisible))
        {
            var accessors = new List<string>(2);
            if (eventInfo.AddMethod is { } add && IsVisible(add))
            {
                accessors.Add($"add: {Accessibility(add)}");
            }

            if (eventInfo.RemoveMethod is { } remove && IsVisible(remove))
            {
                accessors.Add($"remove: {Accessibility(remove)}");
            }

            var eventModifiers = FormatAccessorModifiers(eventInfo.AddMethod ?? eventInfo.RemoveMethod!);
            yield return $"{FormatApiAttributes(eventInfo)}event {Accessibility(eventInfo)}{eventModifiers} {FormatType(Nullability.Create(eventInfo), CreateNullableCursor(eventInfo.CustomAttributes, eventInfo))} {eventInfo.Name} {{ {string.Join("; ", accessors)}; }}";
        }

        foreach (var method in type.GetMethods(DeclaredMembers).Where(IsVisible).Where(static method => !IsAccessor(method)))
        {
            var modifiers = new List<string>();
            if (method.IsStatic)
            {
                modifiers.Add("static");
            }

            if (method.IsAbstract)
            {
                modifiers.Add("abstract");
            }
            else if (method.IsVirtual && !method.IsFinal && method.GetBaseDefinition() == method)
            {
                modifiers.Add("virtual");
            }

            var modifierText = modifiers.Count == 0 ? string.Empty : $" {string.Join(" ", modifiers)}";
            var genericArguments = method.IsGenericMethodDefinition
                ? $"<{string.Join(", ", method.GetGenericArguments().Select(static argument => argument.Name))}>"
                : string.Empty;
            var constraints = FormatGenericConstraints(method.GetGenericArguments());
            yield return $"{FormatApiAttributes(method)}method {Accessibility(method)}{modifierText} {FormatReturnType(method)} {method.Name}{genericArguments}({FormatParameters(method.GetParameters(), method.IsDefined(typeof(ExtensionAttribute), inherit: false))}){constraints}";
        }
    }

    private static string DescribeTypeDeclaration(Type type)
    {
        var kind = type.IsEnum
            ? "enum"
            : type.IsInterface
                ? "interface"
                : type.IsValueType
                    ? "struct"
                    : type.IsSubclassOf(typeof(MulticastDelegate))
                        ? "delegate"
                        : "class";
        var modifiers = new List<string>();
        if (type.IsAbstract && type.IsSealed)
        {
            modifiers.Add("static");
        }
        else
        {
            if (type.IsValueType && type.IsDefined(typeof(IsReadOnlyAttribute), inherit: false))
            {
                modifiers.Add("readonly");
            }

            if (type.IsByRefLike)
            {
                modifiers.Add("ref");
            }

            if (type.IsAbstract && !type.IsInterface)
            {
                modifiers.Add("abstract");
            }

            if (type.IsSealed && !type.IsValueType)
            {
                modifiers.Add("sealed");
            }
        }

        var modifierText = modifiers.Count == 0 ? string.Empty : $" {string.Join(" ", modifiers)}";
        var inheritance = type.IsEnum
            ? $" : {FormatType(Enum.GetUnderlyingType(type))}"
            : FormatInheritance(type);
        return $"{FormatApiAttributes(type)}type {Accessibility(type)}{modifierText} {kind} {FormatTypeDeclarationName(type)}{inheritance}{FormatGenericConstraints(type.GetGenericArguments())}";
    }

    private static string FormatInheritance(Type type)
    {
        var inherited = new List<Type>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(MulticastDelegate))
        {
            inherited.Add(baseType);
        }

        inherited.AddRange(type.GetInterfaces().OrderBy(FormatType, StringComparer.Ordinal));
        return inherited.Count == 0 ? string.Empty : $" : {string.Join(", ", inherited.Select(FormatType))}";
    }

    private static string FormatGenericConstraints(IEnumerable<Type> genericArguments)
    {
        var clauses = new List<string>();
        foreach (var argument in genericArguments.Where(static argument => argument.IsGenericParameter))
        {
            var constraints = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                constraints.Add(ReadGenericParameterNullableFlag(argument) == 2 ? "class?" : "class");
            }
            else if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            {
                constraints.Add(HasUnmanagedConstraint(argument) ? "unmanaged" : "struct");
            }
            else if (HasNotNullConstraint(argument))
            {
                constraints.Add("notnull");
            }

            var constraintNullableFlags = ReadGenericConstraintNullableFlags(argument);
            constraints.AddRange(argument.GetGenericParameterConstraints()
                .Select((constraint, index) => (Constraint: constraint, Index: index))
                .Where(static item => item.Constraint != typeof(ValueType))
                .Select(item => FormatType(
                    item.Constraint,
                    new NullableFlagCursor(
                        item.Index < constraintNullableFlags.Count ? constraintNullableFlags[item.Index] : [],
                        ReadGenericParameterNullableContext(argument))))
                .Order(StringComparer.Ordinal));
            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0
                && (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                clauses.Add($" where {argument.Name} : {string.Join(", ", constraints)}");
            }
        }

        return string.Concat(clauses);
    }

    private static string FormatParameters(IEnumerable<ParameterInfo> parameters, bool extensionMethod = false) => string.Join(", ", parameters.Select((parameter, index) =>
    {
        var parameterType = parameter.ParameterType;
        var receiver = extensionMethod && index == 0 ? "this " : string.Empty;
        var modifier = parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false)
            ? "params "
            : parameterType.IsByRef
            ? parameter.IsOut
                ? "out "
                : parameter.IsIn
                    ? "in "
                    : "ref "
            : string.Empty;
        var effectiveType = parameterType.IsByRef ? parameterType.GetElementType()! : parameterType;
        var nullability = Nullability.Create(parameter);
        var nullableCursor = CreateNullableCursor(parameter.CustomAttributes, parameter.Member);
        var formattedType = parameterType.IsByRef && nullability.ElementType is { } elementNullability
            ? FormatType(elementNullability, nullableCursor)
            : parameterType.IsByRef
                ? FormatByRefElementType(effectiveType, nullability.ReadState, nullableCursor)
                : FormatType(nullability, nullableCursor);
        var defaultValue = parameter.HasDefaultValue
            ? $" = {FormatDefaultValue(parameter.DefaultValue, effectiveType)}"
            : string.Empty;
        return $"{receiver}{modifier}{formattedType} {parameter.Name}{defaultValue}";
    }));

    private static string FormatReturnType(MethodInfo method)
    {
        var returnType = method.ReturnType;
        if (!returnType.IsByRef)
        {
            return FormatType(
                Nullability.Create(method.ReturnParameter),
                CreateNullableCursor(method.ReturnParameter.CustomAttributes, method));
        }

        var prefix = method.ReturnParameter.GetRequiredCustomModifiers().Any(static modifier => modifier.FullName == "System.Runtime.InteropServices.InAttribute")
            ? "ref readonly "
            : "ref ";
        var nullability = Nullability.Create(method.ReturnParameter);
        var nullableCursor = CreateNullableCursor(method.ReturnParameter.CustomAttributes, method);
        return prefix + (nullability.ElementType is { } elementNullability
            ? FormatType(elementNullability, nullableCursor)
            : FormatByRefElementType(returnType.GetElementType()!, nullability.ReadState, nullableCursor));
    }

    private static string FormatByRefElementType(
        Type elementType,
        NullabilityState state,
        NullableFlagCursor nullableCursor)
    {
        var formatted = FormatType(elementType);
        var nullableFlag = nullableCursor.Next();
        return IsNullableAnnotation(elementType, nullableFlag, state) ? formatted + "?" : formatted;
    }

    private static string FormatType(NullabilityInfo nullability, NullableFlagCursor nullableCursor)
    {
        var type = nullability.Type;
        var nullableFlag = nullableCursor.Next();
        string formatted;
        if (type.IsByRef || type.IsPointer)
        {
            formatted = nullability.ElementType is { } element
                ? FormatType(element, nullableCursor) + (type.IsByRef ? "&" : "*")
                : FormatType(type);
        }
        else if (type.IsArray)
        {
            var element = nullability.ElementType is null
                ? FormatType(type.GetElementType()!)
                : FormatType(nullability.ElementType, nullableCursor);
            formatted = $"{element}[{new string(',', type.GetArrayRank() - 1)}]";
        }
        else if (type.IsGenericParameter)
        {
            formatted = type.Name;
        }
        else
        {
            if (type.IsGenericType)
            {
                var reflectedArguments = type.GetGenericArguments();
                var formattedArguments = nullability.GenericTypeArguments.Length == reflectedArguments.Length
                    ? nullability.GenericTypeArguments.Select(argument => FormatType(argument, nullableCursor)).ToArray()
                    : reflectedArguments.Select(FormatType).ToArray();
                formatted = FormatNamedType(type, formattedArguments);
            }
            else
            {
                formatted = FormatNamedType(type, []);
            }
        }

        return IsNullableAnnotation(type, nullableFlag, nullability.ReadState) ? formatted + "?" : formatted;
    }

    private static bool IsNullableAnnotation(Type type, byte nullableFlag, NullabilityState fallbackState) =>
        (!type.IsValueType || type.IsGenericParameter)
        && (nullableFlag == 2 || (nullableFlag == 0 && fallbackState == NullabilityState.Nullable));

    private static string FormatType(Type type)
    {
        if (type.IsByRef)
        {
            return FormatType(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return FormatType(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (!type.IsGenericType)
        {
            return FormatNamedType(type, []);
        }

        return FormatNamedType(type, type.GetGenericArguments().Select(FormatType).ToArray());
    }

    private static string FormatType(Type type, NullableFlagCursor nullableCursor)
    {
        var nullableFlag = nullableCursor.Next();
        string formatted;
        if (type.IsByRef || type.IsPointer)
        {
            formatted = FormatType(type.GetElementType()!, nullableCursor) + (type.IsByRef ? "&" : "*");
        }
        else if (type.IsArray)
        {
            formatted = $"{FormatType(type.GetElementType()!, nullableCursor)}[{new string(',', type.GetArrayRank() - 1)}]";
        }
        else if (type.IsGenericParameter)
        {
            formatted = type.Name;
        }
        else
        {
            if (!type.IsGenericType)
            {
                formatted = FormatNamedType(type, []);
            }
            else
            {
                var arguments = type.GetGenericArguments()
                    .Select(argument => FormatType(argument, nullableCursor))
                    .ToArray();
                formatted = FormatNamedType(type, arguments);
            }
        }

        return IsNullableAnnotation(type, nullableFlag, NullabilityState.Unknown) ? formatted + "?" : formatted;
    }

    private static string FormatTypeDeclarationName(Type type)
    {
        if (!type.IsGenericType)
        {
            return FormatNamedType(type, []);
        }

        var arguments = type.GetGenericArguments()
            .Select(argument =>
            {
                if (!argument.IsGenericParameter)
                {
                    return FormatType(argument);
                }

                var variance = argument.GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
                var prefix = variance switch
                {
                    GenericParameterAttributes.Covariant => "out ",
                    GenericParameterAttributes.Contravariant => "in ",
                    _ => string.Empty,
                };
                return prefix + argument.Name;
            })
            .ToArray();
        return FormatNamedType(type, arguments);
    }

    private static string FormatNamedType(Type type, IReadOnlyList<string> formattedArguments)
    {
        var hierarchy = new Stack<Type>();
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            hierarchy.Push(current);
        }

        var builder = new StringBuilder();
        var argumentIndex = 0;
        while (hierarchy.TryPop(out var segment))
        {
            if (builder.Length == 0 && !string.IsNullOrEmpty(segment.Namespace))
            {
                builder.Append(segment.Namespace).Append('.');
            }
            else if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(StripArity(segment.Name));
            var totalArgumentCount = segment.GetGenericArguments().Length;
            var declaringArgumentCount = segment.DeclaringType?.GetGenericArguments().Length ?? 0;
            var ownArgumentCount = totalArgumentCount - declaringArgumentCount;
            if (ownArgumentCount > 0)
            {
                builder.Append('<')
                    .Append(string.Join(", ", formattedArguments.Skip(argumentIndex).Take(ownArgumentCount)))
                    .Append('>');
                argumentIndex += ownArgumentCount;
            }
        }

        return builder.ToString();
    }

    private static bool HasUnmanagedConstraint(Type argument) => argument.CustomAttributes.Any(attribute =>
        string.Equals(
            attribute.AttributeType.FullName,
            "System.Runtime.CompilerServices.IsUnmanagedAttribute",
            StringComparison.Ordinal));

    private static string StripArity(string name)
    {
        var marker = name.IndexOf('`', StringComparison.Ordinal);
        return marker < 0 ? name : name[..marker];
    }

    private static string FormatDefaultValue(object? value, Type declaredType)
    {
        if (value is null)
        {
            return declaredType.IsValueType && Nullable.GetUnderlyingType(declaredType) is null ? "default" : "null";
        }

        if (value is DBNull or Missing)
        {
            return "default";
        }

        if (declaredType.IsEnum)
        {
            var name = Enum.GetName(declaredType, value);
            return name is null
                ? $"({FormatType(declaredType)}){Convert.ToString(value, CultureInfo.InvariantCulture)}"
                : $"{FormatType(declaredType)}.{name}";
        }

        return value switch
        {
            string text => $"\"{Escape(text)}\"",
            char character => $"'{Escape(character.ToString())}'",
            bool flag => flag ? "true" : "false",
            float number => number.ToString("R", CultureInfo.InvariantCulture) + "F",
            double number => number.ToString("R", CultureInfo.InvariantCulture) + "D",
            decimal number => number.ToString(CultureInfo.InvariantCulture) + "M",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
        };
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal);

    private static bool HasNotNullConstraint(Type genericParameter)
    {
        var nullableFlag = ReadNullableFlag(genericParameter.CustomAttributes, "System.Runtime.CompilerServices.NullableAttribute");
        if (nullableFlag.HasValue)
        {
            return nullableFlag == 1;
        }

        if (genericParameter.DeclaringMethod is { } method
            && ReadNullableFlag(method.CustomAttributes, "System.Runtime.CompilerServices.NullableContextAttribute") is { } methodContext)
        {
            return methodContext == 1;
        }

        for (var declaringType = genericParameter.DeclaringType; declaringType is not null; declaringType = declaringType.DeclaringType)
        {
            if (ReadNullableFlag(
                declaringType.CustomAttributes,
                "System.Runtime.CompilerServices.NullableContextAttribute") is { } typeContext)
            {
                return typeContext == 1;
            }
        }

        return false;
    }

    private static byte ReadGenericParameterNullableFlag(Type genericParameter) =>
        ReadNullableFlag(genericParameter.CustomAttributes, "System.Runtime.CompilerServices.NullableAttribute")
        ?? ReadGenericParameterNullableContext(genericParameter);

    private static byte ReadGenericParameterNullableContext(Type genericParameter)
    {
        if (genericParameter.DeclaringMethod is { } method
            && ReadNullableFlag(method.CustomAttributes, "System.Runtime.CompilerServices.NullableContextAttribute") is { } methodContext)
        {
            return methodContext;
        }

        for (var declaringType = genericParameter.DeclaringType; declaringType is not null; declaringType = declaringType.DeclaringType)
        {
            if (ReadNullableFlag(
                declaringType.CustomAttributes,
                "System.Runtime.CompilerServices.NullableContextAttribute") is { } typeContext)
            {
                return typeContext;
            }
        }

        return 0;
    }

    private static IReadOnlyList<byte[]> ReadGenericConstraintNullableFlags(Type genericParameter)
    {
        using var stream = File.OpenRead(genericParameter.Assembly.Location);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var genericParameterHandle = MetadataTokens.GenericParameterHandle(
            genericParameter.MetadataToken & 0x00FFFFFF);
        var parameter = metadata.GetGenericParameter(genericParameterHandle);
        return parameter.GetConstraints()
            .Select(handle => ReadNullableFlags(metadata, metadata.GetGenericParameterConstraint(handle).GetCustomAttributes()))
            .ToArray();
    }

    private static byte[] ReadNullableFlags(MetadataReader metadata, CustomAttributeHandleCollection attributes)
    {
        foreach (var attribute in attributes.Select(metadata.GetCustomAttribute))
        {
            if (!string.Equals(GetAttributeTypeName(metadata, attribute.Constructor), "System.Runtime.CompilerServices.NullableAttribute", StringComparison.Ordinal))
            {
                continue;
            }

            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                return [];
            }

            if (value.RemainingBytes == 3)
            {
                return [value.ReadByte()];
            }

            var count = value.ReadInt32();
            if (count < 0 || count > value.RemainingBytes - 2)
            {
                return [];
            }

            var flags = new byte[count];
            for (var index = 0; index < flags.Length; index++)
            {
                flags[index] = value.ReadByte();
            }

            return flags;
        }

        return [];
    }

    private static string? GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle typeHandle;
        if (constructor.Kind == HandleKind.MemberReference)
        {
            typeHandle = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        }
        else if (constructor.Kind == HandleKind.MethodDefinition)
        {
            typeHandle = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
        }
        else
        {
            return null;
        }

        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => GetFullName(metadata, metadata.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => GetFullName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => null,
        };
    }

    private static string GetFullName(MetadataReader metadata, TypeReference type) =>
        JoinNamespaceAndName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string GetFullName(MetadataReader metadata, TypeDefinition type) =>
        JoinNamespaceAndName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string JoinNamespaceAndName(string typeNamespace, string name) =>
        string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";

    private static byte? ReadNullableFlag(IEnumerable<CustomAttributeData> attributes, string attributeTypeName)
    {
        var attribute = attributes.FirstOrDefault(attribute => attribute.AttributeType.FullName == attributeTypeName);
        if (attribute is null || attribute.ConstructorArguments.Count != 1)
        {
            return null;
        }

        var argument = attribute.ConstructorArguments[0].Value;
        if (argument is byte flag)
        {
            return flag;
        }

        if (argument is IReadOnlyCollection<CustomAttributeTypedArgument> flags
            && flags.FirstOrDefault().Value is byte firstFlag)
        {
            return firstFlag;
        }

        return null;
    }

    private static NullableFlagCursor CreateNullableCursor(
        IEnumerable<CustomAttributeData> signatureAttributes,
        MemberInfo declaringMember)
    {
        var flags = ReadNullableFlags(signatureAttributes);
        var context = ReadNullableFlag(
            declaringMember.CustomAttributes,
            "System.Runtime.CompilerServices.NullableContextAttribute");
        if (!context.HasValue)
        {
            for (var declaringType = declaringMember.DeclaringType; declaringType is not null; declaringType = declaringType.DeclaringType)
            {
                context = ReadNullableFlag(
                    declaringType.CustomAttributes,
                    "System.Runtime.CompilerServices.NullableContextAttribute");
                if (context.HasValue)
                {
                    break;
                }
            }
        }

        return new NullableFlagCursor(flags, context ?? 0);
    }

    private static byte[] ReadNullableFlags(IEnumerable<CustomAttributeData> attributes)
    {
        var attribute = attributes.FirstOrDefault(static attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (attribute is null || attribute.ConstructorArguments.Count != 1)
        {
            return [];
        }

        var argument = attribute.ConstructorArguments[0].Value;
        if (argument is byte flag)
        {
            return [flag];
        }

        return argument is IReadOnlyCollection<CustomAttributeTypedArgument> flags
            ? flags.Select(static flag => flag.Value is byte value ? value : (byte)0).ToArray()
            : [];
    }

    private static string FormatApiAttributes(MemberInfo member)
    {
        var attributes = new List<string>(2);
        if (member is Type type && type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            attributes.Add("[System.Flags]");
        }

        if (member.GetCustomAttribute<ObsoleteAttribute>(inherit: false) is { } obsolete)
        {
            attributes.Add($"[System.Obsolete(\"{Escape(obsolete.Message ?? string.Empty)}\", error: {(obsolete.IsError ? "true" : "false")})]");
        }

        return attributes.Count == 0 ? string.Empty : string.Join(" ", attributes) + " ";
    }

    private static bool IsAccessor(MethodInfo method) =>
        method.IsSpecialName
        && (method.Name.StartsWith("get_", StringComparison.Ordinal)
            || method.Name.StartsWith("set_", StringComparison.Ordinal)
            || method.Name.StartsWith("add_", StringComparison.Ordinal)
            || method.Name.StartsWith("remove_", StringComparison.Ordinal));

    private static string FormatAccessorModifiers(MethodInfo accessor)
    {
        var modifiers = new List<string>();
        if (accessor.IsStatic)
        {
            modifiers.Add("static");
        }

        if (accessor.IsAbstract)
        {
            modifiers.Add("abstract");
        }
        else if (accessor.IsVirtual && !accessor.IsFinal && accessor.GetBaseDefinition() == accessor)
        {
            modifiers.Add("virtual");
        }

        return modifiers.Count == 0 ? string.Empty : $" {string.Join(" ", modifiers)}";
    }

    private static bool IsVisibleType(Type type) =>
        type.IsPublic
        || (type.IsNested && IsVisible(type) && type.DeclaringType is not null && IsVisibleType(type.DeclaringType));

    private static bool IsVisible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsVisible(FieldInfo field) => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static bool IsVisible(PropertyInfo property) =>
        (property.GetMethod is { } getter && IsVisible(getter))
        || (property.SetMethod is { } setter && IsVisible(setter));

    private static bool IsVisible(EventInfo eventInfo) =>
        (eventInfo.AddMethod is { } add && IsVisible(add))
        || (eventInfo.RemoveMethod is { } remove && IsVisible(remove));

    private static bool IsVisible(Type type) => type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem;

    private static string Accessibility(MethodBase method) => method.IsPublic ? "public" : method.IsFamily ? "protected" : "protected internal";

    private static string Accessibility(FieldInfo field) => field.IsPublic ? "public" : field.IsFamily ? "protected" : "protected internal";

    private static string Accessibility(PropertyInfo property) => MostVisible(
        property.GetMethod is null ? null : Accessibility(property.GetMethod),
        property.SetMethod is null ? null : Accessibility(property.SetMethod));

    private static string Accessibility(EventInfo eventInfo) => MostVisible(
        eventInfo.AddMethod is null ? null : Accessibility(eventInfo.AddMethod),
        eventInfo.RemoveMethod is null ? null : Accessibility(eventInfo.RemoveMethod));

    private static string Accessibility(Type type) => type.IsPublic || type.IsNestedPublic
        ? "public"
        : type.IsNestedFamily
            ? "protected"
            : "protected internal";

    private static string MostVisible(string? first, string? second)
    {
        if (first == "public" || second == "public")
        {
            return "public";
        }

        return first == "protected internal" || second == "protected internal" ? "protected internal" : "protected";
    }

    private sealed class NullableFlagCursor(byte[] flags, byte context)
    {
        private int _index;

        internal byte Next() => _index < flags.Length ? flags[_index++] : context;
    }

    private static string FindRepositoryRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("APPSURFACE_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot)
            && File.Exists(Path.Join(configuredRoot, "ForgeTrust.AppSurface.slnx")))
        {
            return Path.GetFullPath(configuredRoot);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the AppSurface repository root from the test output directory.");
    }
}
