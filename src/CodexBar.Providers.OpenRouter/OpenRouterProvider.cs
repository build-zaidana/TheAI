using System.Text.Json;
using CodexBar.Providers;
using CodexBar.Providers.OpenAiCompatible;

namespace CodexBar.Providers.OpenRouter;

/// <summary>
/// OpenRouter provider (OpenAI-compatible chat-completions gateway to many models).
/// Unlike a single-vendor provider, the model list is fetched live from OpenRouter's
/// public catalog on each <see cref="ListModelsAsync"/> call, since the whole point of
/// OpenRouter is access to a large, frequently-changing set of models; a hardcoded list
/// falls back to <see cref="KnownModels"/> when the catalog request fails.
/// </summary>
public sealed class OpenRouterProvider(
    HttpClient httpClient,
    Func<string?> apiKeyProvider,
    Func<string, string>? localize = null)
    : OpenAiCompatibleProvider(
        httpClient,
        "openrouter",
        "OpenRouter",
        new Uri("https://openrouter.ai/api/v1/"),
        "chat/completions",
        apiKeyProvider,
        localize,
        OpenRouterProvider.KnownModels(),
        OpenRouterProvider.DefaultModel)
{
    private static readonly Uri ModelsUri = new("https://openrouter.ai/api/v1/models");

    internal static string DefaultModel => "openai/gpt-4.1";

    internal static IReadOnlyList<ProviderModelInfo> KnownModels() =>
    [
        new("openai/gpt-4.1", "openai/gpt-4.1", "OpenAI: GPT-4.1", "Seimbang (default)", true, "default"),
        new("anthropic/claude-sonnet-5", "anthropic/claude-sonnet-5", "Anthropic: Claude Sonnet 5", "Seimbang", false, "default"),
        new("google/gemini-2.5-pro", "google/gemini-2.5-pro", "Google: Gemini 2.5 Pro", "Konteks besar", false, "default"),
        new("meta-llama/llama-4-maverick", "meta-llama/llama-4-maverick", "Meta: Llama 4 Maverick", "Open-weight", false, "default"),
    ];

    public override async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(ModelsUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return KnownModels();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return KnownModels();
            }

            var models = new List<ProviderModelInfo>();
            foreach (var entry in data.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var name = entry.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : id;
                var description = entry.TryGetProperty("description", out var descElement)
                    ? descElement.GetString() ?? ""
                    : "";
                models.Add(new ProviderModelInfo(
                    id, id, name ?? id, Truncate(description, 140), id == DefaultModel, "default"));
            }

            return models.Count > 0 ? models : KnownModels();
        }
        catch (Exception)
        {
            return KnownModels();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
