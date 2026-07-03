using CodexBar.Providers;
using CodexBar.Providers.OpenAiCompatible;

namespace CodexBar.Providers.OpenAI;

/// <summary>OpenAI Chat Completions API provider (streaming SSE, api-key, no threads/workspace).</summary>
public sealed class OpenAIProvider(
    HttpClient httpClient,
    Func<string?> apiKeyProvider,
    Func<string, string>? localize = null)
    : OpenAiCompatibleProvider(
        httpClient,
        "openai",
        "OpenAI",
        new Uri("https://api.openai.com/v1/"),
        "chat/completions",
        apiKeyProvider,
        localize,
        OpenAIProvider.KnownModels(),
        OpenAIProvider.DefaultModel)
{
    internal static string DefaultModel => "gpt-4.1";

    internal static IReadOnlyList<ProviderModelInfo> KnownModels() =>
    [
        new("gpt-4.1", "gpt-4.1", "GPT-4.1", "Seimbang (default)", true, "default"),
        new("gpt-4.1-mini", "gpt-4.1-mini", "GPT-4.1 Mini", "Lebih cepat & murah", false, "default"),
        new("gpt-4o", "gpt-4o", "GPT-4o", "Multimodal", false, "default"),
        new("o4-mini", "o4-mini", "o4-mini", "Reasoning ringan", false, "default"),
    ];
}
