using System;

public sealed class RateLimitStopException : Exception
{
    public RateLimitStopException(string message, int? retryAfterSeconds = null)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int? RetryAfterSeconds { get; }
}
