namespace PerfectWorldAgent.LLM;

public interface ILanguageModel
{
    Task<string> AnalyzeAsync(byte[] screenshot, string context, CancellationToken cancellationToken);
}
