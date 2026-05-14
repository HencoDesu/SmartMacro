namespace PerfectWorldAgent.LLM;

public sealed class OllamaModel : ILanguageModel
{
    private readonly string _endpoint;
    private readonly string _modelName;

    public OllamaModel(string endpoint, string modelName)
    {
        _endpoint = endpoint;
        _modelName = modelName;
    }

    public Task<string> AnalyzeAsync(byte[] screenshot, string context, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Ollama HTTP API /api/generate with vision model");
}
