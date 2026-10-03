using System.IO;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Providers;

namespace CodexBar.App;

// Parallel agents. Each one is its own provider instance (its own Codex app-server or Claude
// Code process) with its own controller and a fresh thread in the current workspace, so it can
// run next to the main conversation without sharing a turn, a queue or a busy flag. Events are
// forwarded to the notch under the agent's key; approvals and questions go through the same
// gate as the main conversation's, so only one request is on screen at a time. The threads are
// ordinary threads: once an agent finishes, "open in window" resumes it in the main window.
public partial class MainWindow
{
    private const int MaxParallelAgents = 3;

    private sealed class ParallelAgent(string key, IAgentProvider provider, ConversationController controller, string workspacePath)
    {
        public string Key { get; } = key;
        public IAgentProvider Provider { get; } = provider;
        public ConversationController Controller { get; } = controller;
        public string WorkspacePath { get; } = workspacePath;
        public string Title { get; set; } = string.Empty;
        public System.Text.StringBuilder Answer { get; } = new();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
        public string WorkspaceLabel => new DirectoryInfo(WorkspacePath).Name;
    }

    private readonly Dictionary<string, ParallelAgent> _agents = new(StringComparer.Ordinal);

    async Task<string> INotchHost.StartParallelAgentAsync(string prompt)
    {
        if (!_controller.Capabilities.SupportsThreads)
        {
            throw new InvalidOperationException(L("parallel.unsupported"));
        }

        if (_agents.Values.Count(agent => agent.Controller.IsBusy) >= MaxParallelAgents)
        {
            throw new InvalidOperationException(LF("parallel.limit", MaxParallelAgents));
        }

        var workspace = _controller.WorkspacePath ?? _settings.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            throw new InvalidOperationException(L("parallel.noWorkspace"));
        }

        // The agent takes the attachments that are waiting, like a sent prompt would.
        var attachments = _attachments.ToArray();
        _attachments.Clear();
        RenderAttachments();

        var key = "agent-" + Guid.NewGuid().ToString("N")[..8];
        var provider = _providerFactory(_controller.ProviderId);
        var controller = new ConversationController(provider);
        var agent = new ParallelAgent(key, provider, controller, workspace);
        _agents[key] = agent;
        controller.EventReceived += (_, providerEvent) =>
            Dispatcher.BeginInvoke(() => HandleAgentEvent(agent, providerEvent));

        await SendToAgentAsync(agent, prompt, attachments, startThread: true);
        return key;
    }

    async Task INotchHost.SendToActivityAsync(string key, string prompt)
    {
        if (_agents.TryGetValue(key, out var agent) && !agent.Controller.IsBusy)
        {
            await SendToAgentAsync(agent, prompt, [], startThread: false);
        }
    }

    async Task INotchHost.StopActivityAsync(string key)
    {
        if (_agents.TryGetValue(key, out var agent) && agent.Controller.IsBusy)
        {
            try
            {
                await agent.Controller.CancelAsync();
            }
            catch (Exception exception)
            {
                DiagnosticsLog.Error("ParallelAgent", exception.Message);
            }
        }
    }

    void INotchHost.DismissActivity(string key)
    {
        if (_agents.Remove(key, out var agent))
        {
            _ = DisposeAgentAsync(agent);
        }
    }

    async Task INotchHost.OpenActivityInWindowAsync(string key)
    {
        if (!_agents.TryGetValue(key, out var agent) || agent.Controller.ThreadId is not { } threadId)
        {
            return;
        }

        if (agent.Controller.IsBusy)
        {
            throw new InvalidOperationException(L("parallel.stillRunning"));
        }

        // Hand the thread to the main window and let go of the agent's own process.
        _agents.Remove(key);
        _notch?.RemoveActivity(key);
        await DisposeAgentAsync(agent);
        ShowFromExternalActivation();
        await OpenThreadFromSidebarAsync(new ProviderThreadSummary(
            threadId, agent.Title, string.Empty, agent.WorkspacePath, DateTimeOffset.Now));
    }

    private async Task SendToAgentAsync(ParallelAgent agent, string prompt, ProviderInputAttachment[] attachments, bool startThread)
    {
        var display = prompt.Length > 0 ? prompt : string.Join(", ", attachments.Select(item => item.DisplayName));
        if (startThread)
        {
            agent.Title = display;
        }

        _notch?.BeginActivity(agent.Key, display, ((INotchHost)this).ProviderName, agent.WorkspaceLabel);
        agent.Answer.Clear();
        agent.StartedAt = DateTimeOffset.Now;
        try
        {
            if (startThread)
            {
                await agent.Controller.InitializeAsync();
                await agent.Controller.OpenWorkspaceAsync(agent.WorkspacePath, null, _settings.SandboxMode, _settings.ApprovalPolicy);
            }

            var model = (ModelCombo.SelectedItem as ProviderModelInfo)?.Model ?? _settings.SelectedModel;
            var effort = string.IsNullOrWhiteSpace(_settings.ReasoningEffort) ? null : _settings.ReasoningEffort;
            DiagnosticsLog.Info("ParallelAgent", $"{agent.Key} send provider={_controller.ProviderId} promptChars={prompt.Length}");
            await agent.Controller.SendAsync(prompt, attachments, model, effort);
            CleanupTemporaryAttachments(attachments.Select(item => item.Path));
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ParallelAgent", exception.Message);
            _notch?.HandleProviderEvent(new ProviderErrorEvent(exception.Message), agent.Key);
        }
    }

    private void HandleAgentEvent(ParallelAgent agent, ProviderEvent providerEvent)
    {
        if (!_agents.ContainsKey(agent.Key))
        {
            return;
        }

        switch (providerEvent)
        {
            case ProviderQuotaEvent quota:
                StoreQuota(agent.Provider.Id, quota, persist: true);
                _notch?.HandleProviderEvent(quota);
                break;

            case ProviderApprovalEvent approval:
                _ = ShowApprovalAsync(approval, agent.Controller.ThreadId, agent.Key);
                break;

            case ProviderUserInputEvent input:
                _ = ShowUserInputAsync(input, agent.Controller.ThreadId, agent.Key);
                break;

            case ProviderTextDeltaEvent text:
                agent.Answer.Append(text.Delta);
                break;

            case ProviderCompletedEvent completed:
                DiagnosticsLog.Info("ParallelAgent", $"{agent.Key} completed status={completed.Status}");
                if (completed.Status == "completed")
                {
                    NotifyCompletion(agent.Key, ((INotchHost)this).ProviderName, agent.WorkspaceLabel,
                        agent.Answer.ToString(), DateTimeOffset.Now - agent.StartedAt);
                }
                break;
        }

        if (providerEvent is not ProviderQuotaEvent)
        {
            _notch?.HandleProviderEvent(providerEvent, agent.Key);
        }
    }

    private static async Task DisposeAgentAsync(ParallelAgent agent)
    {
        try
        {
            if (agent.Controller.IsBusy)
            {
                await agent.Controller.CancelAsync();
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ParallelAgent", exception.Message);
        }

        try
        {
            await agent.Provider.DisposeAsync();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("ParallelAgent", exception.Message);
        }
    }

    private void DisposeParallelAgents()
    {
        foreach (var agent in _agents.Values.ToArray())
        {
            _ = DisposeAgentAsync(agent);
        }

        _agents.Clear();
    }
}
