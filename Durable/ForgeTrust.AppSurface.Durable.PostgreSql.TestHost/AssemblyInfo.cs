using System.Diagnostics.CodeAnalysis;

[assembly: ExcludeFromCodeCoverage(Justification = "Child-process test host behavior is verified by PostgreSQL integration tests; it is not production runtime code.")]
