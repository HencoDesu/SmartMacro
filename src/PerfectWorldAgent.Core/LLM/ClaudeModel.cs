namespace PerfectWorldAgent.LLM;

public sealed class ClaudeModel : ILanguageModel
{
    private readonly string _apiKey;
    private readonly string _modelId;

    public ClaudeModel(string apiKey, string modelId)
    {
        _apiKey = apiKey;
        _modelId = modelId;
    }

    public Task<string> AnalyzeAsync(byte[] screenshot, string context, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Anthropic.SDK call with vision content block");
}
