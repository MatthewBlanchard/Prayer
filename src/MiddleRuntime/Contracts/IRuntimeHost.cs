using System.Threading;
using System.Threading.Tasks;

public interface IRuntimeHost
{
    void SetScript(string script, bool preserveAssociatedPrompt = true);

    void GenerateScript(string prompt);

    Task<string?> GenerateScriptAsync(string prompt);

    bool Interrupt(string reason = "Interrupted");

    void Halt(string reason = "Halted");

    RuntimeHostSnapshot GetSnapshot();

    Task TickAsync(CancellationToken token = default);

    Task RunAsync(CancellationToken token);
}
