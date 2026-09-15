using System.Diagnostics.CodeAnalysis;

[assembly: ExcludeFromCodeCoverage(
    Justification = "The focused AppHost is executable sample-composition glue. Its resource graph, environment projection, and completion edges are validated by graph-level tests rather than line instrumentation.")]
