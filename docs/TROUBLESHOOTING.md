# CodexBar — Troubleshooting & FAQ

Panduan masalah umum untuk pengguna. Sebagian besar kondisi sudah ditangani otomatis
oleh onboarding di dalam aplikasi; dokumen ini untuk kasus yang perlu tindakan manual.

## Persyaratan
- Windows 11 (x64).
- **Codex CLI** terpasang dan sudah login. CodexBar adalah pengirim prompt ke Codex —
  bukan AI tersendiri.

```powershell
npm install -g @openai/codex
codex login
```

---

## "Codex CLI belum terpasang"
CodexBar tidak menemukan `codex.exe`. Jalankan perintah instalasi di atas, lalu klik
**Coba lagi** di panel onboarding. CodexBar mencari Codex di lokasi npm global, folder
program OpenAI, dan PATH. Untuk lokasi kustom, set env var:

```powershell
$env:CODEXBAR_CODEX_PATH = "C:\path\ke\codex.exe"
```

## "Belum login ke Codex"
Codex terpasang tapi belum ada akun aktif. Jalankan `codex login`, selesaikan login di
browser, lalu klik **Coba lagi**.

## "Codex tidak merespons … dalam 30 detik" (timeout)
Codex App Server tidak membalas. Coba: tutup CodexBar dari tray → pastikan `codex login`
masih valid → buka lagi. Jika berulang, jalankan `codex` sekali di terminal untuk
memastikan CLI sehat.

## "Koneksi Codex App Server terputus"
Proses Codex anak berhenti. CodexBar akan menyambung ulang pada permintaan berikutnya;
jika tidak pulih, tutup dari tray lalu buka kembali.

## Overlay tidak muncul saat menekan hotkey
1. **Hotkey bentrok** dengan aplikasi lain. Buka CodexBar dari **ikon tray** →
   **Settings** → ubah hotkey (klik field hotkey, tekan kombinasi baru, **Terapkan**).
2. Jika notifikasi *"Hotkey CodexBar tidak aktif"* muncul saat start, hotkey gagal
   didaftarkan — ganti lewat Settings seperti di atas.
3. Pastikan hanya satu instance berjalan (cek Task Manager: `CodexBar.App`).

## Windows SmartScreen: "Windows protected your PC" / "Unknown publisher"
Build belum ditandatangani secara digital. Untuk menjalankan: klik **More info** →
**Run anyway**. (Rilis bertanda tangan menghilangkan peringatan ini — lihat
`docs/RELEASE-CHECKLIST.md`.)

## Workspace tidak ditemukan
Folder workspace yang tersimpan sudah dipindah/dihapus. Klik chip direktori di bar untuk
memilih folder yang valid.

## Ikon tray "hantu" tidak hilang setelah keluar
Arahkan kursor ke area tray; Windows akan membersihkan ikon yang sudah mati. Ini perilaku
Windows, bukan bug aplikasi.

## Output tidak terlihat / ingin memunculkan percakapan
Klik tombol **percakapan** di bar. Bila mode "Output menyatu", panel chat akan muncul/
tersembunyi inline; bila "Output terpisah", window output ditampilkan/disembunyikan.

---

## Di mana data disimpan
- Pengaturan: `%LOCALAPPDATA%\CodexBar\settings.json`
- Thread/riwayat: dikelola oleh penyimpanan thread Codex lokal (sama dengan Codex Desktop).

## Reset pengaturan ke awal
Tutup CodexBar, hapus `%LOCALAPPDATA%\CodexBar\settings.json`, lalu buka lagi. (File rusak
akan dibuatkan cadangan otomatis dan diganti dengan default.)

## Privasi
Prompt diproses secara lokal oleh Codex CLI milikmu; CodexBar tidak mengirim data ke
server lain selain alur Codex yang sudah kamu login.
