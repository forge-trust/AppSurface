using ForgeTrust.AppSurface.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core.Tests;

public class EnumerableExtensionsTests
{
    [Fact]
    public async Task ParallelSelectAsyncEnumerable_PreservesOrder()
    {
        // Arrange
        var input = Enumerable.Range(0, 10).ToList();

        // Act
        // Variable delays to simulate out-of-order completion
        var results = new List<int>();
        await foreach (var item in input.ParallelSelectAsyncEnumerable(
                           async x =>
                           {
                               // Even numbers wait longer, odd numbers complete fast
                               // This ensures they would arrive out of order if not re-ordered by the enumerable
                               await Task.Delay(x % 2 == 0 ? 100 : 10);

                               return x;
                           },
                           maxDegreeOfParallelism: 4))
        {
            results.Add(item);
        }

        // Assert
        Assert.Equal(input, results);
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_HandlesEmptySource()
    {
        // Arrange
        var input = Enumerable.Empty<int>();

        // Act
        var results = new List<int>();
        await foreach (var item in input.ParallelSelectAsyncEnumerable(async x => await Task.FromResult(x), 4))
        {
            results.Add(item);
        }

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_ExceptionInBody_Propagates()
    {
        // Arrange
        var input = Enumerable.Range(0, 5);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in input.ParallelSelectAsyncEnumerable(
                               async x =>
                               {
                                   if (x == 3)
                                   {
                                       throw new InvalidOperationException("Test Error");
                                   }

                                   return await Task.FromResult(x);
                               },
                               2))
            {
                // Intentionally empty: drives execution to verify exception propagation
            }
        });
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_RespectsMaxDegreeOfParallelism()
    {
        // This is a rough test since perfect concurrency testing is hard.
        // We verify that we don't start ALL tasks at once.

        // Arrange
        var input = Enumerable.Range(0, 10);
        var activeTasks = 0;
        var maxObservedTasks = 0;
        var lockObj = new object();

        // Act
        await foreach (var _ in input.ParallelSelectAsyncEnumerable(
                           async x =>
                           {
                               lock (lockObj)
                               {
                                   activeTasks++;
                                   maxObservedTasks = Math.Max(maxObservedTasks, activeTasks);
                               }

                               await Task.Delay(100);
                               lock (lockObj)
                               {
                                   activeTasks--;
                               }

                               return x;
                           },
                           maxDegreeOfParallelism: 2))
        {
            // Intentionally empty: drives execution to verify degree of parallelism
        }

        // Assert
        // We strictly assert maxObservedTasks <= 2 since we increased the delay to reduce flakiness.
        Assert.True(maxObservedTasks <= 2, $"Expected max degree 2, but observed {maxObservedTasks}");
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_EarlyBreak_CompletesProducerTask()
    {
        // Arrange
        var input = Enumerable.Range(0, 100);
        var startedCount = 0;

        // Act
        await foreach (var item in input.ParallelSelectAsyncEnumerable(
                           async x =>
                           {
                               Interlocked.Increment(ref startedCount);
                               await Task.Delay(50);

                               return x;
                           },
                           maxDegreeOfParallelism: 10))
        {
            if (item == 5)
            {
                break;
            }
        }

        // Wait a bit to ensure cleanup would have happened
        await Task.Delay(200);

        // Assert
        // With 10 parallelism and breaking at item 5, we expect some tasks to have started but not all 100.
        // With bufferMultiplier 4x and DOP 10, the buffer is 40. We allow some headroom and assert <= 50.
        Assert.True(
            startedCount <= 50,
            $"Expected producer to stop, but {startedCount} tasks were started (expected <= 50).");
    }

    #region ParallelSelectAsync Tests

    [Fact]
    public async Task ParallelSelectAsync_ValidInput_ReturnsResults()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);

        // Act
        var results = await input.ParallelSelectAsync(
            async (x, ct) =>
            {
                await Task.Delay(10, ct);
                return x * 2;
            },
            maxDegreeOfParallelism: 2);

        // Assert
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, results);
    }

    [Fact]
    public async Task ParallelSelectAsync_NullSource_ThrowsArgumentNullException()
    {
        // Arrange
        IEnumerable<int>? source = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await source!.ParallelSelectAsync(async (x, ct) => x, 2));
    }

    [Fact]
    public async Task ParallelSelectAsync_NullBody_ThrowsArgumentNullException()
    {
        // Arrange
        var source = Enumerable.Range(1, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await source.ParallelSelectAsync((Func<int, CancellationToken, Task<int>>)null!, 2));
    }

    [Fact]
    public async Task ParallelSelectAsync_InvalidParallelism_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var source = Enumerable.Range(1, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await source.ParallelSelectAsync(async (x, ct) => x, 0));
    }

    [Fact]
    public async Task ParallelSelectAsync_CancellationToken_CancelsOperation()
    {
        // Arrange
        var input = Enumerable.Range(1, 100);
        using var cts = new CancellationTokenSource();
        var processedCount = 0;

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await input.ParallelSelectAsync(
                async (x, ct) =>
                {
                    Interlocked.Increment(ref processedCount);
                    if (processedCount >= 5)
                    {
                        await cts.CancelAsync();
                    }
                    await Task.Delay(50, ct);
                    return x;
                },
                maxDegreeOfParallelism: 2,
                cancellationToken: cts.Token);
        });

        // Verify that not all items were processed
        Assert.True(processedCount < 100);
    }

    [Fact]
    public async Task ParallelSelectAsync_CancellationToken_DoesNotLogCleanupFailure_WhenLoggerProvided()
    {
        // Arrange
        var input = Enumerable.Range(1, 100);
        using var cts = new CancellationTokenSource();
        var logger = new TestLogger();
        var processedCount = 0;

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await input.ParallelSelectAsync(
                async (x, ct) =>
                {
                    if (Interlocked.Increment(ref processedCount) >= 5)
                    {
                        await cts.CancelAsync();
                    }

                    await Task.Delay(50, ct);
                    return x;
                },
                maxDegreeOfParallelism: 2,
                cancellationToken: cts.Token,
                logger: logger);
        });

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ParallelSelectAsync_BodyThrowsException_PropagatesException()
    {
        // Arrange
        var input = Enumerable.Range(1, 10);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await input.ParallelSelectAsync(
                async (x, ct) =>
                {
                    if (x == 5)
                    {
                        throw new InvalidOperationException("Test exception");
                    }
                    await Task.Delay(10, ct);
                    return x;
                },
                maxDegreeOfParallelism: 3);
        });
    }

    [Fact]
    public async Task ParallelSelectAsync_BodyThrowsException_LogsSuppressedCleanupFailure_WhenLoggerProvided()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);
        var logger = new TestLogger();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await input.ParallelSelectAsync(
                async (x, ct) =>
                {
                    await Task.Delay(10, ct);
                    if (x == 2)
                    {
                        throw new InvalidOperationException("Primary failure");
                    }

                    return x;
                },
                maxDegreeOfParallelism: 2,
                cancellationToken: CancellationToken.None,
                logger: logger);
        });

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Debug
                && entry.EventId.Id == 1102
                && entry.Exception is InvalidOperationException
                && entry.Message.Contains("suppressed task cleanup failures", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelSelectAsync_WithLoggerOverload_ThrowsWhenLoggerIsNull()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await input.ParallelSelectAsync(
                async (x, ct) => await Task.FromResult(x),
                maxDegreeOfParallelism: 2,
                cancellationToken: CancellationToken.None,
                logger: null!);
        });
    }

    [Fact]
    public async Task ParallelSelectAsync_WithLoggerOverload_ReturnsResults_WhenSuccessful()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);
        var logger = new TestLogger();

        // Act
        var results = await input.ParallelSelectAsync(
            async (x, ct) => await Task.FromResult(x * 2),
            maxDegreeOfParallelism: 2,
            cancellationToken: CancellationToken.None,
            logger: logger);

        // Assert
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, results);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ParallelSelectAsync_PreservesOrder()
    {
        // Arrange
        var input = Enumerable.Range(1, 20);

        // Act - Use variable delays to ensure out-of-order completion
        var results = await input.ParallelSelectAsync(
            async (x, ct) =>
            {
                await Task.Delay(x % 2 == 0 ? 50 : 10, ct);
                return x;
            },
            maxDegreeOfParallelism: 5);

        // Assert
        Assert.Equal(input, results);
    }

    [Fact]
    public async Task ParallelSelectAsync_RespectsMaxDegreeOfParallelism()
    {
        // Arrange
        var input = Enumerable.Range(1, 20);
        var activeTasks = 0;
        var maxObservedTasks = 0;
        var lockObj = new object();

        // Act
        await input.ParallelSelectAsync(
            async (x, ct) =>
            {
                lock (lockObj)
                {
                    activeTasks++;
                    maxObservedTasks = Math.Max(maxObservedTasks, activeTasks);
                }

                await Task.Delay(100, ct);

                lock (lockObj)
                {
                    activeTasks--;
                }

                return x;
            },
            maxDegreeOfParallelism: 3);

        // Assert
        Assert.True(maxObservedTasks <= 3, $"Expected max 3 concurrent tasks, but observed {maxObservedTasks}");
    }

    [Fact]
    public async Task ParallelSelectAsync_EmptySource_ReturnsEmpty()
    {
        // Arrange
        var input = Enumerable.Empty<int>();

        // Act
        var results = await input.ParallelSelectAsync(async (x, ct) => x * 2, 2);

        // Assert
        Assert.Empty(results);
    }

    #endregion

    #region ParallelSelectAsyncEnumerable Validation Tests

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_NullSource_ThrowsArgumentNullException()
    {
        // Arrange
        IEnumerable<int>? source = null;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in source!.ParallelSelectAsyncEnumerable(async (x, ct) => x, 2))
            {
            }
        });
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_NullBody_ThrowsArgumentNullException()
    {
        // Arrange
        var source = Enumerable.Range(1, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in source.ParallelSelectAsyncEnumerable((Func<int, CancellationToken, Task<int>>)null!, 2))
            {
            }
        });
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_InvalidParallelism_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var source = Enumerable.Range(1, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in source.ParallelSelectAsyncEnumerable(async x => x, 0))
            {
            }
        });
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_InvalidBufferMultiplier_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var source = Enumerable.Range(1, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in source.ParallelSelectAsyncEnumerable(async x => x, 2, bufferMultiplier: 0))
            {
            }
        });
    }

    #endregion

    #region Cancellation and Cleanup Tests

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_CancellationDuringProducer_StopsGracefully()
    {
        // Arrange
        var input = Enumerable.Range(1, 100);
        using var cts = new CancellationTokenSource();
        var processedCount = 0;

        // Act
        try
        {
            await foreach (var item in input.ParallelSelectAsyncEnumerable(
                               async (x, ct) =>
                               {
                                   Interlocked.Increment(ref processedCount);
                                   await Task.Delay(50, ct);
                                   return x;
                               },
                               maxDegreeOfParallelism: 5,
                               cancellationToken: cts.Token))
            {
                if (item >= 10)
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - Not all items should have been processed
        Assert.True(processedCount < 100, $"Expected cancellation to stop processing, but {processedCount} items were processed");
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_CancellationDuringConsumer_CleansUpProperly()
    {
        // Arrange
        var input = Enumerable.Range(1, 50);
        using var cts = new CancellationTokenSource();
        var results = new List<int>();

        // Act
        try
        {
            await foreach (var item in input.ParallelSelectAsyncEnumerable(
                               async x =>
                               {
                                   await Task.Delay(20);
                                   return x;
                               },
                               maxDegreeOfParallelism: 3,
                               cancellationToken: cts.Token))
            {
                results.Add(item);
                if (results.Count >= 5)
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - Should have stopped early
        Assert.True(results.Count < 50);
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_Cancellation_DoesNotLogCleanupFailure_WhenLoggerProvided()
    {
        // Arrange
        var input = Enumerable.Range(1, 50);
        using var cts = new CancellationTokenSource();
        var logger = new TestLogger();
        var results = new List<int>();

        // Act
        try
        {
            await foreach (var item in input.ParallelSelectAsyncEnumerable(
                               async (x, ct) =>
                               {
                                   await Task.Delay(20, ct);
                                   return x;
                               },
                               maxDegreeOfParallelism: 3,
                               bufferMultiplier: 4,
                               cancellationToken: cts.Token,
                               logger: logger))
            {
                results.Add(item);
                if (results.Count >= 5)
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        Assert.True(results.Count < 50);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_CustomBufferMultiplier_Works()
    {
        // Arrange
        var input = Enumerable.Range(1, 20);

        // Act
        var results = new List<int>();
        await foreach (var item in input.ParallelSelectAsyncEnumerable(
                           async x =>
                           {
                               await Task.Delay(10);
                               return x * 2;
                           },
                           maxDegreeOfParallelism: 2,
                           bufferMultiplier: 8))
        {
            results.Add(item);
        }

        // Assert
        Assert.Equal(20, results.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(x => x * 2), results);
    }

    #endregion

    #region Error Scenario Tests

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_ProducerException_PropagatesCorrectly()
    {
        // Arrange - Create a source that throws during enumeration
        var input = ThrowingEnumerable();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in input.ParallelSelectAsyncEnumerable(async x => x, 2))
            {
            }
        });

        static IEnumerable<int> ThrowingEnumerable()
        {
            yield return 1;
            yield return 2;
            throw new InvalidOperationException("Source enumeration failed");
        }
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_MultipleExceptionsInTasks_PropagatesFirst()
    {
        // Arrange
        var input = Enumerable.Range(1, 10);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in input.ParallelSelectAsyncEnumerable(
                               async x =>
                               {
                                   await Task.Delay(10);
                                   if (x >= 3 && x <= 6)
                                   {
                                       throw new InvalidOperationException($"Error at {x}");
                                   }
                                   return x;
                               },
                               maxDegreeOfParallelism: 4))
            {
            }
        });
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_BodyThrowsException_LogsSuppressedCleanupFailure_WhenLoggerProvided()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);
        var logger = new TestLogger();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in input.ParallelSelectAsyncEnumerable(
                               async (x, ct) =>
                               {
                                   await Task.Delay(10, ct);
                                   if (x == 2)
                                   {
                                       throw new InvalidOperationException("Primary failure");
                                   }

                                   return x;
                               },
                               maxDegreeOfParallelism: 2,
                               bufferMultiplier: 4,
                               cancellationToken: CancellationToken.None,
                               logger: logger))
            {
            }
        });

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Debug
                && entry.EventId.Id == 1105
                && entry.Exception is InvalidOperationException
                && entry.Message.Contains("suppressed task cleanup failures", StringComparison.Ordinal));
    }

    [Fact]
    public void ParallelSelectAsyncEnumerable_WithLoggerOverload_ThrowsWhenLoggerIsNull()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            input.ParallelSelectAsyncEnumerable(
                async (x, ct) => await Task.FromResult(x),
                maxDegreeOfParallelism: 2,
                bufferMultiplier: 4,
                cancellationToken: CancellationToken.None,
                logger: null!));
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_WithLoggerOverload_ReturnsResults_WhenSuccessful()
    {
        // Arrange
        var input = Enumerable.Range(1, 5);
        var logger = new TestLogger();

        // Act
        var results = new List<int>();
        await foreach (var item in input.ParallelSelectAsyncEnumerable(
                           async (x, ct) => await Task.FromResult(x * 2),
                           maxDegreeOfParallelism: 2,
                           bufferMultiplier: 4,
                           cancellationToken: CancellationToken.None,
                           logger: logger))
        {
            results.Add(item);
        }

        // Assert
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, results);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_OverflowCapacity_ClampsToIntMax()
    {
        // Act
        // Use a very large DOP and multiplier that would overflow int if not clamped
        var source = Enumerable.Range(1, 5);
        var results = new List<int>();
        await foreach (var item in source.ParallelSelectAsyncEnumerable(
                           async (x, ct) => x,
                           maxDegreeOfParallelism: int.MaxValue / 2,
                           bufferMultiplier: 4))
        {
            results.Add(item);
        }

        // Assert
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task ParallelSelectAsyncEnumerable_ProducerException_CleanupLogic()
    {
        // Arrange
        // A source that throws during iteration
        var source = GetThrowingSource();

        static IEnumerable<int> GetThrowingSource()
        {
            yield return 1;
            throw new Exception("Producer failed");
        }

        // Act & Assert
        var ex = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await foreach (var _ in source.ParallelSelectAsyncEnumerable(async (x, ct) => x, 2))
            {
            }
        });

        Assert.Equal("Producer failed", ex.Message);
    }

    [Fact]
    public async Task ParallelSelectAsync_CancellationToken_JoinsActiveTasksBeforeDisposal()
    {
        // Arrange
        var input = Enumerable.Range(1, 10);
        using var cts = new CancellationTokenSource();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await input.ParallelSelectAsync(
                async (x, ct) =>
                {
                    if (x == 5)
                    {
                        await cts.CancelAsync();
                    }
                    await Task.Delay(100, ct);
                    return x;
                },
                maxDegreeOfParallelism: 5,
                cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task ParallelSelectAsync_IAsyncEnumerable_SequentialResults()
    {
        // Act
        // This targets the overload returning IAsyncEnumerable (Method 2)
        var source = Enumerable.Range(1, 5);
        var results = new List<int>();
        await foreach (var item in source.ParallelSelectAsync(async x => x, 2))
        {
            results.Add(item);
        }

        // Assert
        Assert.Equal(source, results);
    }

    #endregion
}
