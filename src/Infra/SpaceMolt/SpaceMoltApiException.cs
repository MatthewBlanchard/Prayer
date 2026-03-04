using System;

public sealed class SpaceMoltApiException : Exception
{
    public SpaceMoltApiException(string message)
        : base(message)
    {
    }
}
