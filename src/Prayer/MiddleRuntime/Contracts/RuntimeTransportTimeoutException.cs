using System;

public sealed class RuntimeTransportTimeoutException : Exception
{
    public RuntimeTransportTimeoutException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
