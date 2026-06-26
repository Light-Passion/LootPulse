using System.Net;
using LootPulse.Services.Trade;
using Xunit;

namespace LootPulse.Tests;

public class TradeRateLimiterTests
{
    private class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = new();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            foreach (var timer in _timers.ToList())
            {
                timer.Check(_utcNow);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, _utcNow);
            _timers.Add(timer);
            return timer;
        }

        private class ManualTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset _triggerTime;
            private bool _active;

            public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, DateTimeOffset now)
            {
                _callback = callback;
                _state = state;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _triggerTime = now + dueTime;
                    _active = true;
                }
            }

            public void Check(DateTimeOffset now)
            {
                if (_active && now >= _triggerTime)
                {
                    _active = false;
                    _callback(_state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _active = false;
                }
                else
                {
                    // For simplicity in tests, assume Change is called to set a new trigger from current "now"
                    // (though in reality it's from when Change is called).
                    // In Task.Delay, Change isn't typically called after creation.
                }
                return true;
            }

            public void Dispose() { _active = false; }
            public ValueTask DisposeAsync() { _active = false; return ValueTask.CompletedTask; }
        }
    }

    private readonly ManualTimeProvider _timeProvider = new();

    [Fact]
    public async Task WaitTurnAsync_FirstCall_ReturnsImmediately()
    {
        // Arrange
        using var limiter = new TradeRateLimiter(_timeProvider);
        var startTime = _timeProvider.GetUtcNow();

        // Act
        await limiter.WaitTurnAsync();

        // Assert
        Assert.Equal(startTime, _timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task WaitTurnAsync_ConsecutiveCalls_EnforcesDelay()
    {
        // Arrange
        using var limiter = new TradeRateLimiter(_timeProvider);
        await limiter.WaitTurnAsync(); // First call sets _nextAllowedUtc to +5s
        var firstCallFinished = _timeProvider.GetUtcNow();

        // Act
        var secondTurnTask = limiter.WaitTurnAsync();

        // Ensure it's waiting
        await Task.Delay(50);
        Assert.False(secondTurnTask.IsCompleted);

        _timeProvider.Advance(TimeSpan.FromSeconds(5));
        await secondTurnTask;

        // Assert
        Assert.Equal(firstCallFinished + TimeSpan.FromSeconds(5), _timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task Observe_RateLimitHeaders_AdaptsInterval()
    {
        // Arrange
        using var limiter = new TradeRateLimiter(_timeProvider);
        var response = new TradeHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
            Headers = new Dictionary<string, string>
            {
                { "x-rate-limit-rules", "ip" },
                { "x-rate-limit-ip", "10:1:60" } // 10 requests per 1 second -> 100ms per request
            }
        };

        // Act
        limiter.Observe(response);
        await limiter.WaitTurnAsync(); // Sets _nextAllowedUtc to +350ms (100ms + 250ms margin)
        var firstCallFinished = _timeProvider.GetUtcNow();

        var secondTurnTask = limiter.WaitTurnAsync();

        _timeProvider.Advance(TimeSpan.FromMilliseconds(350));
        await secondTurnTask;

        // Assert
        Assert.Equal(firstCallFinished + TimeSpan.FromMilliseconds(350), _timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task Observe_429RetryAfter_EnforcesPenalty()
    {
        // Arrange
        using var limiter = new TradeRateLimiter(_timeProvider);
        var response = new TradeHttpResponse
        {
            StatusCode = (HttpStatusCode)429,
            Headers = new Dictionary<string, string>
            {
                { "retry-after", "30" }
            }
        };

        // Act
        limiter.Observe(response);
        var beforeWait = _timeProvider.GetUtcNow();

        var waitTask = limiter.WaitTurnAsync();
        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        await waitTask;

        // Assert
        Assert.Equal(beforeWait + TimeSpan.FromSeconds(30), _timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task WaitTurnAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        using var limiter = new TradeRateLimiter(_timeProvider);
        await limiter.WaitTurnAsync();
        using var cts = new CancellationTokenSource();

        // Act
        var task = limiter.WaitTurnAsync(cts.Token);
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task WaitTurnAsync_ConcurrentCalls_AreSerialized()
    {
        // Arrange
        using var limiter = new TradeRateLimiter(_timeProvider);
        var results = new List<DateTimeOffset>();
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await limiter.WaitTurnAsync();
                lock(results) results.Add(_timeProvider.GetUtcNow());
            }));
        }

        // First one should finish immediately
        await Task.Delay(100);
        lock(results) Assert.Single(results);

        // Second one
        _timeProvider.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        lock(results) Assert.Equal(2, results.Count);

        // Third one
        _timeProvider.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        lock(results) Assert.Equal(3, results.Count);

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r == new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains(results, r => r == new DateTimeOffset(2025, 1, 1, 0, 0, 5, TimeSpan.Zero));
        Assert.Contains(results, r => r == new DateTimeOffset(2025, 1, 1, 0, 0, 10, TimeSpan.Zero));
    }
}
