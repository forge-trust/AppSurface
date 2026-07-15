using ForgeTrust.AppSurface.Durable.Tests.Support;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void Durable_production_assembly_does_not_reference_provider()
    {
        Assert.DoesNotContain(
            typeof(AppSurfaceDurableModule).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "ForgeTrust.AppSurface.Durable.Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Durable_public_types_match_reviewed_baseline()
    {
        PublicApiSnapshot.AssertMatches(
            typeof(AppSurfaceDurableModule).Assembly,
            "Durable.PublicAPI.Shipped.txt",
            "Durable/ForgeTrust.AppSurface.Durable/PublicAPI.Shipped.txt");
    }

    [Fact]
    public void Public_api_snapshot_represents_member_signatures_and_constraints()
    {
        var snapshot = PublicApiSnapshot.DescribeType(typeof(SignatureFixture<>));

        Assert.Contains(snapshot, line => line.Contains("constructor public", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("property public System.String Name { get: public; init: public; }", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("method protected virtual System.Boolean TryRead", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("ref System.Int32 value", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("System.String label = \"default\"", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("method public System.String? Combine(System.String? prefix, params System.String?[] values)", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("property public System.String? OptionalName", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("event public System.EventHandler? Changed", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("field public System.String? OptionalField", StringComparison.Ordinal));
        Assert.Contains(snapshot, line => line.Contains("[System.Obsolete(\"Use Combine.\", error: true)] method public", StringComparison.Ordinal));
        Assert.Contains("where T : class, new()", snapshot[0], StringComparison.Ordinal);

        var notNullSnapshot = PublicApiSnapshot.DescribeType(typeof(NotNullFixture<>));
        Assert.Contains("where TValue : notnull", notNullSnapshot[0], StringComparison.Ordinal);
        var productionNotNullSnapshot = PublicApiSnapshot.DescribeType(typeof(DurableWorkScheduleTarget<>));
        Assert.Contains("where TWork : notnull", productionNotNullSnapshot[0], StringComparison.Ordinal);

        var flagsSnapshot = PublicApiSnapshot.DescribeType(typeof(SignatureFlags));
        Assert.StartsWith("[System.Flags] type public enum", flagsSnapshot[0], StringComparison.Ordinal);

        var extensionSnapshot = PublicApiSnapshot.DescribeType(typeof(SignatureExtensions));
        Assert.Contains(extensionSnapshot, line => line.Contains("Extend(this System.String? value)", StringComparison.Ordinal));

        var genericNullabilitySnapshot = PublicApiSnapshot.DescribeType(typeof(GenericNullabilityFixture));
        Assert.Contains(genericNullabilitySnapshot, line => line.Contains("Plain<TValue>(ForgeTrust.AppSurface.Durable.Tests.PublicApiBaselineTests.GenericContainer<TValue> value)", StringComparison.Ordinal));
        Assert.Contains(genericNullabilitySnapshot, line => line.Contains("Annotated<TValue>(ForgeTrust.AppSurface.Durable.Tests.PublicApiBaselineTests.GenericContainer<TValue?> value)", StringComparison.Ordinal));

        var genericConstraintSnapshot = PublicApiSnapshot.DescribeType(typeof(GenericConstraintFixture));
        Assert.Contains(genericConstraintSnapshot, line => line.Contains("NonNullableReference<TValue>() where TValue : class", StringComparison.Ordinal));
        Assert.Contains(genericConstraintSnapshot, line => line.Contains("NullableReference<TValue>() where TValue : class?", StringComparison.Ordinal));
        Assert.Contains(genericConstraintSnapshot, line => line.Contains("NonNullableInterface<TValue>() where TValue : System.Collections.Generic.IEnumerable<System.String>", StringComparison.Ordinal));
        Assert.Contains(genericConstraintSnapshot, line => line.Contains("NullableInterface<TValue>() where TValue : System.Collections.Generic.IEnumerable<System.String?>", StringComparison.Ordinal));
    }

    public class SignatureFixture<T>
        where T : class, new()
    {
        public SignatureFixture(int value)
        {
            _ = value;
        }

        public string Name { get; init; } = string.Empty;

        public string? OptionalName { get; set; }

        public string? OptionalField;

        public event EventHandler? Changed;

        public string? Combine(string? prefix, params string?[] values)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return values.Length == 0 ? prefix : values[0];
        }

        [Obsolete("Use Combine.", error: true)]
        public void Legacy()
        {
        }

        protected virtual bool TryRead(ref int value, string label = "default") => value > label.Length;
    }

    public sealed class NotNullFixture<TValue>
        where TValue : notnull
    {
    }

    [Flags]
    public enum SignatureFlags
    {
        None = 0,
        One = 1,
    }

    public sealed class GenericContainer<TValue>
    {
    }

    public static class GenericNullabilityFixture
    {
        public static void Plain<TValue>(GenericContainer<TValue> value) => _ = value;

        public static void Annotated<TValue>(GenericContainer<TValue?> value) => _ = value;
    }

    public static class GenericConstraintFixture
    {
        public static void NonNullableReference<TValue>() where TValue : class { }

        public static void NullableReference<TValue>() where TValue : class? { }

        public static void NonNullableInterface<TValue>() where TValue : IEnumerable<string> { }

        public static void NullableInterface<TValue>() where TValue : IEnumerable<string?> { }
    }

}

public static class SignatureExtensions
{
    public static string? Extend(this string? value) => value;
}
