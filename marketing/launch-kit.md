# TheAI launch kit

Private notes for launching TheAI. Nothing here is published by a script.

## The one line

**Approve your AI agents from a notch on Windows.**

Music, lyrics, timers and reminders are already offered by other Windows notch apps (Yata Notch, Notchify, Isle Notch, Smart Notch, Oriel, as listed on AlternativeTo in November 2026). Agents that report and ask for permission in the notch, including sessions from the terminal, are the reason to choose TheAI. Lead with that; the rest is a bonus.

## Who it is for first

People who run Claude Code, Codex or Gemini CLI on Windows and leave them working while doing something else. They feel the problem daily: an agent sits waiting for a yes in a window they aren't looking at.

## Before posting

- [ ] Test the installer once on a clean Windows user account, including the SmartScreen step.
- [ ] Publish with `scripts\publish-release.ps1 -Confirm`, then open the site and click Download.
- [ ] Share the site link in a private chat and check that the preview image appears.
- [ ] Be free for the next 48 hours: first-day bug reports decide what people say about it.

## Where to post, in order

Check each community's current rules on self-promotion first. Post one a day so each can get a real answer.

1. **r/ClaudeAI**, then **r/codex** or **r/ChatGPTCoding**: closest to the audience. Use the vertical video or the GIF.
2. **Show HN**: link the site. Stay in the thread.
3. **r/Windows11**, **r/windowsapps**: lead with the notch itself.
4. **X, Threads, LinkedIn**: the vertical video with the short post below.
5. **Indonesian developer groups** (Telegram, Discord, Facebook): the Indonesian post and video.
6. **AlternativeTo**, then **Product Hunt** once the first bugs are fixed.
7. **Winget**: a manifest makes installing one command. Check its current rules on unsigned installers first.

## Posts

Say that you built it and that it isn't signed. Being upfront costs less than someone else pointing it out.

### Reddit, for agent users

**Title:** I got tired of Claude Code waiting for a yes in a window I wasn't looking at, so I built a notch for Windows

I run agents while doing other work, and they kept sitting idle waiting for permission. TheAI puts them in a notch at the top of the screen: what they're doing, when they finish, and Allow or Deny right there, including sessions already running in your terminal or VS Code. It works with Claude Code, Codex and Gemini CLI.

Requests that look destructive are marked as risky, and declining becomes the main button.

It's free, needs no account and has no analytics. The source isn't public. Windows 10 and 11 only. The installer isn't code-signed yet, so SmartScreen will warn you; each release lists its SHA-256.

I'd like to hear what breaks, and what your agents do that the notch doesn't show well.

### Show HN

**Title:** Show HN: TheAI, approve your AI coding agents from a notch on Windows

**Text:** TheAI is a WPF app that turns the top edge of Windows into a notch for Claude Code, Codex and Gemini CLI. Agents report progress and ask for permission there, using hooks, so terminal sessions show up too. It also does timers, music with synced lyrics, reminders and a system monitor, each optional. At rest it measured about 0.16% of one core and about 100 MB of private memory on my PC. Free, closed source, unsigned for now, no telemetry. I'm looking for bug reports and for what you'd want it to show.

### Short post, for the vertical video

Your AI agent is waiting for a yes in a window you aren't looking at. TheAI puts it in a notch on Windows. Free, no account. build-zaidana.github.io/theai-app

### Bahasa Indonesia

Agent AI sering nunggu izin di jendela yang lagi nggak kamu lihat. Jadi aku bikin TheAI: notch di puncak layar Windows buat Claude Code, Codex, dan Gemini CLI. Kelihatan lagi ngapain, kapan selesai, dan bisa Izinkan atau Tolak langsung dari situ. Ada juga timer, musik, pengingat, dan jadwal sholat, tinggal pilih yang mau.

Gratis, tanpa akun. Installernya belum bertanda tangan kode, jadi Windows bakal kasih peringatan: pilih More info, lalu Run anyway. Kalau ada yang rusak, kabari ya.

build-zaidana.github.io/theai-app

## Measuring without breaking the promise

The site and README promise no analytics, so don't add any. Use what's public:

- Downloads per release: `gh release view v1.7.0 --repo build-zaidana/theai-app --json assets`
- Issues and crash reports opened
- GitHub stars and replies to the posts

## Making money

Suggested order. Each step waits for the one before it to work. The thresholds are starting guesses, so change them freely.

1. **Now: free, for reach.** There's no evidence yet that people want it, and charging would only slow finding out.
2. **Around 500 downloads: buy a code-signing certificate.** Azure Artifact Signing, the cheap option, is only open to individuals in the USA and Canada, so an OV certificate from a traditional CA is the realistic route: Microsoft's docs put it at about $150 to $300 a year. It removes the biggest obstacle at download, though SmartScreen reputation still has to build up.
3. **Around 2,000 downloads with people coming back: tips.** A support link. GitHub Sponsors covers Indonesia but is aimed at open source contributors and pays out through Stripe, so confirm a closed-source app qualifies first. Otherwise use a local option.
4. **When people ask for more: Pro, on what's new.** A paid tier for the modular direction: themes, widgets, actions and scripts, plus team features such as shared approval rules. Whatever is free today stays free, as the site promises. Check Paddle's or Lemon Squeezy's terms for Indonesian sellers first; this wasn't verified.

Not recommended: ads, selling data, or a trial on current features. "No account, no ads, no trackers" is part of why anyone would trust an app that sits between them and their agents.
