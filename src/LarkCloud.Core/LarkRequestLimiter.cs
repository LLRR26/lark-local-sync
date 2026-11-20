namespace LarkCloud.Core;

/// <summary>Paces request starts and shares rate-limit cooldowns across Lark Drive clients.</summary>
public sealed class LarkRequestLimiter(double minIntervalSeconds = 0.12, TimeProvider? timeProvider = null)
{
    readonly object _gate = new();
    readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    readonly long _start = (timeProvider ?? TimeProvider.System).GetTimestamp();
    readonly TimeSpan _minimum = TimeSpan.FromSeconds(minIntervalSeconds);
    TimeSpan _interval = TimeSpan.FromSeconds(minIntervalSeconds);
    TimeSpan? _lastRequest;
    TimeSpan _cooldownUntil;
    int _successes;

    TimeSpan Now => _clock.GetElapsedTime(_start);

    internal void Wait(Action<TimeSpan, CancellationToken> delay, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (_gate)
            {
                var now = Now;
                var next = _lastRequest + _interval ?? TimeSpan.Zero;
                if (_cooldownUntil > next) next = _cooldownUntil;
                wait = next - now;
                if (wait <= TimeSpan.Zero)
                {
                    _lastRequest = now;
                    return;
                }
            }
            // Recheck after waiting so a new cooldown also applies to callers already in the queue.
            delay(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, ct);
        }
    }

    internal void Throttled(TimeSpan retryAfter)
    {
        lock (_gate)
        {
            _successes = 0;
            _interval = TimeSpan.FromSeconds(Math.Max(_minimum.TotalSeconds, Math.Clamp(_interval.TotalSeconds * 2, 0.05, 2)));
            var until = Now + retryAfter;
            if (until > _cooldownUntil) _cooldownUntil = until;
        }
    }

    internal void Succeeded()
    {
        lock (_gate)
        {
            if (Now < _cooldownUntil || _interval <= _minimum || ++_successes < 20) return;
            _successes = 0;
            var reduced = _interval * 0.75;
            _interval = reduced > _minimum ? reduced : _minimum;
        }
    }
}
