# Changelog

## 1.3.0 — 2026-10-06

### Added — notch and chat

- **Notch / Dynamic Island** at the top of the screen replaces the bar: compact status, spring-animated expand, corner docking, drag as liquid; answers approvals and agent questions inside the notch, with one-click recommended answers.
- **Chat beside the notch**: grows out of the notch and folds back into it; always on top and out of Alt+Tab; widen or drag its edge (width remembered); Win+Alt+C toggles it; a chat button on the compact notch.
- **Conversation like Claude Code**: Markdown with tables, nested/task lists, syntax-coloured code; the agent's work folded into one line per run of tool calls ("Read 2 files, ran 4 commands +251 −0"), progress notes, an "Edited N files" summary with diffs and Undo; live while the agent works, for Claude Code and Codex.
- **Message actions**: copy, retry, edit, Fork from here, Pin as chapter (with a chapter list), Read aloud (Windows voices); edit/retry branch Claude Code sessions and show "‹ 1/2 ›" versions.
- **Composer**: framed box with ↵, + and mic below, model / effort / approval-mode pickers, context ring (hover for numbers, click for a card with Compact), slash commands, Ctrl+V images, file drop, ↑ recalls the last prompt; Ctrl+F finds in the conversation; export to Markdown.
- **Claude Code sync**: past sessions in Lanjutkan, live sessions in the notch through hooks, history read from the session file (latest branch, conversation order), a notice with "Continue here" when a session is open in Claude Code, optional auto-fork, Compact via `/compact`.
- Drop files and area screenshots onto the notch; files the agent changed with diffs; subscription quota meter; prompt queue; parallel agents; completion toasts with sound; next-prompt suggestions; offline voice input (whisper.cpp).
- Check for updates from a public GitHub release repository; `scripts\install.ps1` / `uninstall.ps1` to install without the installer.
- Auto-resume after usage limits (persisted across restart), scheduled prompts, regenerate / edit last / copy response actions, open in editor.

### Removed

- The bar display mode and its sidebar window; the notch and the chat replace them.

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
