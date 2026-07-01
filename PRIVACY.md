# Privasi CodexBar

CodexBar adalah aplikasi komunitas tidak resmi. Aplikasi ini tidak menambahkan analytics, iklan, atau server milik CodexBar.

## Data yang diproses

- Prompt yang Anda ketik, path workspace, dan konteks file yang dipilih Codex diteruskan ke proses Codex CLI lokal.
- Codex CLI dapat membaca atau mengubah file workspace, menjalankan perintah, mengakses layanan OpenAI, dan memakai MCP server yang Anda konfigurasi. CodexBar meminta persetujuan untuk operasi yang dikirim App Server sebagai permintaan approval.
- Login, thread, retensi, dan pemrosesan layanan OpenAI dikelola oleh Codex CLI/OpenAI sesuai konfigurasi, akun, dan kebijakan yang berlaku.
- Jika akun memakai API key, penggunaan dapat ditagihkan sebagai penggunaan API. CodexBar menampilkan peringatan saat metode login tersebut terdeteksi.

## Data lokal

Settings CodexBar disimpan di `%LOCALAPPDATA%\CodexBar\settings.json`. File ini berisi preferensi tampilan, workspace terakhir, dan ID thread; file ini tidak menyimpan API key atau kata sandi.

Thread tidak disalin ke database CodexBar. Riwayat dibaca dari penyimpanan yang dikelola Codex CLI.

## Menghapus data

Uninstall aplikasi lalu hapus `%LOCALAPPDATA%\CodexBar` untuk menghapus settings CodexBar. Kelola atau hapus thread serta kredensial melalui Codex Desktop/CLI dan pengaturan akun OpenAI.

## Workspace sensitif

Jangan pilih folder yang berisi data yang tidak boleh diproses oleh Codex. Periksa ringkasan perintah, path, akses jaringan, dan izin tambahan sebelum menyetujui permintaan.
