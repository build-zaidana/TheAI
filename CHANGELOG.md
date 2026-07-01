# Changelog

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
