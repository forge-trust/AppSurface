using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;
using Microsoft.Win32.SafeHandles;
using Calls = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunOutputLease.CoverageArtifactEnumerationCalls;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises native result orchestration; these tests make no native-platform acceptance claim.</summary>
public sealed class CoverageArtifactEnumerationDecisionTests
{
    [Theory]
    [InlineData("duplicate", 0)]
    [InlineData("rewind", 1)]
    [InlineData("open", 1)]
    [InlineData("read", 0)]
    public void FailedEnumerationReleasesOnlyTheDescriptorOrStreamItOwns(string failure, int expectedDescriptorCloses)
    {
        using var retained = new SafeFileHandle(123, false);
        var descriptorCloses = 0;
        var streamCloses = 0;
        var calls = new Calls
        {
            Duplicate = _ => failure == "duplicate" ? -1 : 456,
            Seek = (_, _, _) => failure == "rewind" ? -1 : 0,
            OpenDirectory = _ => failure == "open" ? 0 : 789,
            ReadDirectory = _ => { Marshal.SetLastPInvokeError(5); return 0; },
            Close = descriptor => { Assert.Equal(456, descriptor); descriptorCloses++; return 0; },
            CloseDirectory = stream => { Assert.Equal(789, stream); streamCloses++; return 0; }
        };
        Assert.Throws<IOException>(() => CoverageRunOutputLease.ReadArtifactUnixNames(retained, 10, calls));
        Assert.Equal(expectedDescriptorCloses, descriptorCloses);
        Assert.Equal(failure == "read" ? 1 : 0, streamCloses);
        Assert.False(retained.IsClosed);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void EnumerationSkipsDotEntriesAndClosesStreamAtEndOrLimit(int limit, bool shouldFail)
    {
        using var retained = new SafeFileHandle(123, false);
        var pointers = new List<nint>();
        var names = new Queue<nint>();
        var closes = 0;
        try
        {
            foreach (var name in new[] { ".", "..", "coverage.xml", "summary.json" })
            {
                var offset = OperatingSystem.IsMacOS() ? 21 : 19;
                var bytes = Encoding.UTF8.GetBytes(name + "\0");
                var pointer = Marshal.AllocHGlobal(offset + bytes.Length);
                pointers.Add(pointer);
                Marshal.Copy(bytes, 0, pointer + offset, bytes.Length);
                names.Enqueue(pointer);
            }
            var calls = new Calls
            {
                Duplicate = _ => 456,
                Seek = (_, _, _) => 0,
                OpenDirectory = _ => 789,
                ReadDirectory = _ => { Marshal.SetLastPInvokeError(0); return names.Count > 0 ? names.Dequeue() : 0; },
                Close = _ => throw new InvalidOperationException("Stream owns descriptor."),
                CloseDirectory = _ => { closes++; return 0; }
            };
            if (shouldFail) Assert.Throws<IOException>(() => CoverageRunOutputLease.ReadArtifactUnixNames(retained, limit, calls));
            else Assert.Equal(["coverage.xml", "summary.json"], CoverageRunOutputLease.ReadArtifactUnixNames(retained, limit, calls));
            Assert.Equal(1, closes);
            Assert.False(retained.IsClosed);
        }
        finally { foreach (var pointer in pointers) Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public void WindowsBoundedNameCollectionPreservesNamesAndRejectsLimit()
    {
        using var retained = new SafeFileHandle(123, false);
        string[] paths = [Path.Join("parent", "coverage.xml"), Path.Join("parent", "summary.json")];
        Assert.Equal(["coverage.xml", "summary.json"], CoverageRunOutputLease.ReadArtifactWindowsNames(retained, 2, _ => paths));
        Assert.Throws<IOException>(() => CoverageRunOutputLease.ReadArtifactWindowsNames(retained, 1, _ => paths));
        Assert.Throws<UnauthorizedAccessException>(() => CoverageRunOutputLease.ReadArtifactWindowsNames(retained, 2, _ => throw new UnauthorizedAccessException()));
        Assert.False(retained.IsClosed);
    }
}
