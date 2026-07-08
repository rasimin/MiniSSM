# Agent Role - MiniSSMS

Dokumen ini menjelaskan aturan kerja agent saat membantu di repo ini.

## Peran

Agent bertugas membantu membaca, mengubah, dan memverifikasi project MiniSSMS secara hati-hati. Fokus utama adalah membuat perubahan yang diminta user dengan scope kecil, konsisten dengan pola codebase, dan tidak merusak perubahan user lain.

## Aturan Git

Wajib:

- Jangan menjalankan `git commit` kecuali user secara eksplisit meminta commit.
- Jangan menjalankan `git push` kecuali user secara eksplisit meminta push.
- Jangan otomatis membuat branch, tag, commit amend, rebase, reset, checkout, atau revert kecuali user meminta jelas.
- Boleh menjalankan `git status` dan `git diff` untuk memahami kondisi working tree.
- Jika user meminta commit, tampilkan ringkasan perubahan yang akan di-commit dan pastikan hanya file relevan yang masuk.
- Jika user meminta push, lakukan setelah commit yang diminta sudah jelas.

Larangan keras:

- Jangan auto commit setelah selesai edit.
- Jangan auto push setelah build/test berhasil.
- Jangan pakai `git reset --hard` atau `git checkout -- <file>` untuk membatalkan perubahan tanpa instruksi eksplisit dari user.
- Jangan menghapus atau menimpa perubahan user yang tidak berkaitan.

## Cara Kerja

1. Baca konteks lokal lebih dulu sebelum mengedit.
2. Cari file dengan `rg` atau `rg --files` jika tersedia.
3. Untuk perubahan code manual, gunakan patch kecil dan terarah.
4. Ikuti pola existing di project.
5. Jalankan verifikasi yang masuk akal, minimal `dotnet build` jika perubahan C#/XAML.
6. Jika build gagal karena aplikasi sedang berjalan dan mengunci output, gunakan `dotnet build -o .\obj\verify-build`.
7. Setelah perubahan, cek apakah `.agents/PROJECT_MAP.md` atau `.agents/AGENT_ROLE.md` perlu ikut diperbarui.
8. Laporkan hasil dengan singkat: file yang berubah, verifikasi, dan blocker jika ada.

## Aturan Dokumentasi Agent

- Setiap perubahan project yang mengubah struktur file, alur fitur, lokasi logic, dependency, build/verifikasi, atau aturan kerja harus ikut memperbarui `.agents/PROJECT_MAP.md` dan/atau `.agents/AGENT_ROLE.md`.
- Jika user meminta fitur baru, bugfix, atau perubahan UI yang menambah/memindahkan logic penting, update `PROJECT_MAP.md`.
- Jika user memberi aturan kerja baru untuk agent, update `AGENT_ROLE.md`.
- Jika perubahan kecil tidak berdampak ke peta project atau role, boleh tidak mengubah file `.agents`, tetapi agent harus sudah mengeceknya sebelum menjawab final.

## Batasan Perubahan

- Jangan melakukan refactor besar kalau user hanya minta perubahan kecil.
- Jangan mengubah format seluruh file hanya karena edit kecil.
- Jangan mengubah dependency/package kecuali diperlukan untuk permintaan user.
- Jangan mengubah connection string, credential, atau file konfigurasi user tanpa instruksi.
- Jangan menutup/kill proses aplikasi tanpa izin user.

## Kebiasaan Saat Edit

- Untuk Object Explorer, cek `MainWindow.xaml.cs`, `ObjectExplorerNode.cs`, dan `DatabaseHelper.cs`.
- Untuk layout/show-hide Object Explorer, cek named columns/panel di `MainWindow.xaml` dan `ToggleObjectExplorer()` di `MainWindow.xaml.cs`.
- Untuk perilaku tab query satu baris atau default isi editor baru, cek `MainWindow.xaml` dan `sql_editor.html`.
- Untuk drag/reorder tab query, pastikan hanya header panel bertag `QueryTabDragHandle` yang bisa memulai drag di `MainWindow.xaml.cs`.
- Jangan reorder `TabQueryControls.Items` berulang saat mouse move; lakukan commit reorder tab sekali saat mouse-up agar UI tidak patah-patah.
- Untuk ukuran scrollbar, jaga standar 6px di `App.xaml`, `MainWindow.xaml`, dan `sql_editor.html`.
- Untuk splitter editor/results, cek `QueryTabControl.xaml` dan handler `EditorResultsSplitter_DragDelta` / `ResultsPane_PreviewMouse*` di `QueryTabControl.xaml.cs`; splitter harus resize row editor dan results, bukan menggeser header tab.
- Untuk editor SQL/Monaco, cek `sql_editor.html` dan `QueryTabControl.xaml.cs`.
- Untuk logging/error auto close, cek `AppLogger.cs`, global handlers di `App.xaml.cs`, dan event create/close tab di `MainWindow.xaml.cs`.
- Untuk UI utama, cek `MainWindow.xaml`.
- Untuk style global, cek `App.xaml`.
- Untuk dialog koneksi, cek `ConnectionWindow.xaml` dan `ConnectionWindow.xaml.cs`.

## Bahasa Respons

Gunakan Bahasa Indonesia yang ringkas dan jelas jika user memakai Bahasa Indonesia. Jelaskan perubahan dengan bahasa praktis, bukan teori panjang.
