using NativePreparation.Subject;
using Xunit;

namespace NativePreparation.Subject.Tests;

public sealed class SubjectTests
{
    [Theory]
    [InlineData(1, 2, 0, 3)]
    [InlineData(6, 2, 1, 4)]
    [InlineData(3, 2, 2, 6)]
    [InlineData(8, 2, 3, 4)]
    [InlineData(8, 0, 3, 0)]
    [InlineData(8, 2, 99, 0)]
    public async Task Calculator_branches_are_exercised(int left, int right, int operation, int expected)
    {
        if (operation == 0 && (Scenario.Kind is "cleanup-failure" or "ignored-cancel"))
        {
            await SubjectFailureScenarios.Run(Scenario.Kind);
        }

        var actual = Record.Exception(() => Calculator.Calculate(left, right, operation));
        if (operation == 3 && right == 0)
        {
            Assert.IsType<DivideByZeroException>(actual);
        }
        else if (operation == 99)
        {
            Assert.IsType<ArgumentOutOfRangeException>(actual);
        }
        else
        {
            Assert.Null(actual);
            Assert.Equal(expected, Calculator.Calculate(left, right, operation));
        }

        if (operation == 0 && Scenario.Kind != "normal" && Scenario.Kind != "ignored-cancel")
        {
            // Root selects this immutable fixture before launch. This marker only lets the
            // external negative control wait until its actual test branch has been entered.
            var scratch = Environment.GetEnvironmentVariable("TMPDIR")
                ?? throw new InvalidOperationException("The fixture needs its producer scratch.");
            var markerPath = Path.Combine(scratch, "checkpoint2-case-entered");
            var pendingPath = markerPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var marker = new FileStream(pendingPath,
                           FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(marker, leaveOpen: true))
                    {
                        writer.Write(Scenario.Kind);
                        writer.Flush();
                    }

                    marker.Flush(flushToDisk: true);
                }

                File.Move(pendingPath, markerPath, overwrite: false);
            }
            catch
            {
                File.Delete(pendingPath);
                throw;
            }
        }

        if (Scenario.Kind == "test-failure" && operation == 0)
        {
            Assert.Fail("Root-selected test-failure scenario.");
        }

        if (Scenario.Kind == "cancel" && operation == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (Scenario.Kind == "quota" && operation == 0)
        {
            var chunk = new byte[65_536];
            Array.Fill(chunk, (byte)'Q');
            // VSTest captures successful testhost stdout instead of forwarding it to the original
            // producer pipe. The fixed subject target emits these actual bytes through SDK output.
            var scratch = Environment.GetEnvironmentVariable("TMPDIR")
                ?? throw new InvalidOperationException("The fixture needs its producer scratch.");
            using var raw = new FileStream(Path.Combine(scratch, "checkpoint2-quota-output.txt"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            for (var index = 0; index < 320; index++)
            {
                // Unique prefixes keep MSBuild's metadata batches distinct for all320 rows.
                raw.Write(System.Text.Encoding.ASCII.GetBytes(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ":"));
                raw.Write(chunk);
                raw.WriteByte((byte)'\n');
            }

            raw.Flush(flushToDisk: true);
        }
    }
}
