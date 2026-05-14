using System.Collections.Concurrent;
using FakeItEasy;
using Microsoft.Extensions.Caching.Memory;

namespace ForgeTrust.AppSurface.Caching.Tests;

public class MemoTests : IDisposable
{
    private sealed class Wrapper<T>
    {
        public async Task<T> Get(IMemo memo, T value)
        {
            return await memo.GetAsync(() => Task.FromResult(value), CachePolicy.Absolute(TimeSpan.FromMinutes(1)));
        }
    }

    [Fact]
    public async Task GetAsync_TResultType_IsPartOfIdentity()
    {
        var memo = CreateMemo();

        // Use a generic wrapper to ensure same file/line for both calls
        var intWrapper = new Wrapper<int>();
        var stringWrapper = new Wrapper<string>();

        var a = await intWrapper.Get(memo, 42);
        var b = await stringWrapper.Get(memo, "42");

        Assert.Equal(42, a);
        Assert.Equal("42", b);
    }

    [Fact]
    public async Task GetAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        var memo = CreateMemo();
        memo.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            memo.GetAsync(() => Task.FromResult(1), CachePolicy.Absolute(TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public async Task GetAsync_OngoingOperation_ThrowsWhenDisposedDuringWait()
    {
        var memo = CreateMemo();
        var gate = new TaskCompletionSource<bool>();
        var entered = new TaskCompletionSource<bool>();

        var task = memo.GetAsync(
            async () =>
            {
                entered.SetResult(true);
                await gate.Task;

                return 1;
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(1)));

        // Wait to ensure it's inside the factory
        await entered.Task;

        memo.Dispose();
        gate.SetResult(true);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }

    private readonly List<MemoryCache> _caches = [];

    private Memo CreateMemo()
    {
        return CreateMemoWithOptions(new MemoryCacheOptions());
    }

    private Memo CreateMemoWithOptions(MemoryCacheOptions options, TimeSpan? failureCacheDuration = null)
    {
        var cache = new MemoryCache(options);
        _caches.Add(cache);

        return failureCacheDuration.HasValue
            ? new Memo(cache, failureCacheDuration.Value)
            : new Memo(cache);
    }

    public void Dispose()
    {
        foreach (var cache in _caches)
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task GetAsync_CacheMiss_CallsFactory()
    {
        var memo = CreateMemo();
        var callCount = 0;

        var result = await memo.GetAsync(
            () =>
            {
                callCount++;

                return Task.FromResult(42);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetAsync_CacheHit_SameCallSite_DoesNotCallFactory()
    {
        var memo = CreateMemo();
        var callCount = 0;

        async Task<int> GetValue() =>
            await memo.GetAsync(
                () =>
                {
                    callCount++;

                    return Task.FromResult(42);
                },
                CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        var first = await GetValue();
        var second = await GetValue();

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetAsync_DifferentCallSites_DifferentEntries()
    {
        var memo = CreateMemo();
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;

            return Task.FromResult(42);
        }

        // Two different lines = two different entries
        var first = await memo.GetAsync(Factory, CachePolicy.Absolute(TimeSpan.FromMinutes(5)));
        var second = await memo.GetAsync(Factory, CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetAsync_DifferentArgs_DifferentEntries()
    {
        var memo = CreateMemo();

        var a = await memo.GetAsync(
            1,
            x => Task.FromResult(x * 10),
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        var b = await memo.GetAsync(
            2,
            x => Task.FromResult(x * 10),
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        Assert.Equal(10, a);
        Assert.Equal(20, b);
    }

    [Fact]
    public async Task GetAsync_SameArgs_SameCallSite_CacheHit()
    {
        var memo = CreateMemo();
        var callCount = 0;

        async Task<string> GetUser(int id) =>
            await memo.GetAsync(
                id,
                userId =>
                {
                    callCount++;

                    return Task.FromResult($"user-{userId}");
                },
                CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        var first = await GetUser(42);
        var second = await GetUser(42);

        Assert.Equal("user-42", first);
        Assert.Equal("user-42", second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetAsync_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            ct =>
            {
                received = ct;

                return Task.FromResult(42);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_OneArg_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            99,
            (id, ct) =>
            {
                received = ct;

                return Task.FromResult($"item-{id}");
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal("item-99", result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_TwoArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            "a",
            "b",
            (a, b, ct) =>
            {
                received = ct;

                return Task.FromResult($"{a}-{b}");
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal("a-b", result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_ThreeArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            "a",
            "b",
            "c",
            (
                a,
                b,
                c,
                ct) =>
            {
                received = ct;

                return Task.FromResult($"{a}-{b}-{c}");
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal("a-b-c", result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_FourArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            1,
            2,
            3,
            4,
            (
                a,
                b,
                c,
                d,
                ct) =>
            {
                received = ct;

                return Task.FromResult(a + b + c + d);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal(10, result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_FiveArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            1,
            2,
            3,
            4,
            5,
            (
                a,
                b,
                c,
                d,
                e,
                ct) =>
            {
                received = ct;

                return Task.FromResult(a + b + c + d + e);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal(15, result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_SixArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            1,
            2,
            3,
            4,
            5,
            6,
            (
                a,
                b,
                c,
                d,
                e,
                f,
                ct) =>
            {
                received = ct;

                return Task.FromResult(a + b + c + d + e + f);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal(21, result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_SevenArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            (
                a,
                b,
                c,
                d,
                e,
                f,
                g,
                ct) =>
            {
                received = ct;

                return Task.FromResult(a + b + c + d + e + f + g);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal(28, result);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task GetAsync_EightArgs_WithCancellationTokenFactory_ForwardsCt()
    {
        var memo = CreateMemo();
        CancellationToken received = default;

        var result = await memo.GetAsync(
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            (
                a,
                b,
                c,
                d,
                e,
                f,
                g,
                h,
                ct) =>
            {
                received = ct;

                return Task.FromResult(a + b + c + d + e + f + g + h);
            },
            CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        Assert.Equal(36, result);
        Assert.Equal(CancellationToken.None, received);
    }

    private sealed class DeterministicCache : IMemoryCache
    {
        private readonly IMemoryCache _inner;
        private readonly CountdownEvent _event;

        public DeterministicCache(IMemoryCache inner, CountdownEvent @event)
        {
            _inner = inner;
            _event = @event;
        }

        public bool TryGetValue(object key, out object? value)
        {
            var result = _inner.TryGetValue(key, out value);
            try
            {
                _event.Signal();
            }
            catch (InvalidOperationException)
            {
                // Ignore if signaled too many times
            }

            return result;
        }

        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);
        public void Remove(object key) => _inner.Remove(key);
        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task GetAsync_ThunderingHerd_FactoryCalledOnce()
    {
        const int callerCount = 10;
        using var allWaitersReachedCache = new CountdownEvent(callerCount);
        var innerCache = new MemoryCache(new MemoryCacheOptions());
        var deterministicCache = new DeterministicCache(innerCache, allWaitersReachedCache);
        var memo = new Memo(deterministicCache);
        _caches.Add(innerCache);

        var callCount = 0;
        var gate = new TaskCompletionSource<bool>();

        async Task<int> SlowFactory()
        {
            Interlocked.Increment(ref callCount);

            return await gate.Task ? 99 : 0;
        }

        var tasks = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(() => memo.GetAsync(SlowFactory, CachePolicy.Absolute(TimeSpan.FromMinutes(5)))))
            .ToArray();

        // Wait for all tasks to have at least attempted to read from the cache.
        // This ensures they are either running the factory or blocked on the semaphore.
        allWaitersReachedCache.Wait(TimeSpan.FromSeconds(5));

        // Release the factory
        gate.SetResult(true);

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(99, r));
        Assert.Equal(1, callCount);
    }


    [Fact]
    public async Task GetAsync_CancellationToken_Respected()
    {
        var memo = CreateMemo();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // CancellationToken cancels the semaphore wait, not the factory
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            memo.GetAsync(
                () => Task.FromResult(1),
                CachePolicy.Absolute(TimeSpan.FromMinutes(5)),
                cts.Token));
    }

    [Fact]
    public async Task GetAsync_NullFactory_Throws()
    {
        var memo = CreateMemo();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            memo.GetAsync(
                (Func<Task<int>>)null!,
                CachePolicy.Absolute(TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public async Task GetAsync_NullPolicy_Throws()
    {
        var memo = CreateMemo();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            memo.GetAsync(
                () => Task.FromResult(1),
                null!));
    }

    [Fact]
    public void Constructor_NullCache_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Memo(null!));
    }

    [Fact]
    public void Constructor_ZeroFailureDuration_Throws()
    {
        var cache = A.Fake<IMemoryCache>();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new Memo(cache, TimeSpan.Zero); });
    }

    [Fact]
    public void Constructor_NegativeFailureDuration_Throws()
    {
        var cache = A.Fake<IMemoryCache>();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new Memo(cache, TimeSpan.FromSeconds(-1)); });
    }

    [Fact]
    public void OnCacheEntryEvicted_InvalidState_DoesNotThrow()
    {
        // Accessing private static method via reflection to test edge cases
        var method = typeof(Memo)
            .GetMethod(
                "OnCacheEntryEvicted",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        // State is not a tuple (OnCacheEntryEvicted expects ValueTuple<ConcurrentDictionary<object, SemaphoreSlim>, SemaphoreSlim>)
        method.Invoke(null, ["key", "value", EvictionReason.Removed, new object()]);

        // Key-type mismatch and logic-guard exercise:
        // Even if we provide a non-string key (e.g., 123), it should not throw.
        // We use a dictionary with 'object' keys to match the expected pattern in Memo.cs.
        var locks = new ConcurrentDictionary<object, SemaphoreSlim>();
        var sem = new SemaphoreSlim(1);
        method.Invoke(null, [123, "value", EvictionReason.Removed, (locks, sem)]);
    }

    [Fact]
    public async Task GetAsync_AbsoluteExpiration_Evicts()
    {
        var memo = CreateMemoWithOptions(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) });
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;

            return Task.FromResult(callCount);
        }

        var first = await memo.GetAsync(Factory, CachePolicy.Absolute(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, first);

        // Wait for expiration
        await Task.Delay(100);

        var second = await memo.GetAsync(Factory, CachePolicy.Absolute(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(2, second);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetAsync_SlidingExpiration_KeepsAliveOnAccess()
    {
        var memo = CreateMemoWithOptions(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) });
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;

            return Task.FromResult(callCount);
        }

        var policy = CachePolicy.Sliding(TimeSpan.FromMilliseconds(200));
        async Task<int> GetValue() => await memo.GetAsync(Factory, policy);

        await GetValue();

        // Access before expiration to keep it alive
        await Task.Delay(100);
        await GetValue();

        await Task.Delay(100);
        await GetValue();

        // Should still be the first call's result
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetAsync_SlidingExpiration_EvictsAfterIdle()
    {
        var memo = CreateMemoWithOptions(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) });
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;

            return Task.FromResult(callCount);
        }

        var policy = CachePolicy.Sliding(TimeSpan.FromMilliseconds(50));

        await memo.GetAsync(Factory, policy);

        // Wait for idle expiration
        await Task.Delay(100);

        var second = await memo.GetAsync(Factory, policy);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task GetAsync_SlidingWithAbsolute_EnforcesCeiling()
    {
        var memo = CreateMemoWithOptions(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) });
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;

            return Task.FromResult(callCount);
        }

        // Sliding of 100ms with absolute ceiling of 200ms
        var policy = CachePolicy.SlidingWithAbsolute(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200));

        async Task<int> GetValue() => await memo.GetAsync(Factory, policy);

        await GetValue();

        // Access within sliding window to keep alive
        await Task.Delay(80);
        var second = await GetValue();
        Assert.Equal(1, second); // Still cached

        // Wait past the absolute ceiling
        await Task.Delay(200);

        var third = await GetValue();
        Assert.Equal(2, third); // Evicted by absolute ceiling
    }

    [Fact]
    public async Task GetAsync_FactoryException_Propagates()
    {
        var memo = CreateMemo();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memo.GetAsync(
                () => Task.FromException<int>(new InvalidOperationException("boom")),
                CachePolicy.Absolute(TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public async Task GetAsync_FactoryException_CachedBriefly()
    {
        var memo = CreateMemo();
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;

            throw new InvalidOperationException("transient");
        }

        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(5));
        async Task<int> GetValue() => await memo.GetAsync(Factory, policy);

        // First call — factory throws
        await Assert.ThrowsAsync<InvalidOperationException>(GetValue);

        // Second call within failure TTL — should rethrow cached exception without calling factory
        await Assert.ThrowsAsync<InvalidOperationException>(GetValue);

        Assert.Equal(1, callCount); // Factory was called only once
    }

    [Fact]
    public async Task GetAsync_FactoryException_RetryAfterTtlExpires()
    {
        var failureTtl = TimeSpan.FromMilliseconds(50);
        var memo = CreateMemoWithOptions(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) },
            failureTtl);
        var callCount = 0;

        Task<int> Factory()
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.FromResult(42);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memo.GetAsync(Factory, CachePolicy.Absolute(TimeSpan.FromMinutes(5))));

        // Wait for the failure TTL to expire
        await Task.Delay(failureTtl + TimeSpan.FromMilliseconds(50));

        var result = await memo.GetAsync(Factory, CachePolicy.Absolute(TimeSpan.FromMinutes(5)));

        Assert.Equal(42, result);
        Assert.Equal(2, callCount);
    }

    // Key generation tests

    [Fact]
    public void BuildKey_OneArg_Deterministic()
    {
        var key1 = Memo.BuildKey<int, int>("Test.cs", 42, 1);
        var key2 = Memo.BuildKey<int, int>("Test.cs", 42, 1);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildKey_DifferentArgs_DifferentKeys()
    {
        var key1 = Memo.BuildKey<int, int>("Test.cs", 42, 1);
        var key2 = Memo.BuildKey<int, int>("Test.cs", 42, 2);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_DifferentLines_DifferentKeys()
    {
        var key1 = Memo.BuildKey<int, int>("Test.cs", 42, 1);
        var key2 = Memo.BuildKey<int, int>("Test.cs", 43, 1);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_TwoArgs_Deterministic()
    {
        var key1 = Memo.BuildKey<string, string, int>("Test.cs", 42, "a", 1);
        var key2 = Memo.BuildKey<string, string, int>("Test.cs", 42, "a", 1);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildKey_NullArg_HandledConsistently()
    {
        var key1 = Memo.BuildKey<string, string?>("Test.cs", 42, null);
        var key2 = Memo.BuildKey<string, string?>("Test.cs", 42, null);
        Assert.Equal(key1, key2);

        var key3 = Memo.BuildKey<string, string?>("Test.cs", 42, "actual");
        Assert.NotEqual(key1, key3);
    }

    [Fact]
    public void BuildKey_IsValueTuple()
    {
        var key = Memo.BuildKey<int, string>("Test.cs", 100, "hello");
        Assert.IsType<(string, int, Type, string)>(key);
        var tuple = ((string, int, Type, string))key;
        Assert.Equal("Test.cs", tuple.Item1);
        Assert.Equal(100, tuple.Item2);
        Assert.Equal(typeof(int), tuple.Item3);
        Assert.Equal("hello", tuple.Item4);
    }

    [Fact]
    public async Task GetAsync_DoubleCheckLock_CachedFailure_Throws()
    {
        var memo = CreateMemo();
        var callCount = 0;
        var gate = new TaskCompletionSource<bool>();

        async Task<int> Factory()
        {
            var current = Interlocked.Increment(ref callCount);
            if (current == 1)
            {
                await gate.Task;

                throw new InvalidOperationException("First call failure");
            }

            return 42;
        }

        // First call starts and waits at gate
        var task1 = memo.GetAsync(
            Factory,
            CachePolicy.Absolute(TimeSpan.FromMinutes(1)),
            callerFilePath: "collision",
            callerLineNumber: 1);

        // Second call waits for semaphore (same identity)
        var task2 = memo.GetAsync(
            Factory,
            CachePolicy.Absolute(TimeSpan.FromMinutes(1)),
            callerFilePath: "collision",
            callerLineNumber: 1);

        // Wait briefly to ensure task2 has had time to reach the internal semaphore.WaitAsync.
        // We poll to ensure it remains in the blocked state (not completed, callCount still 1).
        for (int i = 0; i < 50; i++) // 50 * 10ms = 500ms
        {
            await Task.Delay(10);
            if (task2.IsCompleted || callCount > 1)
            {
                throw new Exception(
                    $"task2 failed to block on semaphore: {(task2.IsCompleted ? "completed unexpectedly" : "factory called again")}");
            }
        }

        // Release gate
        gate.SetResult(true);

        // Both should throw original exception
        await Assert.ThrowsAsync<InvalidOperationException>(() => task1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => task2);

        Assert.Equal(1, callCount); // Factory only invoked once
    }

    [Fact]
    public async Task GetAsync_Arity0_Works()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal(42, await memo.GetAsync(() => Task.FromResult(42), policy));
        Assert.Equal(42, await memo.GetAsync(_ => Task.FromResult(42), policy));
    }

    [Fact]
    public async Task GetAsync_Arity1_Works()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal("1", await memo.GetAsync(1, (a) => Task.FromResult(a.ToString()), policy));
        Assert.Equal("1", await memo.GetAsync(1, (a, _) => Task.FromResult(a.ToString()), policy));
    }

    [Fact]
    public async Task GetAsync_Arity2_Works_Independent()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal("12", await memo.GetAsync(1, 2, (a, b) => Task.FromResult($"{a}{b}"), policy));
        Assert.Equal("12", await memo.GetAsync(1, 2, (a, b, _) => Task.FromResult($"{a}{b}"), policy));
    }

    [Fact]
    public async Task GetAsync_Arity3_Works_Independent()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal("123", await memo.GetAsync(1, 2, 3, (a, b, c) => Task.FromResult($"{a}{b}{c}"), policy));
        Assert.Equal(
            "123",
            await memo.GetAsync(
                1,
                2,
                3,
                (
                    a,
                    b,
                    c,
                    _) => Task.FromResult($"{a}{b}{c}"),
                policy));
    }

    [Fact]
    public async Task GetAsync_Arity4_Works()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal(
            "1234",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                (
                    a,
                    b,
                    c,
                    d) => Task.FromResult($"{a}{b}{c}{d}"),
                policy));
        Assert.Equal(
            "1234",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                (
                    a,
                    b,
                    c,
                    d,
                    _) => Task.FromResult($"{a}{b}{c}{d}"),
                policy));
    }

    [Fact]
    public async Task GetAsync_Arity5_Works_Granular()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal(
            "12345",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                (
                    a,
                    b,
                    c,
                    d,
                    e) => Task.FromResult($"{a}{b}{c}{d}{e}"),
                policy));
        Assert.Equal(
            "12345",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    _) => Task.FromResult($"{a}{b}{c}{d}{e}"),
                policy));
    }

    [Fact]
    public async Task GetAsync_Arity6_Works_Granular()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal(
            "123456",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                6,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    f) => Task.FromResult($"{a}{b}{c}{d}{e}{f}"),
                policy));
        Assert.Equal(
            "123456",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                6,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    f,
                    _) => Task.FromResult($"{a}{b}{c}{d}{e}{f}"),
                policy));
    }

    [Fact]
    public async Task GetAsync_Arity7_Works_Granular()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal(
            "1234567",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    f,
                    g) => Task.FromResult($"{a}{b}{c}{d}{e}{f}{g}"),
                policy));
        Assert.Equal(
            "1234567",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    f,
                    g,
                    _) => Task.FromResult($"{a}{b}{c}{d}{e}{f}{g}"),
                policy));
    }

    [Fact]
    public async Task GetAsync_Arity8_Works_Granular()
    {
        var memo = CreateMemo();
        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        Assert.Equal(
            "12345678",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                8,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    f,
                    g,
                    h) => Task.FromResult($"{a}{b}{c}{d}{e}{f}{g}{h}"),
                policy));
        Assert.Equal(
            "12345678",
            await memo.GetAsync(
                1,
                2,
                3,
                4,
                5,
                6,
                7,
                8,
                (
                    a,
                    b,
                    c,
                    d,
                    e,
                    f,
                    g,
                    h,
                    _) => Task.FromResult($"{a}{b}{c}{d}{e}{f}{g}{h}"),
                policy));
    }

    [Fact]
    public async Task GetAsync_FactoryReturnsNull_CachesAndRetrievesNull()
    {
        var memo = CreateMemo();
        var callCount = 0;

        Task<string?> Factory()
        {
            Interlocked.Increment(ref callCount);

            return Task.FromResult<string?>(null);
        }

        var policy = CachePolicy.Absolute(TimeSpan.FromMinutes(1));
        async Task<string?> GetValue() => await memo.GetAsync(Factory, policy);

        var result1 = await GetValue();
        var result2 = await GetValue();

        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Dispose_DisposesSemaphoresAndClearsLocks()
    {
        var memo = CreateMemo();
        var tcs = new TaskCompletionSource<int>();

        // Start a call that will wait, keeping the lock in the dictionary
        var task = memo.GetAsync(() => tcs.Task, CachePolicy.Absolute(TimeSpan.FromMinutes(1)));

        // Access internal locks via reflection to verify state
        var locksField = typeof(Memo).GetField(
            "_locks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var locks = (ConcurrentDictionary<object, SemaphoreSlim>)locksField!.GetValue(memo)!;

        Assert.NotEmpty(locks);

        memo.Dispose();

        Assert.Empty(locks);

        tcs.SetResult(42);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }
}
