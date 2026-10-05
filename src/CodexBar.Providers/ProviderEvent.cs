using System.Text.Json;

namespace CodexBar.Providers;

public abstract record ProviderEvent(DateTimeOffset Timestamp);

public sealed record ProviderStatusEvent(string Message)
    : ProviderEvent(DateTimeOffset.Now);

public sealed record ProviderTextDeltaEvent(string Delta)
    : ProviderEvent(DateTimeOffset.Now);

/// <summary>Potongan streaming "proses berpikir" (reasoning) agent.</summary>
public sealed record ProviderReasoningDeltaEvent(string Delta)
    : ProviderEvent(DateTimeOffset.Now);

/// <summary>Pemakaian token diperbarui untuk thread/turn aktif.</summary>
public sealed record ProviderTokenUsageEvent(
    long TotalTokens,
    long LastTurnTokens,
    long? ContextWindow)
    : ProviderEvent(DateTimeOffset.Now);

public sealed record ProviderThreadStatusEvent(
    string ThreadId,
    string Status,
    IReadOnlyList<string> ActiveFlags)
    : ProviderEvent(DateTimeOffset.Now);

public sealed record ProviderCompletedEvent(string Status)
    : ProviderEvent(DateTimeOffset.Now);

public sealed record ProviderErrorEvent(string Message, bool WillRetry = false)
    : ProviderEvent(DateTimeOffset.Now);

public enum ProviderApprovalDecision
{
    AcceptOnce,
    AcceptForSession,
    Decline,
    Cancel
}

public sealed record ProviderApprovalEvent(
    string Method,
    string Summary,
    Func<ProviderApprovalDecision, Task> RespondAsync,
    JsonElement RawParams,
    Func<bool>? IsPending = null)
    : ProviderEvent(DateTimeOffset.Now);

public sealed record ProviderInputOption(string Label, string Description, string? Value = null);

public sealed record ProviderInputQuestion(
    string Id,
    string Header,
    string Question,
    IReadOnlyList<ProviderInputOption> Options,
    bool AllowsOther = false,
    bool IsSecret = false,
    bool AllowsMultiple = false,
    bool IsRequired = true,
    string ValueType = "string");

/// <summary>
/// Input terstruktur yang diminta agent selama turn. Jawaban null berarti pengguna membatalkan.
/// </summary>
public sealed record ProviderUserInputEvent(
    string Title,
    string Message,
    IReadOnlyList<ProviderInputQuestion> Questions,
    Func<IReadOnlyDictionary<string, IReadOnlyList<string>>?, Task> RespondAsync,
    JsonElement RawParams,
    ulong? AutoResolutionMs = null,
    Func<bool>? IsPending = null,
    string? ActionUrl = null)
    : ProviderEvent(DateTimeOffset.Now);

/// <summary>
/// One file the agent changed. <c>Kind</c> is "add", "delete" or "update"; <c>Diff</c> is a
/// unified-style diff (or the full content for an added file), possibly empty.
/// </summary>
public sealed record ProviderFileChange(string Path, string Kind, string Diff, string? MovePath = null);

/// <summary>
/// The current turn's work so far (tool calls and the text between them) and the answer text
/// written after the last tool call; replaces what was streamed as text until now.
/// </summary>
public sealed record ProviderActivityEvent(IReadOnlyList<ProviderActivityStep> Steps, string Answer)
    : ProviderEvent(DateTimeOffset.Now);

/// <summary>Files changed by a completed edit step of the current turn.</summary>
public sealed record ProviderFileChangeEvent(IReadOnlyList<ProviderFileChange> Changes)
    : ProviderEvent(DateTimeOffset.Now);

/// <summary>
/// Usage of one subscription limit window. <c>Id</c> is "session" (the rolling ~5 hour window),
/// "weekly", or a provider-specific name; <c>UsedFraction</c> runs from 0 to 1.
/// </summary>
public sealed record ProviderQuotaWindow(string Id, double UsedFraction, DateTimeOffset? ResetsAt);

/// <summary>Latest known subscription usage for the active account.</summary>
public sealed record ProviderQuotaEvent(IReadOnlyList<ProviderQuotaWindow> Windows, bool LimitReached = false)
    : ProviderEvent(DateTimeOffset.Now);
