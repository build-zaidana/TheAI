# CodexBar

CodexBar adalah companion Windows ringan untuk mengirim prompt ke Codex tanpa membuka jendela Codex penuh. MVP memakai login Codex/ChatGPT yang sudah aktif dan tidak meminta OpenAI API key.

CodexBar adalah aplikasi komunitas tidak resmi, bukan produk OpenAI dan tidak berafiliasi dengan atau didukung oleh OpenAI. Penggunaan Codex dan layanan OpenAI tetap mengikuti ketentuan serta kebijakan akun pengguna.

Antarmuka dibagi menjadi tiga floating window:

- **Input bar** untuk workspace, status, dan prompt.
- **Output window** untuk respons streaming dan riwayat percakapan aktif.
- **Codex menu** untuk status login, pencarian, percakapan baru, dan thread Codex terbaru.

Status akun dan riwayat dibaca langsung dari Codex App Server. Thread yang dibuat atau dilanjutkan di CodexBar memakai penyimpanan thread Codex lokal yang sama, sehingga dapat dibuka kembali di Codex Desktop. Ini bukan sinkronisasi dengan riwayat ChatGPT umum.

## Menjalankan build siap pakai

1. Pastikan Codex CLI terpasang dan sudah login:

   ```powershell
   npm install -g @openai/codex
   codex login
   ```

   Jika langkah ini terlewat, onboarding menyediakan tombol **Pasang otomatis** dan **Buka login**; perintah manual tetap tersedia sebagai fallback.

2. Pasang lewat installer (`CodexBar-Setup-x.y.z.exe`, lihat *Membangun installer*) atau jalankan langsung `artifacts\win-x64\CodexBar.App.exe`.
3. Tekan hotkey global (default `Win+Shift+C`) untuk menampilkan atau menyembunyikan overlay. Kombinasi dapat diubah dari Settings.

Build pada `artifacts\win-x64` bersifat self-contained: pengguna akhir tidak perlu memasang .NET Runtime. Codex CLI tetap harus terpasang dan login. Rilis saat ini mendukung Windows 11 x64.

Lihat [PRIVACY.md](PRIVACY.md) sebelum mendistribusikan atau memakai aplikasi pada workspace sensitif.

## Membangun installer

```powershell
.\scripts\build-installer.ps1
```

Script ini mem-publish build self-contained lalu mengompilasi installer per-user (tanpa hak admin) dengan [Inno Setup](https://jrsoftware.org/isdl.php) ke `artifacts\installer\CodexBar-Setup-x.y.z.exe`. Installer menambahkan shortcut Start Menu, opsi ikon desktop, dan opsi *Jalankan saat login*. Jika hanya membutuhkan exe tanpa Inno Setup, gunakan `-AllowPublishOnly`.

**Code signing.** Build belum ditandatangani, sehingga Windows SmartScreen menampilkan peringatan "Unknown publisher". Untuk distribusi, sediakan sertifikat code-signing lalu set env var sebelum build:

```powershell
$env:CODEXBAR_PFX = "C:\path\sertifikat.pfx"
$env:CODEXBAR_PFX_PASSWORD = "rahasia"
.\scripts\build-installer.ps1   # exe + installer akan ditandatangani via signtool
```

## Shortcut

| Shortcut | Aksi |
|---|---|
| Hotkey global (default `Win+Shift+C`) | Tampilkan/sembunyikan overlay; dapat diubah dari Settings |
| `Enter` | Kirim prompt |
| `Shift+Enter` | Baris baru |
| `Ctrl+N` | Thread baru |
| `Ctrl+O` | Buka thread di Codex Desktop |
| `Ctrl+.` | Batalkan turn |
| `Ctrl+Scroll`, `Ctrl++/-` | Zoom respons |
| `Ctrl+0` | Reset zoom |
| `Ctrl+Shift+T` | Toggle always-on-top |
| `Ctrl+Shift+W` | Toggle word wrap kode |
| `Ctrl+V` saat clipboard berisi gambar/file | Tambahkan screenshot atau file sebagai lampiran |
| Drag file/gambar ke CodexBar | Tambahkan sebagai lampiran prompt |

Gunakan tombol `☰` untuk membuka menu/riwayat. Tombol **template prompt** (ikon daftar) di input bar membuka daftar template untuk sisip cepat, atau menyimpan prompt saat ini sebagai template baru. Tombol output `▤` hanya muncul saat output disetel terpisah (lihat Tampilan). `Top` (always-on-top) dan `Wrap kode` kini ada di panel Settings, bukan di toolbar, agar input bar tetap minimalis.

## Tampilan

CodexBar tampil sebagai **launcher satu baris** yang mengambang tepat di atas taskbar (gaya command palette), muncul/menyembunyi dengan hotkey global yang dapat diatur. Kartu memakai permukaan solid dengan sudut membulat native dan kontrol modern (ikon Segoe Fluent, slider/checkbox/combobox kustom, tombol kirim ikon Enter di dalam field).

- Baris berisi: tombol menu, chip direktori (dengan indikator status), field input dengan placeholder, dan tombol Settings.
- **Field dan chip direktori saling menyesuaikan**: saat input dipakai/di-hover, field membesar dan chip direktori menyusut; saat chip direktori di-hover, ia melebar menampilkan path lebih lengkap.
- Tinggi window menyesuaikan isi (`SizeToContent`); saat ada output/Settings, window tumbuh ke atas sehingga tepi bawah tetap menempel di atas taskbar.
- Tema `System`/`Light`/`Dark` tersedia di panel Settings.

Di panel Settings tersedia opsi **Output menyatu** dan **Menu menyatu** untuk menentukan apakah output dan Codex menu tampil menyatu (docked) dengan input bar atau sebagai window terpisah, plus info versi dan catatan privasi. Tersedia juga toggle **Log diagnostik** (opt-in, default mati) beserta tombol **Buka folder log** — bila diaktifkan, CodexBar menulis log ke `%LOCALAPPDATA%\CodexBar\logs` (rotasi 7 hari) untuk memudahkan pelaporan bug.

## Build

Jalankan:

```powershell
.\scripts\publish.ps1
```

## Batasan saat ini

- Form khusus MCP `openai/form` belum ditampilkan; gunakan **Buka di Codex** untuk flow tersebut. Form standar dan URL elicitation sudah didukung.
- Thread dapat dibaca dan dilanjutkan, tetapi penghapusan/arsip thread dilakukan lewat Codex Desktop/CLI.

## Arsitektur provider

- `CodexBar.Providers` berisi kontrak provider dan event netral.
- `CodexBar.Providers.Codex` adalah satu-satunya implementasi MVP.
- `settings.json` menyimpan `provider: "codex"`, tetapi tidak memiliki field API key.
- Provider berbasis API key dapat ditambahkan nanti melalui adapter baru dan Windows Credential Manager tanpa mengubah core/UI.

Lihat [DPPL](../DPPL-CodexBar.md) untuk rancangan lengkap.

## Progress terbaru

- **Template prompt**: simpan dan sisip cepat prompt yang sering dipakai lewat tombol template di input bar; instal baru di-seed dengan beberapa contoh (Jelaskan kode, Perbaiki bug, Tulis test, Review).
- **Diagnostics logging opt-in**: toggle di Settings menulis log rotasi harian (7 hari) ke `%LOCALAPPDATA%\CodexBar\logs`, plus penangkap unhandled exception, untuk mempermudah pelaporan bug. Default mati.
- **Aksesibilitas**: `AutomationProperties` lengkap di input bar serta window Output dan Sidebar (nama window, region percakapan, dan tiap item thread) untuk screen reader.
- **Fix proses background saat keluar dari tray.** Dulu `CodexBar.App.exe` (dan proses `codex` anak) tetap berjalan/menggantung setelah klik "Keluar". Penyebab: deadlock saat shutdown (`OnExit` memblokir UI thread + `DisposeAsync` menunggu read loop yang tidak pernah selesai). Kini dispose dijalankan di thread pool dengan batas waktu, dan penutupan child process dijamin. **Terverifikasi**: app + child `codex` benar-benar berhenti tanpa orphan.
- **Output bisa diseleksi & berformat.** Respons kini dirender sebagai Markdown (heading, bold, italic, inline code, list, blockquote, blok kode) di dalam `RichTextBox` read-only, sehingga teks dapat diblok dan disalin.
- **Setting docked/separated** untuk Output dan Codex menu (default menyatu).
- **Input bar diminimaliskan**: tombol Copy/Top/Wrap dipindah ke panel Settings.
- **Launcher satu baris di atas taskbar** dengan ikon modern (Segoe Fluent), tombol kirim ikon Enter di dalam field, dan field/direktori yang saling menyesuaikan saat hover/fokus.
- **Onboarding first-run**: bila Codex belum terpasang atau belum login, tampil panduan dengan perintah siap-salin dan tombol *Coba lagi* (bukan sekadar pesan error).
- **Interaksi agent lengkap**: `request_user_input` dan MCP form elicitation tampil sebagai form native, termasuk pilihan tunggal/jamak, input rahasia, validasi wajib, dan auto-resolution.
- **Approval actionable**: pilih izinkan sekali, izinkan selama sesi, tolak, atau tolak sekaligus hentikan turn; notifikasi tray dapat diklik untuk kembali ke permintaan.
- **Context capture cepat**: paste screenshot atau file langsung dari clipboard dengan `Ctrl+V`.
- **Agent attention center**: sidebar dan tray membedakan thread yang bekerja, butuh izin, butuh jawaban, atau error.
- **Recovery otomatis**: jika Codex App Server berhenti, koneksi dibuat ulang dan thread aktif dilanjutkan sebelum operasi berikutnya.
- **Input file fleksibel**: file/gambar dapat di-paste atau di-drag langsung ke bar.
- **Thread panjang tetap ringan**: visual percakapan dimuat 20 pesan per halaman; pesan lama dibuka bertahap.
- **Context guard**: indikator `Ctx %` tampil di bar, memberi warning saat hampir penuh, dan dapat diklik untuk compact.
- **Checkpoint lokal**: percakapan aktif dapat diekspor ke Markdown lengkap dengan workspace dan thread ID.
- **Startup ringan**: history tidak dibaca sampai percakapan dibuka; UI merender maksimal 20 pesan per halaman dan melepas visual tree saat overlay tersembunyi.
- **Window terpisah lazy**: OutputWindow tidak dialokasikan pada startup kecuali mode output terpisah benar-benar digunakan.
- **Branding & distribusi**: ikon aplikasi/tray/window kustom, info versi + catatan privasi di Settings, exe self-contained dikompresi (~72 MB), serta installer per-user (`scripts\build-installer.ps1`) dengan dukungan code signing opsional.
- **Perapian repo**: output build (`bin`/`obj`) dan `artifacts` dibersihkan; build ulang via `scripts\publish.ps1`.
