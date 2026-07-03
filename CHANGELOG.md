# Changelog

## Unreleased

### Added

- Multi-provider support: choose between **Codex**, **Claude** (Anthropic Messages API), **OpenAI**, and **OpenRouter** from a Settings provider picker. The UI adapts to each provider's capabilities (workspace/approval controls disable when unsupported). API keys are stored in Windows Credential Manager, never in plaintext settings. OpenAI and OpenRouter share an OpenAI-compatible chat-completions provider base; OpenRouter's model list is fetched live from its catalog instead of hardcoded.
- Local conversation persistence for the API-key providers (Claude, OpenAI, OpenRouter): since they have no server-side thread storage, each thread's messages are now saved to `%LOCALAPPDATA%\CodexBar\conversations\{provider}\{threadId}.json` and reloaded when that thread id is resumed, so history survives an app restart instead of only living in memory.
- Thread history browsing for those same providers: the sidebar's thread list and search now read from the saved conversation files (title/preview derived from the first user message and last message), so old local conversations can be reopened instead of being silently orphaned once a new thread starts.
- Global quick-capture: select text in any app, press the hotkey, and CodexBar sends the selection straight to the AI. A clipboard-sequence guard ensures an empty selection never re-sends stale clipboard text, and the prior clipboard is restored.
- Template variables `{selection}` and `{clipboard}` expanded on insert, a template manager (create/edit/rename/delete), and prompt history recall (Up/Down in the input).
- Export/import of settings and templates for backup or sharing.
- "Report a bug" button that bundles diagnostics logs and an info summary into a single zip.
- Optional English UI: a Language toggle in Settings with live switching (no restart), covering the input bar, Settings, sidebar, output window, dialogs, and onboarding.
- Opt-in diagnostics logging to `%LOCALAPPDATA%\CodexBar\logs` with 7-day rotation, an unhandled-exception capture hook, and an "open logs folder" button.
- Reliability unit tests for `CodexProvider` via an injectable transport seam (timeout, JSON-RPC error, and disconnect/EOF) plus helper-level tests; CI now also runs the WPF UI smoke tests.
- Window-level, conversation-region, and per-thread accessibility names on the output and sidebar windows.

### Changed

- Providers can be switched live without restarting: the swap is blocked while a turn is active and rolls back to the previous provider if the new one fails to initialize. The saved Codex workspace is preserved across switches.
- Settings schema bumped to 8 (UI language, provider selection, prompt templates, diagnostics flag), with matching normalization.

### Removed

- Dead code: the unused `MarkdownRenderer.Build`/`CreateCodeBlock` FlowDocument path and the unused `FieldChipButton` style.

## 1.2.0 — 2026-07-01

### Added

- Live thread attention states in the sidebar and tray: working, waiting for approval, waiting for input, and error.
- Automatic app-server reconnect with active thread resume after a child-process crash.
- Drag-and-drop file and image attachments.
- Live reconnect smoke test that kills and restores the Codex app-server without using model quota.

### Changed

- Separate output window is created lazily only when requested.
- Reasoning and copy controls are keyboard-focusable buttons with screen-reader names.
- Sidebar, output, approval, and interactive forms expose accessibility names and cyclic tab navigation.

### Fixed

- Sidebar account/history operations recover after app-server disconnects.
- Session-scoped approvals preserve their exact Codex protocol decision and permission scope.

## 1.1.0 — 2026-07-01

### Added

- Native `request_user_input`, MCP structured-form, and MCP URL elicitation flows.
- Four-way approvals: once, session, decline, and decline plus cancel.
- Clipboard screenshot and file capture with `Ctrl+V`.
- Context usage indicator, high-usage warning, and one-click compact.
- Markdown conversation checkpoints with workspace and thread metadata.
- Assisted Codex CLI installation and login onboarding.
- Windows CI covering build and deterministic protocol smoke tests.

### Changed

- Conversation history is loaded lazily, capped to 200 in-memory messages, and rendered 20 messages per page.
- Hidden overlays release their Markdown visual tree while preserving conversation state.
- Tray tooltip and notifications expose current agent status and reopen the overlay when clicked.

### Fixed

- Interactive agent requests are no longer silently declined.
- MCP numeric fields validate input and preserve integer, number, boolean, and array types.
- Installer command scripts run correctly when their path contains spaces.
