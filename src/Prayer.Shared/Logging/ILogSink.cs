public interface ILogSink
{
    void Enqueue(LogEvent evt);
}

public static class LogSink
{
    private static ILogSink _instance = NullLogSink.Instance;

    public static ILogSink Instance => _instance;

    public static void SetInstance(ILogSink sink)
    {
        _instance = sink ?? NullLogSink.Instance;
    }
}

internal sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    public void Enqueue(LogEvent evt) { }
}
