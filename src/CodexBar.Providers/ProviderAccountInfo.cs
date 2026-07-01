namespace CodexBar.Providers;

public sealed record ProviderAccountInfo(
    bool IsAuthenticated,
    string AuthenticationMethod,
    string? Email,
    string? PlanType,
    bool RequiresOpenAiAuth);

