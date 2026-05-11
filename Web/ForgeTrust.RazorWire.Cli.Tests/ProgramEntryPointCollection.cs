using Xunit;

namespace ForgeTrust.RazorWire.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProgramEntryPointCollection
{
    public const string Name = "ProgramEntryPoint";
}
