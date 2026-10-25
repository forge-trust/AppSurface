using System.Buffers.Binary;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached directory-block procedure controls; no descriptor, owner or native acceptance is fabricated.</summary>
public sealed class SupervisionDirectoryReadTests
{
    [Fact]
    public void MultipleBlocksAndEofKeepExactCallbackOrderAndOrdinalNames()
    {
        var blocks = new Queue<byte[]>([Block("z", "."), Block("a", ".."), []]);
        var order = new List<string>();
        var result = SupervisionDirectoryRead.Read(new byte[4096], buffer =>
        {
            order.Add("read");
            var block = blocks.Dequeue();
            block.CopyTo(buffer, 0);
            return block.Length;
        }, () => order.Add("before"), () => order.Add("after"), 2, 3,
        name => { order.Add(name); return true; }, Invalid);

        Assert.Equal(["a", "z"], result);
        Assert.Equal(["before", "read", "after", "z", "before", "read", "after", "a",
            "before", "read", "after"], order);
        Assert.Empty(blocks);
    }

    [Fact]
    public void EmptyInventoryStillReadsAndChecksEofExactlyOnce()
    {
        var reads = 0;
        var before = 0;
        var after = 0;
        var predicates = 0;
        Assert.Empty(SupervisionDirectoryRead.Read(new byte[4096], _ => { reads++; return 0; },
            () => before++, () => after++, 0, 1, _ => { predicates++; return false; }, Invalid));
        Assert.Equal((1, 1, 1, 0), (reads, before, after, predicates));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("count")]
    [InlineData("inventory")]
    public void CrossBlockDuplicateNameAndCallerBoundsUseOriginalException(string failure)
    {
        var expected = new IOException("fixed scan rejection");
        var reads = 0;
        var predicates = new List<string>();
        var blocks = new Queue<byte[]>([Block("a"), Block(failure == "duplicate" ? "a" : "b"), []]);
        var actual = Assert.Throws<IOException>(() => SupervisionDirectoryRead.Read(new byte[4096], buffer =>
        {
            reads++;
            var block = blocks.Dequeue();
            block.CopyTo(buffer, 0);
            return block.Length;
        }, static () => { }, static () => { }, failure == "count" ? 1 : 2, null,
        name => { predicates.Add(name); return failure != "inventory" || name == "a"; }, () => expected));

        Assert.Same(expected, actual);
        Assert.Equal(2, reads);
        Assert.Equal(failure == "inventory" ? ["a", "b"] : new[] { "a" }, predicates);
    }

    [Fact]
    public void BlockBudgetIncludesEofAndRejectsBeforeTheNextRead()
    {
        var reads = 0;
        var before = 0;
        var after = 0;
        var expected = new IOException();
        Assert.Same(expected, Assert.Throws<IOException>(() => SupervisionDirectoryRead.Read(new byte[4096], buffer =>
        {
            reads++;
            var block = Block("a");
            block.CopyTo(buffer, 0);
            return block.Length;
        }, () => before++, () => after++, 1, 1, null, () => expected)));
        Assert.Equal((1, 2, 1), (reads, before, after));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("read")]
    [InlineData("after")]
    [InlineData("predicate")]
    public void ExceptionsPropagateByIdentityWithoutFurtherIo(string phase)
    {
        var expected = new OperationCanceledException();
        var calls = new List<string>();
        void At(string current)
        {
            calls.Add(current);
            if (current == phase) throw expected;
        }
        var error = Assert.Throws<OperationCanceledException>(() => SupervisionDirectoryRead.Read(new byte[4096], buffer =>
        {
            At("read");
            var block = Block("a");
            block.CopyTo(buffer, 0);
            return block.Length;
        }, () => At("before"), () => At("after"), 1, null, _ => { At("predicate"); return true; }, Invalid));
        Assert.Same(expected, error);
        Assert.Equal(new[] { "before", "read", "after", "predicate" }.Take(calls.Count), calls);
        Assert.Equal(phase, calls[^1]);
    }

    [Fact]
    public void CancellationAtEofCheckCannotReturnACompletedInventory()
    {
        var after = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => SupervisionDirectoryRead.Read(new byte[4096], _ => 0,
            static () => { }, () => { after++; cancelled.Token.ThrowIfCancellationRequested(); }, 0, null, null, Invalid));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(1, after);
    }

    [Fact]
    public void MalformedStrictBlocksCannotEnterTheInventoryPredicate()
    {
        var predicates = 0;
        var error = Assert.Throws<ForgeTrust.AppSurface.Evidence.Contracts.EvidenceAdmissionException>(() =>
            SupervisionDirectoryRead.Read(new byte[4096], buffer =>
            {
                var block = Block("private:name");
                block.CopyTo(buffer, 0);
                return block.Length;
            }, static () => { }, static () => { }, 1, null, _ => { predicates++; return true; }, Invalid));
        Assert.Equal("ASEVD402", error.Code);
        Assert.DoesNotContain("private:name", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, predicates);
    }

    [Fact]
    public void MissingCallbacksAndInvalidProcedureBoundsRejectBeforeIo()
    {
        var reads = 0;
        int Read(byte[] _) { reads++; return 0; }
        Assert.Throws<ArgumentNullException>(() => SupervisionDirectoryRead.Read(null!, Read, () => { }, () => { }, 1, 1, null, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDirectoryRead.Read(new byte[1], null!, () => { }, () => { }, 1, 1, null, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDirectoryRead.Read(new byte[1], Read, null!, () => { }, 1, 1, null, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDirectoryRead.Read(new byte[1], Read, () => { }, null!, 1, 1, null, Invalid));
        Assert.Throws<ArgumentNullException>(() => SupervisionDirectoryRead.Read(new byte[1], Read, () => { }, () => { }, 1, 1, null, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisionDirectoryRead.Read([], Read, () => { }, () => { }, 1, 1, null, Invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisionDirectoryRead.Read(new byte[32769], Read, () => { }, () => { }, 1, 1, null, Invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisionDirectoryRead.Read(new byte[1], Read, () => { }, () => { }, -1, 1, null, Invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupervisionDirectoryRead.Read(new byte[1], Read, () => { }, () => { }, 1, 0, null, Invalid));
        Assert.Equal(0, reads);
    }

    private static IOException Invalid() => new("Fixed directory scan rejection.");

    private static byte[] Block(params string[] names)
    {
        using var stream = new MemoryStream();
        foreach (var name in names)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            var size = (19 + bytes.Length + 1 + 7) & ~7;
            var block = new byte[size];
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(16), checked((ushort)size));
            bytes.CopyTo(block, 19);
            stream.Write(block);
        }
        return stream.ToArray();
    }
}
