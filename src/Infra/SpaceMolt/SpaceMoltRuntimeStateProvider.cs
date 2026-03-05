using System;
using System.Threading.Tasks;

public sealed class SpaceMoltRuntimeStateProvider : IRuntimeStateProvider
{
    private readonly SpaceMoltHttpClient _client;

    public SpaceMoltRuntimeStateProvider(SpaceMoltHttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<GameState> GetLatestStateAsync()
    {
        try
        {
            return Task.FromResult(_client.GetGameState());
        }
        catch (RateLimitStopException ex)
        {
            throw new RuntimeRateLimitException(ex.Message, ex.RetryAfterSeconds);
        }
    }
}
