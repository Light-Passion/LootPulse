using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LootPulse.Services.Trade
{
    /// <summary>
    /// Serializes all trade API requests through a single async gate and spaces them out to respect
    /// GGG's dynamic rate limits. Starts conservative (1 request / 5s, matching Exiled Exchange 2's
    /// default) and widens/narrows the interval from the live <c>X-Rate-Limit-*</c> response headers.
    /// On a 429 it honors the <c>Retry-After</c> hint.
    /// </summary>
    public sealed class TradeRateLimiter : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly TimeProvider _timeProvider;
        private DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;

        // Conservative default: 1 request every 5 seconds.
        private TimeSpan _minInterval = TimeSpan.FromSeconds(5);

        public TradeRateLimiter(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Wait until the next request is allowed. Call before each API request.</summary>
        public async Task WaitTurnAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                if (now < _nextAllowedUtc)
                {
                    var delay = _nextAllowedUtc - now;
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }
                _nextAllowedUtc = _timeProvider.GetUtcNow() + _minInterval;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Feed back the response so the limiter can adapt. Parses the active rate-limit policy and,
        /// on 429, pushes the next-allowed time out by Retry-After.
        /// </summary>
        public void Observe(TradeHttpResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);

            // x-rate-limit-rules lists the policy names, e.g. "Ip,Account". Each named rule has a
            // header like x-rate-limit-ip: "8:10:60,15:60:120" => max:period(s):penalty groups.
            string? rules = response.GetHeader("x-rate-limit-rules");
            if (!string.IsNullOrEmpty(rules))
            {
                TimeSpan tightest = TimeSpan.Zero;
                foreach (var rule in rules.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string? policy = response.GetHeader($"x-rate-limit-{rule.Trim()}");
                    var interval = TightestInterval(policy);
                    if (interval > tightest) tightest = interval;
                }
                if (tightest > TimeSpan.Zero)
                {
                    // Add a small safety margin so we stay strictly under the limit.
                    _minInterval = tightest + TimeSpan.FromMilliseconds(250);
                }
            }

            if ((int)response.StatusCode == 429)
            {
                double retrySeconds = 10;
                string? retryAfter = response.GetHeader("retry-after");
                if (!string.IsNullOrEmpty(retryAfter) &&
                    double.TryParse(retryAfter, NumberStyles.Any, CultureInfo.InvariantCulture, out var ra))
                {
                    retrySeconds = ra;
                }
                _nextAllowedUtc = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(retrySeconds);
            }
        }

        // Given "8:10:60,15:60:120" return the largest (period/max) interval = slowest required spacing.
        private static TimeSpan TightestInterval(string? policy)
        {
            if (string.IsNullOrEmpty(policy)) return TimeSpan.Zero;

            TimeSpan tightest = TimeSpan.Zero;
            foreach (var group in policy.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = group.Split(':');
                if (parts.Length < 2) continue;
                if (int.TryParse(parts[0], out var max) && max > 0 &&
                    int.TryParse(parts[1], out var periodSec) && periodSec > 0)
                {
                    var perRequest = TimeSpan.FromSeconds((double)periodSec / max);
                    if (perRequest > tightest) tightest = perRequest;
                }
            }
            return tightest;
        }

        public void Dispose() => _gate.Dispose();
    }
}
