# Project Map - MiniSSMS

Dokumen ini dipakai sebagai peta cepat saat agent diminta mengubah fitur di project ini.

## Ringkasan

MiniSSMS adalah aplikasi desktop WPF untuk SQL Server.

- Framework: `.NET 9`, `net9.0-windows`, WPF.
- Entry UI awal: `ConnectionWindow`, lalu `MainWindow`.
- SQL client library: `Microsoft.Data.SqlClient`.
- Query history lokal: `Microsoft.Data.Sqlite`, disimpan di `%LocalAppData%\MiniSSMS\Data\query-history.db`.
- Editor SQL: Single Shared WebView2 instance dengan Monaco Editor multi-model architecture (`createTabModel`, `switchTabModel`, `disposeTabModel`).
- File host editor: `Editor\sql_editor.html`; style host ada di `Editor\sql_editor.css`; state global & registry model ada di `Editor\sql_editor_state.js`; katalog keyword/function T-SQL ada di `Editor\sql_editor_catalog.js`; helper autocomplete/parser ada di `Editor\sql_editor_autocomplete.js`; provider completion & Redgate SQL Prompt snippets (`cp`, `ap`, `ct`, `at`, `cv`, `av`, `cf`, `af`, `dp`, `dv`, `dfn`, `dt`, dll dengan auto-fetch definisi script objek existing saat Enter) ada di `Editor\sql_editor_completion.js`; hover ada di `Editor\sql_editor_hover.js`; formatter ada di `Editor\sql_editor_formatter.js`; bridge C# multi-model (`createTabModel`, `switchTabModel`, `disposeTabModel`) ada di `Editor\sql_editor_bridge.js`; bootstrap Monaco ada di `Editor\sql_editor.js`; semuanya disalin ke output lewat `SSMS.csproj`.

## File Utama

| File | Isi / tanggung jawab |
| --- | --- |
| `SSMS.csproj` | Target framework, package WebView2 dan SqlClient, aturan copy file editor `sql_editor*` dan `appsettings.json`. |
| `Properties/PublishProfiles/FolderProfile.pubxml` | Publish profile Release ke folder lokal `D:\\Publish\\SSMS`. |
| `App.xaml` | Style global WPF: scrollbar, DataGrid, DataGrid cell/header, ContextMenu, MenuItem. |
| `App.xaml.cs` | Startup aplikasi. Biasanya membuka `ConnectionWindow`, lalu `MainWindow` jika koneksi sukses. |
| `Windows\ConnectionWindow.xaml`, `Windows\ConnectionWindow.xaml.cs` | Dialog koneksi SQL Server, build connection string, test/connect, dan simpan history koneksi ke `connection_settings.json`. |
| `Windows\MainWindow.xaml`, `Windows\MainWindow.xaml.cs` | Layout utama dan orkestrasi aplikasi: toolbar, Object Explorer, tab query, context menu, shortcut, open/save script, dan script object. Berisi single shared instance WebView2 (`SharedSqlEditorWebView`). |
| `Controls\QueryTabControl.xaml`, `Controls\QueryTabControl.xaml.cs` | Layout dan logic satu tab query: WebView2 editor, splitter, Results/Messages tab (dengan pesan berwarna & double-click error jump ke baris Monaco Editor), eksekusi query, result grid, dan cache autocomplete metadata. |
| `Services\DatabaseHelper.cs` | Semua akses SQL Server: metadata database/object, eksekusi query, generate script. |
| `Utilities\SqlBatchSplitter.cs` | Memecah script pada separator `GO`/`GO n` tanpa memecah `GO` di string atau comment; dipakai semua mode eksekusi. |
| `Models\ObjectExplorerNode.cs` | Model data `Tag` untuk node TreeView Object Explorer. |
| `Services\AppLogger.cs` | Logger file sederhana untuk error global dan event penting seperti create/close tab. Log tersimpan di `logs\minissms-YYYYMMDD.log` dalam output app. |
| `Models\AppSettings.cs` | Model serta load/save parameter aplikasi dari `appsettings.json`. |
| `Windows\SettingsWindow.xaml`, `Windows\SettingsWindow.xaml.cs` | Dialog Settings dari ikon gear di kanan toolbar; saat ini mengatur query command timeout. |
| `Windows\UnsavedChangesWindow.xaml`, `Windows\UnsavedChangesWindow.xaml.cs` | Dialog dark-mode custom untuk konfirmasi Save, Don't Save, atau Cancel saat menutup query yang masih berubah. |
| `Models\QueryHistoryEntry.cs` | Model satu record history eksekusi beserta properti display untuk grid. |
| `Services\QueryHistoryService.cs` | Inisialisasi schema SQLite, insert history, retention maksimum 10.000 record, dan pembacaan maksimum 300 record terbaru. |
| `Windows\QueryHistoryWindow.xaml`, `Windows\QueryHistoryWindow.xaml.cs` | Window dark-mode untuk melihat query execution history, filter rentang tanggal/database/isi SQL, detail query/message, Copy Query, refresh, double-click, dan Open in New Query. |
| `Windows\ObjectSearchWindow.xaml`, `Windows\ObjectSearchWindow.xaml.cs` | Pencarian table/view/routine/trigger/column lintas database yang dapat diakses pada satu server, lalu membuka SELECT atau definition. |
| `Windows\RenameObjectWindow.xaml`, `Windows\RenameObjectWindow.xaml.cs` | Dialog dark-mode custom untuk memasukkan nama baru objek/database dan menghasilkan script `sp_rename` atau `ALTER DATABASE` ke query tab baru. |
| `Editor\sql_editor.html` | Host ringan WebView2 untuk container Monaco, stylesheet, require.js, catalog, dan script editor. |
| `Editor\sql_editor.css` | Style host WebView2/Monaco container. |
| `Editor\sql_editor_state.js` | Konfigurasi require.js dan state global editor/metadata serta map registry `tabModels` per `tabId`. |
| `Editor\sql_editor_catalog.js` | Katalog keyword, data type, dan built-in function T-SQL untuk autocomplete. |
| `Editor\sql_editor_autocomplete.js` | Helper parsing statement aktif, alias/source query, lookup kolom, metadata lintas database, dan hover object/column info. |
| `Editor\sql_editor_completion.js` | Provider autocomplete Monaco untuk keyword, table, schema, routine, parameter, dan kolom. |
| `Editor\sql_editor_hover.js` | Provider hover Monaco dan command `View Schema / Definition`. |
| `Editor\sql_editor_formatter.js` | Formatter SQL sederhana untuk selection atau dokumen penuh. |
| `Editor\sql_editor_bridge.js` | Fungsi callable dari WPF seperti createTabModel, switchTabModel, disposeTabModel, get/set text, comment/uncomment, insert text, focus, dan update metadata. |
| `Editor\sql_editor.js` | Bootstrap Monaco: register provider, create editor, binding shortcut, dan event bridge ke WPF. |
| `Resources\SqlDark.xshd` | Syntax highlighting resource untuk SQL editor fallback/AvalonEdit. |
| `Assets/MiniSSMS.ico`, `Assets/MiniSSMS.png` | Icon aplikasi untuk executable dan window WPF. |

## Alur Aplikasi

1. `App.xaml.cs` membuka `ConnectionWindow`.
2. `ConnectionWindow` membuat connection string dari input user.
3. Setelah koneksi sukses, `MainWindow` dibuat dengan connection string awal.
4. `MainWindow.Window_Loaded` menginisialisasi single shared WebView2 (`InitializeSharedWebViewAsync`), memanggil `AddServerToExplorerAsync`, dan membuat tab query pertama.
5. Setiap tab query adalah instance `QueryTabControl` yang secara dinamis menempelkan single shared `SharedSqlEditorWebView` ke `EditorHostGrid` miliknya saat tab aktif.
6. Berpindah tab memanggil `switchTabModel(tabId)` di JS Monaco tanpa memuat ulang web page atau Chromium process baru.
7. Tombol Execute/F5 memanggil `QueryTabControl.ExecuteQuery()`.
8. Query dieksekusi oleh `DatabaseHelper.ExecuteQueryAsync()`, lalu hasil ditampilkan di DataGrid atau Messages.
9. Setiap query yang benar-benar dikirim ke SQL Server dicatat ke SQLite setelah selesai.
