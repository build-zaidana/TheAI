# CodexBar — Handoff untuk AI/Developer Lain

Salin seluruh dokumen ini sebagai konteks awal. Tujuannya: kamu langsung paham apa
itu CodexBar, di mana kodenya, status pengembangan, cara build/jalan, dan apa yang
belum selesai.

---

## 1. Apa ini
CodexBar = companion Windows ringan (WPF, .NET 10, win-x64) yang tampil sebagai
**launcher mengambang di atas taskbar** (gaya command palette) untuk mengirim prompt
ke **Codex** tanpa membuka Codex penuh. Hotkey global `Win+Shift+C`.

- **Bukan AI sendiri** — front-end tipis di atas **Codex CLI** (`codex app-server --stdio`,
  protokol JSON-RPC). Pengguna harus sudah `npm install -g @openai/codex` + `codex login`.
- Tanpa OpenAI API key (pakai login Codex/ChatGPT yang sudah ada).
- Bahasa UI: Indonesia.

## 2. Struktur direktori (yang perlu dicek)
```
CodexBar/
├─ src/
│  ├─ CodexBar.App/            ← UI WPF (PALING SERING DIUBAH)
│  │  ├─ MainWindow.xaml(.cs)  ← launcher bar + logika utama, onboarding, streaming
│  │  ├─ ConversationView.xaml(.cs) ← gelembung chat (user kanan / agent kiri)
│  │  ├─ OutputWindow.xaml(.cs)     ← window output terpisah (pakai ConversationView)
│  │  ├─ SidebarWindow.xaml(.cs)    ← menu/riwayat thread Codex
│  │  ├─ MarkdownRenderer.cs        ← markdown → FlowDocument (Build) & → panel (BuildPanel)
│  │  ├─ Theme.xaml                 ← style kontrol (tombol, slider, combobox, dll)
│  │  ├─ App.xaml(.cs)              ← bootstrap, shutdown handling
│  │  └─ codexbar.ico               ← ikon app (digenerate scripts/make-icon.ps1)
│  ├─ CodexBar.Core/           ← AppSettings, ConversationController (orkestrasi turn)
│  ├─ CodexBar.Providers/      ← kontrak provider netral (IAgentProvider, event, model)
│  ├─ CodexBar.Providers.Codex/← satu-satunya impl: CodexProvider + CodexExecutableLocator
│  ├─ CodexBar.Persistence/    ← JsonSettingsStore (settings.json)
│  └─ CodexBar.Windows/        ← interop Win32: WindowBackdrop, WindowPositioner,
│                                  GlobalHotKey, GlassEffects
├─ scripts/
│  ├─ publish.ps1              ← build self-contained single-file (lihat §4)
│  ├─ build-installer.ps1      ← publish → (opsional sign) → Inno Setup
│  └─ make-icon.ps1            ← generate codexbar.ico multi-resolusi
├─ installer/CodexBar.iss      ← skrip Inno Setup (installer per-user)
├─ artifacts/                  ← output build (gitignored)
│  ├─ win-x64/                 ← hasil publish.ps1
│  └─ win-x64-opaque-card/     ← VARIAN AKTIF yang dites user; exe disalin ke sini
├─ README.md
└─ HANDOFF.md (file ini)
```

## 3. Status pengembangan saat ini (per 2026-06-30)
Desain sudah berevolusi: **glassmorphism → opaque card → launcher satu baris + chat bubble**.
Kondisi sekarang (semua SUDAH di kode, build hijau):

- **Launcher satu baris** di atas taskbar: `[grip] [☰ menu] [chip direktori] [field input + tombol Enter di dalam] [▢ percakapan] [⚙ settings]`. Ikon modern via font `Segoe Fluent Icons`. Tombol kirim = panah Enter vektor kecil DI DALAM field.
- **Tinggi auto-fit** (`SizeToContent=Height`); tepi bawah tetap menempel di atas taskbar (tumbuh ke atas via `Window_SizeChanged`: `Top -= deltaHeight`).
- **Field ↔ direktori dinamis**: hover/fokus input → field membesar, chip direktori menyusut (animasi MaxWidth); hover chip → melebar. Lihat `UpdateDirWidth()`.
- **Placeholder** "Tanya Codex apa saja…" + **path direktori ringkas** (`FormatWorkspaceDisplay`) + StatusDot warna (hijau siap/kuning sibuk/merah error).
- **Chat bubble**: output dirender sebagai gelembung — pesan user kanan (biru), agent kiri (netral), via `ConversationView` + `MarkdownRenderer.BuildPanel`. Streaming mengisi bubble agent real-time. Berlaku inline maupun OutputWindow.
- **Onboarding first-run** (`StartupAsync` di MainWindow): deteksi Codex belum terpasang (`FileNotFoundException`) / belum login (`GetAccountAsync().IsAuthenticated==false`) → panel panduan + perintah siap-salin + tombol Coba lagi.
- **Branding**: ikon app/tray/window kustom. **Versi** v1.0.0 + catatan privasi di Settings.
- **Distribusi**: exe self-contained dikompresi (~72 MB, dari 173 MB) via `EnableCompressionInSingleFile`; installer per-user (Inno Setup) + dukungan code-signing opsional (`CODEXBAR_PFX`).

### Fitur terbaru (lanjutan, 2026-06-30 malam)
- **Chat bubble + tombol Salin**: tiap gelembung jawaban agent punya "⧉ Salin" (menyalin markdown pesan itu) — ConversationView.BuildAgentContent. CATATAN: teks berformat (TextBlock) TIDAK bisa diseleksi-drag (keterbatasan WPF); copy button + "Salin percakapan" menutup kebutuhan copy.
- **Thinking / reasoning**: menangkap `item/reasoning/textDelta` & `item/reasoning/summaryTextDelta` → `ProviderReasoningDeltaEvent` → bagian "Berpikir" collapsible di gelembung (toggle ▾/▸, update in-place saat streaming). Toggle Settings "Tampilkan thinking" (`AppSettings.ShowReasoning`). Hanya untuk turn live (history tak memuat reasoning).
- **Setting posisi output** (`AppSettings.OutputPosition` below/above): `ApplyOutputPosition()` menukar Grid.Row input↔output.
- **Editor hotkey** di Settings (HotkeyTextBox + ApplyHotkeyButton, rollback bila konflik) — `GlobalHotKey.ChangeGesture/NormalizeGesture`.
- **5 fitur Codex (v8)** — kontrak `IAgentProvider` diperluas:
  - `StartOrResumeThreadAsync(workspacePath, threadId, sandboxMode, approvalPolicy, ct)`
  - `SendPromptAsync(threadId, prompt, attachments, model, reasoningEffort, ct)`
  - `SearchThreadsAsync(term, limit, ct)` · `CompactAsync(threadId, ct)`
  - (a) **Token usage**: notif `thread/tokenUsage/updated` → `ProviderTokenUsageEvent` → `TokenUsageText` di Settings.
  - (b) **Approval policy** combo (untrusted/on-failure/on-request/never, `AppSettings.ApprovalPolicy`).
  - (c) **Search riwayat** server-side `thread/search` di SidebarWindow (debounce 280ms).
  - (d) **Reasoning effort** combo (default/minimal/low/medium/high, `AppSettings.ReasoningEffort`) → `effort` di turn/start.
  - (e) **Compact konteks**: tombol "Padatkan konteks" → `thread/compact/start`.
- **Permission/sandbox** combo pakai nama Codex resmi: `read-only`/`workspace-write`/`danger-full-access`.
- Bug fix: `IsMissingThread` cocok "rollout" (thread lama); `ReadThreadAsync` tangani "not materialized" (thread baru kosong) → return [].

> ⚠️ GOTCHA kontrak: bila mengubah signature `IAgentProvider`, WAJIB update `FakeProvider` + call-site di `tests/CodexBar.SmokeTest/Program.cs` (kalau tidak, build merah). `SchemaVersion` saat ini **5** (AppSettings + JsonSettingsStore + assertion test harus sinkron). Build & test SELALU hijau di commit terakhir (`dotnet build` 0/0, smoke test PASS).

## 4. Build & jalan (CATATAN PENTING — mesin spesifik)
- `dotnet` TIDAK di PATH; ada di `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (publish.ps1 sudah menanganinya).
- Build **Debug framework-dependent GAGAL** di mesin ini (butuh .NET Desktop Runtime yang tak terpasang). HARUS pakai **self-contained**:
  ```powershell
  .\scripts\publish.ps1            # → artifacts\win-x64\CodexBar.App.exe
  ```
- Varian yang dites user ada di `artifacts\win-x64-opaque-card\`. Alur biasa: jalankan publish.ps1, lalu salin `CodexBar.App.exe` (+ *.pdb) ke folder opaque-card, baru jalankan.
- Sebelum menyalin exe, hentikan instance lama: `Get-Process CodexBar.App | Stop-Process -Force` (kalau tidak, file terkunci).
- Settings runtime: `%LOCALAPPDATA%\CodexBar\settings.json`. Codex CLI standalone: `%APPDATA%\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe`.

## 5. KETERBATASAN VERIFIKASI (penting)
Tooling computer-use **tidak bisa screenshot CodexBar** di mesin ini: overlay borderless,
`ShowInTaskbar=False`, dan resolver akses hanya mengenali aplikasi "Codex" (Codex Desktop),
menolak "CodexBar". Jadi **verifikasi visual harus dilakukan user** (tekan `Win+Shift+C`).
Jangan buang waktu mengulang request_access untuk CodexBar.

## 6. Status audit publik terbaru

Sudah diselesaikan: timeout JSON-RPC, deteksi disconnect/re-init, refresh login, error serta editor hotkey dengan rollback konflik, tombol Stop, validasi workspace, pemulihan riwayat, state turn/workspace atomik, approval izin tambahan, permintaan approval kedaluwarsa, normalisasi settings rusak, startup error handling, multi-monitor work area, dokumen privasi, serta build/test otomatis.

Yang masih terbuka:

- UI belum mendukung form MCP dan `request_user_input`; request tersebut ditolak secara aman dan batasan didokumentasikan.
- Rilis hanya ditargetkan ke Windows 11 x64 dan belum memiliki auto-update.
- Mesin saat ini tidak memiliki Inno Setup, sertifikat code-signing, atau signing identity. Exe publish tersedia tetapi belum layak disebut rilis bertanda tangan.
- Publisher masih harus menetapkan URL dukungan, kanal laporan keamanan, dan pilihan lisensi sebelum source dipublikasikan.
- Uji visual, installer, upgrade, dan uninstall pada VM Windows bersih tetap diperlukan.

**Antrean "standar profesional" (belum dikerjakan, build sudah hijau jadi siap dikerjakan):**
- **Unit test reliability `CodexProvider`** (timeout/disconnect/RPC-error/missing-thread/not-materialized) — bagian paling kritis & belum ada test. Test saat ini hanya `tests/CodexBar.SmokeTest` (controller + settings).
- **Aksesibilitas** `OutputWindow.xaml` & `SidebarWindow.xaml` (AutomationProperties baru ada di MainWindow).
- **Dead code**: `MarkdownRenderer.Build` (FlowDocument) tak terpakai sejak bubble; style `FieldChipButton` tak terpakai.
- **CI** (.github/workflows) build + smoke test otomatis. Belum ada.
- **Logging/diagnostik** opt-in (kegagalan hanya muncul di UI).
- **#7 multi-monitor**: `WindowPositioner.PlaceAboveTaskbar` masih pakai monitor kursor (`GetWorkArea` window-based sudah ada tapi tak dipakai di placement). By-design-ish.
- **Seleksi-drag teks jawaban**: tidak ada (WPF TextBlock). Opsi: read-only TextBox plain (kehilangan format).
- Fitur Codex lain yg belum: voice/realtime, MCP/skills/plugins, file browser, background terminals, thread fork/archive/delete/rename, remote control.

**Fitur baru perlu live test (akun user kena usage limit Codex saat dikerjakan):** token usage, reasoning display, approval/effort, search, compact — UI sudah tampil; perilaku live perlu dicek user setelah kuota pulih.

### Riwayat audit awal (status mutakhir ada pada daftar di atas)
Kritis (keandalan — bikin app terasa "rusak"):
1. **Tidak ada timeout** pada `SendRequestAsync` (CodexProvider.cs) → jika app-server hang,
   UI "Menghubungkan Codex…" selamanya. Tambahkan timeout + pesan recovery.
2. **Tidak ada reconnect** setelah app-server putus (read-loop emit "Koneksi terputus",
   `IsInitialized` tetap true, guard di `InitializeAsync` mencegah re-init). Butuh deteksi
   disconnect + re-initialize otomatis.
3. **Kegagalan registrasi hotkey kini SENYAP** — `Window_SourceInitialized` menulis error ke
   `StatusText` yang sekarang `Collapsed`. Surface-kan (mis. tray balloon / onboarding).
4. **Onboarding retry** mungkin tak deteksi login baru: `GetAccountAsync` pakai
   `refreshToken:false`.

Lain:
5. Tidak ada tombol **Stop** untuk turn berjalan (hanya `Ctrl+.`).
6. Workspace path hilang → `DirectoryNotFoundException`, tanpa fallback.
7. Penempatan multi-monitor/DPI memakai monitor kursor (`WindowPositioner`).
8. Hotkey fixed, tak bisa diubah dari UI.
9. Respons terakhir tak dipulihkan saat buka ulang.

Butuh keputusan/biaya user (BUKAN coding):
- Sertifikat **code-signing** asli (beli) — tanpa ini SmartScreen memperingatkan.
- **Proposisi nilai** vs Codex langsung (fitur pembeda: template prompt, multi-provider,
  export, attachment).
- Auto-update (butuh infra hosting), i18n, aksesibilitas mendalam.

## 7. Konvensi & jebakan kode
- WinForms + WPF aktif berdampingan (`UseWindowsForms` untuk NotifyIcon). Banyak tipe
  AMBIGU → kualifikasi penuh: `System.Windows.Input.MouseEventArgs`,
  `System.Windows.Controls.UserControl`, `System.Windows.HorizontalAlignment`,
  `System.IO.FileNotFoundException`. File App pakai alias (`Path`, `Brushes`, `Drawing`, dll).
- `MainWindow.xaml.cs` menyimpan sumber percakapan di `List<ChatMessage> _messages` +
  `_streamingAgent`; render lewat `ActiveConversation` (inline atau OutputWindow).
- Build XAML: property element (mis. `Grid.ColumnDefinitions`) harus sebelum/sesudah konten,
  tidak di tengah.
