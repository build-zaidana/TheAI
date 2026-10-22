# TheAI

**A notch for Windows** that keeps your AI agents, builds, music and day at the top of the screen.

[Download for Windows](https://github.com/build-zaidana/theai-app/releases/latest) · [Website](https://build-zaidana.github.io/theai-app/) · [Report a problem](https://github.com/build-zaidana/theai-app/issues/new/choose)

![The notch at the top of the screen, page by page](docs/assets/demo.gif)

Free · Windows 10 (1809+) and 11 · x64 · no account needed

## What it does

- **Agents in the notch.** Claude Code, Codex and Gemini CLI report what they're doing and ask for permission in the notch — including the sessions you run in the terminal, VS Code or the desktop apps.
- **Command palette.** Type `/` in the launcher: `/review`, `/explain`, `/fix`, `/test`, `/commit`, `/translate ja`, `/summarize`, `/proofread`, `/remind 15m stretch`, `/focus 25`, `/weather`… and your own templates.
- **Select anything, ask about it.** Select text in any app, press the quick-capture hotkey, and the notch offers to translate, explain, summarize or fix it — the answer comes back in the notch.
- **Builds, CI and scripts report themselves.** Long PowerShell commands, your pull request's checks (through your GitHub CLI) and anything a script sends with `TheAI.exe activity` show up as live activities. A failure goes to your agent in one click.
- **Focus first.** During a pomodoro or a meeting, agent results and permission requests wait and appear together at the break.
- **The rest of your day.** Reminders and to-dos, a water reminder, prayer times, weather and a world clock, timer and pomodoro, a calendar countdown with a join button, music with controls and synced lyrics you can click to jump to a line, clipboard history, a file shelf, today's recap for standup, and a system page.
- **Small things, in the notch.** Volume, brightness, battery, Caps Lock, keyboard language and the internet dropping, in place of Windows' pop-ups; a dot while an app uses your microphone or camera, and `/mute`; and, if you like, a pair of eyes that blink while nothing is going on and doze off while you're away.
- **Only what you want.** Everything except the agent and the launcher is optional: TheAI asks on first start, and Settings → Notch → Choose features changes it any time.
- **Out of the way.** Hides while a game or video is fullscreen; at rest under 0.5% of one CPU core and ~125 MB of memory, redrawing only when something changes.

## Install

1. Download `TheAI-Setup-<version>.exe` from [Releases](https://github.com/build-zaidana/theai-app/releases/latest) (or `TheAI-<version>-portable.zip` to run it without installing).
2. Run it. It installs for your user only — no administrator rights.
3. If Windows shows "Windows protected your PC", choose **More info → Run anyway**. The installer isn't code-signed yet; each release lists its SHA-256 so you can check the download:

   ```powershell
   Get-FileHash .\TheAI-Setup-1.6.0.exe -Algorithm SHA256
   ```

TheAI needs one AI to talk to: Claude Code or Codex (signed in with your subscription), or an API key for Anthropic, OpenAI or OpenRouter. It walks you through this on first start.

Updates: TheAI checks once a day and offers **Update now**; the new installer is verified against the release checksum before it runs.

Coming from **CodexBar**? Install TheAI over it — settings, conversations and saved keys move over on the first start.

## Privacy

No account, no analytics, nothing sent in the background.

| Talks to the network | Why |
|---|---|
| Your AI provider | Only what you ask, through the agent or API key you choose |
| GitHub (api.github.com), once a day | To see whether a new version is out |
| Optional, off until enabled | Weather and prayer times (open-meteo.com: your city's name, to find its coordinates; the prayer times themselves are worked out on your PC) · PR checks via your own GitHub CLI · synced lyrics (lrclib.net) · an iCal calendar link |

Everything else stays on your PC: settings, conversations and the daily recap in `%LOCALAPPDATA%\TheAI`; API keys and the calendar link in Windows Credential Manager; clipboard history in memory only.

## Reporting a problem

Use [Issues](https://github.com/build-zaidana/theai-app/issues/new/choose). If TheAI closes unexpectedly, the next start offers **Report** — it opens a pre-filled issue you can read before sending.

## Bahasa Indonesia

TheAI adalah notch untuk Windows: agent AI (Claude Code, Codex, Gemini CLI), build & CI, pengingat, cuaca, musik, timer, kalender, dan clipboard di puncak layar — kamu pilih sendiri fitur mana yang menyala. Gratis untuk Windows 10 (1809+) dan 11. Antarmukanya mengikuti bahasa Windows (Indonesia atau Inggris). Unduh di [Releases](https://github.com/build-zaidana/theai-app/releases/latest); kalau muncul "Windows protected your PC", pilih **More info → Run anyway** (installer belum bertanda tangan kode).

---

TheAI is free to use, with no account, ads or trial. If that ever changes it will be announced in the README and the release notes first, and copies already downloaded keep working. The source is not public. See [LICENSE](LICENSE.md). Not affiliated with Anthropic, OpenAI or Google.
