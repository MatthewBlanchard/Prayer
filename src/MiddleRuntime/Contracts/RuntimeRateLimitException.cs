using System;

public sealed class RuntimeRateLimitException : Exception
{
    public RuntimeRateLimitException(string message, int? retryAfterSeconds = null)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int? RetryAfterSeconds { get; }
}
