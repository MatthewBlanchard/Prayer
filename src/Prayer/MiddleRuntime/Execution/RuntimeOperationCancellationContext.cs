using System;
using System.Threading;

internal static class RuntimeOperationCancellationContext
{
    private static readonly AsyncLocal<CancellationToken?> CurrentToken = new();

    public static CancellationToken? Current => CurrentToken.Value;

    public static IDisposable Push(CancellationToken token)
    {
        var previous = CurrentToken.Value;
        CurrentToken.Value = token;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CancellationToken? _previous;
        private bool _disposed;

        public Scope(CancellationToken? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            CurrentToken.Value = _previous;
        }
    }
}
