# Changelog

## Unreleased

### Added

- Optional English UI: a Language toggle in Settings with live switching (no restart). The input bar, Settings panel, sidebar, output window, approval/interactive dialogs, and first-run onboarding are localized; remaining dynamic status messages migrate incrementally.
- Prompt templates: save and quick-insert reusable prompts from an input-bar button; fresh installs are seeded with example templates.
- Opt-in diagnostics logging to `%LOCALAPPDATA%\CodexBar\logs` with 7-day rotation, an unhandled-exception capture hook, a Settings toggle, and an "open logs folder" button.
- Helper-level reliability unit tests for `CodexProvider` (sandbox/approval normalization, missing-thread vs not-materialized classification, preview formatting, thread status/summary parsing).
- Window-level, conversation-region, and per-thread accessibility names on the output and sidebar windows.
- CI now also runs the WPF UI smoke tests on every push.

### Changed

- Settings schema bumped to 7 (diagnostics logging flag and prompt templates), with matching normalization.

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
