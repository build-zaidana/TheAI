# Changelog

## 1.7.0 — 2026-11-09 · One family of pages, shutdown

### Changed — one family of pages

- **Every open page is 420 × 248** with one template: a label, one big number or title, the middle, a footer. System has a live sparkline and jumps to the reading at fault; Timer sets its length on a ruler; Clipboard shows the newest large with where it came from; Music has the song as a waveform; Today, Control Center and the agent follow.
- Weather (hourly temperatures as a line), Water, Reminders, the day's recap, "while you focused" and the selection actions follow the same template.
- Pages without a close button (System, Control Center, Today, Clipboard, Weather, Water, Reminders, Calendar, Jobs, Recap) now close when the pointer leaves or you click elsewhere; opened from a hotkey they close after 6 seconds.
- The brightness slider in Control Center works: its WMI calls failed after the first one.
- Starting the app no longer flashes the old bar: it stays unseen until the notch is up.
- Open pages no longer carry the round objects; those stay small on the compact pill.
- The small record on the pill turns 20 steps a second instead of redrawing the notch 60 times a second while a song plays.
- Approvals that look destructive are marked risky: declining is the main button and "for the session" is not offered.

### Added — turn the PC off later

- **Matikan** mode on the Timer page, or type `shutdown -s -t 3600`, `shutdown 30m`, `matikan 1 jam`; `shutdown 22:30`, `matikan jam 22`; `shutdown -a` calls it off. **Pakai jam** on the page picks the time of day on the ruler (past that time, it means tomorrow). It runs Windows' own `shutdown /s /t X`, which **closes open apps by force**, so the page says so. The time left shows on the compact pill, and the notch opens by itself a minute before. Anything under 30 seconds is raised to 30.

## 1.6.0 — 2026-10-22 · Today, water, lyrics

### Added — fewer pages, if you like

- **Two page modes.** Combined: weather, the next meeting, water and reminders share one **Today** page of short rows (a row opens the full page; `/today` opens it). Separate: a page per feature, as before. New users start combined; Settings → Appearance switches it.

### Added — prayer times, microphone and camera

- **Prayer times** (optional): the next prayer for your weather city, worked out on your PC with Kemenag's angles (Fajr 20°, Isha 18°, Shafi'i Asr, 2 minutes of ihtiyat), on the Today and weather pages, with a quiet note in the notch when it begins.
- **Microphone & camera indicator** (optional): a dot at the notch's edge while an app uses the microphone (orange) or the camera (green), and its name when it starts. It reads what Windows itself records for its privacy indicator.
- **`/mute`** mutes or unmutes the microphone; while the microphone is in use, the Today page has a Mute button too.

### Added — small things in the notch

- **Caps Lock and keyboard language** in the HUD (optional): the notch says when Caps Lock goes on or off and which language you switched to.
- **Internet lost / back online** joins the volume, brightness and battery HUD, from Windows' own network event.
- **Water reminder** (optional): a daily goal, +1 glass in one click, the last seven days, and a gentle nudge during your active hours until the goal is met. `/water` opens it.
- **Synced lyrics beside the player**: the line being sung is highlighted; click any line to jump there.
- **Idle companion** (optional): two small eyes in the compact notch that blink, and doze off after five minutes away.

### Changed — lighter at rest

- At rest TheAI now uses under 0.5% of one CPU core (from about 0.8%): the compact equalizer stands still, fullscreen apps are noticed through Windows events instead of a poll, and timers run only while their feature needs them.

### Changed — quicker and clearer

- The notch opens faster: it reacts to the pointer in 150 ms (from 280) and the page settles sooner.
- The first-start feature picker shows a picture of each feature in the notch as you go through the list.
- Settings: the water reminder has its own fields (goal, glass, minutes, hours) and the page mode has a switch.
- Uninstalling asks whether to delete your TheAI data too (kept unless you say yes).

### Fixed

- The lyric placeholder note and the "…" for cut-off text showed as garbled characters.

## 1.5.0 — 2026-10-21 · CodexBar is now TheAI

### Changed — a new name

- **CodexBar is now TheAI** (there's another app called CodexBar, and "Codex" is OpenAI's name). Upgrading keeps everything: settings, conversations, the shelf, timers and saved keys move over on the first start, and the installer replaces the old app.
- The interface follows Windows' language by default (Indonesian or English); either can still be chosen in Settings.
- Windows 10 (1809 and later) is supported again.

### Changed — only what you want

- **Every notch feature is optional.** A new user starts with the agent and the launcher; the first start shows a short tour and a feature picker (music, timer and the HUD suggested). Settings → Notch → Choose features changes it any time. Existing users keep what they had.
- A new **TheAI icon**: the notch at the top of a screen.
- The release also has a **portable zip** for running TheAI without installing it.

### Added — reminders, weather, selection

- **Reminders & to-do:** `/remind 15m stretch`, `/remind 14:30 call`, `/remind besok 9.00 standup`, `/todo buy milk`, or the page's own field. When one is due the notch opens on it (Snooze 10m / Done) and Windows shows a notification.
- **Weather & world clock:** today's weather for your city (Open-Meteo, no key) and up to four other time zones.
- **Select anything, ask about it:** the quick-capture hotkey now offers Translate / Explain / Summarize / Fix writing / Ask… for the selected text, with the answer in the notch. The older modes stay in Settings. New `/proofread` command.

### Added — command palette & smart commands

- **Type `/` in the launcher** to see what TheAI can do and run it from the keyboard: `/review` the uncommitted changes, `/explain`, `/summarize` or `/translate [language]` text or the clipboard, `/fix` the last failed command, `/test`, `/commit`, `/docs` — plus `/focus 25`, `/timer 10`, `/clipboard`, `/recap`, `/standup`, `/system`, `/new`, `/settings`, and your own templates as `/their-name`. ↑/↓ choose, Tab completes, Enter runs.
- **Search what was said** in past conversations, not only their titles, right from the launcher.

### Added — updates and crash reports

- **Update from inside the app**: "Update now" downloads the new installer, checks it against the release's SHA-256, installs it quietly and restarts TheAI.
- If TheAI ever closes unexpectedly, the next start offers to **report it** as a pre-filled GitHub issue you read before sending. Nothing is sent automatically.

### Changed — faster start

- The notch shows ~0.2 s sooner (precompiled code), and checking the sign-in no longer holds up the rest of startup.


### Added — live activities from scripts

- **Any script can show itself in the notch**: `TheAI.exe activity build --title "npm run build" --progress 0.4`, then `--done` or `--failed "exit 1"` (or a JSON line written to the user's named pipe `\\.\pipe\theai-<user>`). The compact notch shows a progress ring, the name and the percent (a spinner and the time when there's no percent), a check and the duration when it finishes, and the one red cross when it fails.
- The **activities page** (scroll the open notch) lists up to four jobs with bars or what they say they're doing; a row with a link opens it; a failed job has **Ask Claude/Codex**, which hands the failure to the agent in the current folder.
- Local only: no port, no network; the pipe accepts only the same Windows user's processes, links are http(s) only, and silent jobs expire after 10 minutes. Can be turned off in Settings → Notch.

### Added — long terminal commands, automatically

- **PowerShell commands that run 10 seconds or more show up in the notch by themselves** (Settings → Notch, adds one marked line to `$PROFILE`, a backup is kept, switching it off takes it out): the command and its time while it runs, then a check and the duration or a cross and the exit code; "Ask Claude" tells the agent which command failed and in which folder. Quick commands never appear. Prompt themes keep their `$?` and `$LASTEXITCODE`.

### Added — pull request CI

- **The current branch's PR checks in the notch** (Settings → Notch, on when the GitHub CLI is signed in): a ring with how many checks are done while CI runs, then a check — or a cross with the failed checks and "Ask Claude"; the row opens the PR's checks on GitHub. Read with `gh` (no token of CodexBar's own) in the folder of the last terminal command or the workspace, every 30 s while CI runs, every 5 min otherwise, and right after a `git push`. Runs that ended before CodexBar started aren't announced.

### Added — today's recap

- **A recap of the day** as a page of the open notch: focus sessions and time, what the agents finished, files they changed, long terminal commands (and how many failed), PR runs. It opens by itself once at 5 pm (Settings → Notch) and copies as a few lines of Markdown for a standup. Kept in a small local file per day, 30 days.

### Added — clipboard history

- **What you copied lately** as a page of the open notch (Settings → Notch): the last 30 texts and 5 images; click one to copy it again, drag it into an app or the prompt, right-click to pin it. Copies marked private (password managers, the Windows "keep out of history" flags) are skipped; only pinned texts are saved, the rest is forgotten when CodexBar quits.

### Added — downloads on the shelf

- **Finished downloads go on the file shelf for two hours** (Settings → Notch), with a short check and the file's name in the notch. The real Downloads folder is watched; partial files (.crdownload, .part…) are ignored and a file is taken only once it has stopped growing. Files put on the shelf by hand stay as before.

### Added — system monitor

- **The machine as a page of the open notch** (Settings → Notch): CPU and GPU the way Task Manager counts them, memory used of total, free space on the system drive — read only while the page is shown. A short HUD warning when memory stays at 90 % or more, or the drive drops under 5 % / 5 GB, then quiet for 10 minutes; a busy CPU or GPU is never a warning. No temperatures: Windows has no standard way to read them without admin rights or a vendor SDK.

### Added — agents × focus × calendar

- **Hold agents during focus and meetings** (Settings → Notch, on by default): while a pomodoro focus session runs or a calendar event is under way, an agent finishing or asking for permission doesn't open the notch, toast or chime — the compact notch shows a count. When the break starts or the meeting ends, everything that waited opens together ("While you focused"), with Answer / View for each.

### Added — Gemini CLI

- **Gemini CLI sessions in the notch** next to Claude Code (Settings → Claude Code): CodexBar adds a relay hook to `~/.gemini/settings.json` (removed again when switched off; a backup is kept) and shows each session's prompt, tool steps and answer. Gemini's permission prompts are shown but answered in its terminal.

### Changed — lighter

- **About a third of the memory**: idle private memory goes from ~430 MB to ~125 MB (working set ~610 → ~230 MB). The app is no longer a compressed single file (that unpacked every assembly into memory at each start; the installer compresses it now, and is smaller for it), the startup garbage is collected once a minute in, and listing Claude Code sessions skips their tool output as bytes instead of reading 100 MB transcripts into strings (1.9 s → 0.35 s).
- **No memory spike at launch**: the last Claude Code conversation's transcript is read when it's opened or continued, not at startup (it used to allocate ~270 MB in the first seconds); memory now stays around 110–125 MB from the start.

### Fixed

- A media player that reports no playback or timeline info yet (a browser tab still loading) no longer breaks the music page.

### Added — out of the way

- **Hide in fullscreen**: a game, a video or a presentation covering the notch's monitor hides the notch until it ends (other monitors keep it). On by default.

## 1.4.0 — 2026-10-10

### Added — music in the notch

- **Now playing** from any app in the Windows media controls (Spotify, browsers, Media Player…): album art and an equalizer in the compact notch, a player on hover with seek, previous / play / next and the app's own volume (Windows mixer).
- A short toast when the track changes (hover it for the whole player); scroll the open notch to switch between music and the agent.
- Optional synced lyrics from LRCLIB (off by default: sends the title and artist to lrclib.net).

### Added — the notch as the system's HUD

- **Volume & brightness**: the volume keys (or any change of the master volume) and the brightness keys widen the compact notch into an icon and a bar for a moment; mute shows a crossed-out speaker.
- **Battery & connections**: plugging the charger in shows a bolt, the percentage and a battery; at 10 % and 5 % the battery turns red (the only colour); headphones becoming the output show their battery (the lower earbud, read from the paired Bluetooth device) or, when they don't report it, their name.
- **File shelf**: dragging files over the notch offers two targets — attach to the prompt, or keep on the shelf; shelved files show as thumbnails, drag them out into any app, save them all to a folder, or clear the shelf; the compact notch shows a tray and how many.
- **Calendar** (off until chosen in Settings): Outlook (read locally) or a pasted iCal link (kept in Windows Credential Manager). Within half an hour of the next event the compact notch counts down to it, a minute before it opens on it, and the calendar page lists the rest of the day with a camera to join Meet / Teams / Zoom calls. Recurring events, time zones, moved and cancelled instances are handled.

### Added — timer in the notch

- **Pomodoro, timer, stopwatch and an alarm** kept by the notch itself, drawn after the Clock app's live activities: tinted round controls, thin orange digits, hour / minute / second wheels (scroll or drag) with a start button; the stopwatch shows hundredths; survives restarts.
- **AllTime's commands** typed on the Timer page, previewed before Enter: `25` `5m` `90s` `1h30` (timer, with an optional label), `+5` `-2` `+30s`, `@7:30` `@19:05 rapat` `@7pm` (alarm), `p` `tm` `sw` `a`, `p50/10/15`, `go` `skip` `lap` `stop` `zz` `x`; anything else becomes the label.
- When a countdown ends or the alarm rings the notch springs out and sends rings outward; the zero fades, the bell shakes; next step / snooze 9 min / close.
- Timer in neutral white and grey, digits and labels in Sora (SIL Open Font License, `Fonts/OFL.txt` ships with the app); icons instead of words (target = focus, cup = break, hourglass = timer, flag = lap); hour / minute / second wheels to pick a length; a countdown (pomodoro or timer), a stopwatch and an alarm can run at once, each as its own row; mode icons (Timer / Pomodoro / Stopwatch / Alarm); typing shows in the row with the result already in the number (`+5` → 29:12).
- The open notch has up to three pages — music, agent, timer — switched by scrolling; a Timer chip in the launcher.

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
