# Checklist rilis publik

- Build dan smoke test lulus tanpa warning/error.
- Versi di `CodexBar.App.csproj` sudah dinaikkan dan sesuai nama installer.
- `PRIVACY.md`, batasan, dan screenshot rilis sesuai perilaku versi ini.
- Publisher menetapkan URL dukungan, kanal laporan keamanan, dan pilihan lisensi sebelum source dipublikasikan.
- Exe dan installer ditandatangani dengan sertifikat code-signing yang sah; hasil `signtool verify /pa` lulus.
- Installer diuji pada Windows 11 x64 yang bersih, termasuk install, upgrade, startup task, dan uninstall.
- Uji akun ChatGPT serta akun API-key billing dilakukan dengan workspace non-sensitif.
- Tidak ada secret, PFX, password, dump, atau settings pengguna di artefak rilis.
