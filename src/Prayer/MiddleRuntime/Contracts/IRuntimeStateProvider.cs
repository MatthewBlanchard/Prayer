using System.Threading.Tasks;

public interface IRuntimeStateProvider
{
    Task<GameState> GetLatestStateAsync();
}
