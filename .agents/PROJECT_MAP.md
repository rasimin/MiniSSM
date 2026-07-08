# Project Map - MiniSSMS

Dokumen ini dipakai sebagai peta cepat saat agent diminta mengubah fitur di project ini.

## Ringkasan

MiniSSMS adalah aplikasi desktop WPF untuk SQL Server.

- Framework: `.NET 9`, `net9.0-windows`, WPF.
- Entry UI awal: `ConnectionWindow`, lalu `MainWindow`.
- SQL client library: `Microsoft.Data.SqlClient`.
- Editor SQL: Monaco Editor di dalam `Microsoft.Web.WebView2`.
- File HTML editor: `sql_editor.html`, disalin ke output lewat `SSMS.csproj`.

## File Utama

| File | Isi / tanggung jawab |
| --- | --- |
| `SSMS.csproj` | Target framework, package WebView2 dan SqlClient, aturan copy `sql_editor.html`. |
| `App.xaml` | Style global WPF: scrollbar, DataGrid, DataGrid cell/header, ContextMenu, MenuItem. |
| `App.xaml.cs` | Startup aplikasi. Biasanya membuka `ConnectionWindow`, lalu `MainWindow` jika koneksi sukses. |
| `ConnectionWindow.xaml` | UI dialog koneksi SQL Server. |
| `ConnectionWindow.xaml.cs` | Build connection string, test/connect, simpan history koneksi ke `connection_settings.json`. |
| `MainWindow.xaml` | Layout utama: toolbar, Object Explorer, tab query, status bar, style lokal toolbar/tree/tab. |
| `MainWindow.xaml.cs` | Orkestrasi aplikasi utama: tab query, Object Explorer, context menu, shortcut, open/save script, script object. |
| `QueryTabControl.xaml` | Layout satu tab query: WebView2 editor, splitter, Results/Messages tab, loading overlay. |
| `QueryTabControl.xaml.cs` | Inisialisasi WebView2/Monaco, eksekusi query, tampilkan result grid, cache autocomplete metadata. |
| `DatabaseHelper.cs` | Semua akses SQL Server: metadata database/object, eksekusi query, generate script. |
| `ObjectExplorerNode.cs` | Model data `Tag` untuk node TreeView Object Explorer. |
| `AppLogger.cs` | Logger file sederhana untuk error global dan event penting seperti create/close tab. Log tersimpan di `logs\minissms-YYYYMMDD.log` dalam output app. |
| `sql_editor.html` | Monaco SQL editor, command JavaScript, autocomplete, bridge message ke WPF. |

## Alur Aplikasi

1. `App.xaml.cs` membuka `ConnectionWindow`.
2. `ConnectionWindow` membuat connection string dari input user.
3. Setelah koneksi sukses, `MainWindow` dibuat dengan connection string awal.
4. `MainWindow.Window_Loaded` memanggil `AddServerToExplorerAsync` dan membuat tab query pertama.
5. Setiap tab query adalah instance `QueryTabControl`.
6. `QueryTabControl` memuat `sql_editor.html` ke WebView2, lalu update autocomplete dari metadata database.
7. Tombol Execute/F5 memanggil `QueryTabControl.ExecuteQuery()`.
8. Query dieksekusi oleh `DatabaseHelper.ExecuteQueryAsync()`, lalu hasil ditampilkan di DataGrid atau Messages.

## Object Explorer

Lokasi utama: `MainWindow.xaml.cs`.

- Root server dibuat di `AddServerToExplorerAsync`.
- Lazy loading tree ada di `TreeItem_Expanded`.
- Panel Object Explorer bisa di-hide/show lewat tombol toolbar `BtnToggleObjectExplorer` atau shortcut `F8`.
- Toggle panel memakai `ObjectExplorerColumn`, `ObjectExplorerSplitterColumn`, `ObjectExplorerPanel`, dan `ObjectExplorerSplitter` di `MainWindow.xaml`, dengan logic di `ToggleObjectExplorer()` pada `MainWindow.xaml.cs`.
- Node type disimpan di `TreeViewItem.Tag` sebagai `ObjectExplorerNode`.
- Struktur umum:
  - `Server`
  - `DatabasesFolder`
  - `Database`
  - `TablesFolder`, `ViewsFolder`, `SpsFolder`, `FuncsFolder`
  - `Table`, `View`, `StoredProcedure`, `Function`
  - `ColumnsFolder`, `IndexesFolder`, `TriggersFolder`
  - `Column`, `Index`, `Trigger`
- Folder filter ada di `_folderFilters`, `CreateFolderContextMenu`, `OpenFilterDialog`, `GetFolderHeader`.
- Context menu object ada di `CreateObjectContextMenu`.
- Script object dibuat lewat `ScriptObjectToQueryTabAsync`.

Saat menambah node baru:

1. Tambahkan `NodeType` baru di `ObjectExplorerNode` usage.
2. Tambahkan cabang lazy-load di `TreeItem_Expanded`.
3. Tambahkan method SQL metadata di `DatabaseHelper.cs` jika perlu.
4. Tambahkan context menu jika node perlu aksi.

## Query Tab dan Editor

Lokasi utama:

- WPF container: `QueryTabControl.xaml`.
- Logic tab: `QueryTabControl.xaml.cs`.
- Monaco editor: `sql_editor.html`.
- Tab management: `MainWindow.xaml.cs`.

Hal penting:

- `CreateNewQueryTab(...)` membuat tab baru di posisi paling kiri (`Insert(0, tabItem)`) lalu memilih tab baru.
- Tab query baru kosong secara default; default editor value ada di `sql_editor.html`.
- Header tab query memakai style `QueryTabsControl` di `MainWindow.xaml` agar tetap satu baris dengan horizontal scroll, bukan wrap bertingkat.
- Scrollbar standar project memakai ukuran 6px: `StandardScrollBar` di `App.xaml` dipakai eksplisit oleh template `ScrollViewer`, DataGrid, Object Explorer external `ScrollViewer`, header tab query, dan Monaco editor di `sql_editor.html`.
- Scrollbar horizontal harus memakai command `PageLeft/PageRight`; scrollbar vertical memakai `PageUp/PageDown`.
- Splitter editor/results memakai `EditorResultsSplitter` berbasis `Thumb` di `QueryTabControl.xaml`, plus resize fallback di 12px teratas `ResultsPane`; logic ada di `QueryTabControl.xaml.cs`.
- Drag tab query di `MainWindow.xaml.cs` hanya boleh mulai dari header panel bertag `QueryTabDragHandle`; saat drag tab hanya bergerak visual, reorder `Items` dilakukan sekali di mouse-up lewat `CommitTabDrag()` agar tidak patah-patah.
- Logging global dipasang di `App.xaml.cs` untuk UI exception, domain exception, dan unobserved task exception. `MainWindow.xaml.cs` juga log create/close query tab.
- `TabQueryControls_SelectionChanged` sync status server/database dan combo database.
- `BtnExecute_Click` memanggil `ExecuteQuery()` pada tab aktif.
- `Window_KeyDown` menangani shortcut seperti `F5`, `F8`, `Ctrl+N`, `Ctrl+S`, `Ctrl+O`, `Ctrl+K`, `Ctrl+Shift+K`.
- `RunEditorCommand(...)` menjalankan fungsi JavaScript di Monaco.
- `SaveActiveTabQuery()` dan `OpenSqlFile()` untuk file `.sql`.
- `QueryTabControl.CacheAndRefreshAutocompleteAsync()` mengambil metadata tabel/kolom dan memanggil `updateMetadata(...)` di editor.

Jika mengubah fitur editor seperti autocomplete, comment/uncomment, keyboard shortcut Monaco, atau get/set text, cek `sql_editor.html` dulu.

## DatabaseHelper

Lokasi: `DatabaseHelper.cs`.

Method penting:

- `BuildConnectionString`
- `TestConnectionAsync`
- `GetDatabasesAsync`
- `GetTablesAsync`
- `GetViewsAsync`
- `GetStoredProceduresAsync`
- `GetFunctionsAsync`
- `GetColumnsAsync`
- `GetIndexesAsync`
- `GetTriggersAsync`
- `ExecuteQueryAsync`
- `GetObjectDefinitionAsync`
- `GenerateTableCreateScriptAsync`

Pola yang dipakai:

- Bangun connection string per database dengan `BuildConnectionString`.
- Pakai `using` untuk `SqlConnection`, `SqlCommand`, dan reader.
- Metadata memakai query ke `sys.*`.
- Parameter object name memakai parameter SQL seperti `@TableFullName`.

## UI dan Style

- Style global ada di `App.xaml`.
- Style khusus window utama ada di `MainWindow.xaml`.
- Style connection dialog ada di `ConnectionWindow.xaml`.
- Style result grid mengikuti style global `DataGrid` di `App.xaml`.
- TreeView Object Explorer saat ini memakai header string langsung, termasuk icon emoji di `MainWindow.xaml.cs`.

Jika mengubah tampilan:

- Cek dulu style existing agar konsisten dengan dark theme.
- Jangan refactor besar style global kalau perubahan hanya untuk satu area kecil.
- Untuk icon Object Explorer, cari string `Header =` di `MainWindow.xaml.cs`.

## Area Perubahan Cepat

| Permintaan user | File yang dicek dulu |
| --- | --- |
| Ubah tree Object Explorer | `MainWindow.xaml.cs`, `ObjectExplorerNode.cs`, `DatabaseHelper.cs` |
| Ubah show/hide Object Explorer | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| Tambah metadata SQL object | `DatabaseHelper.cs`, lalu `MainWindow.xaml.cs` |
| Ubah toolbar atau shortcut | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| Ubah tab query | `MainWindow.xaml.cs`, `QueryTabControl.xaml`, `QueryTabControl.xaml.cs` |
| Ubah hasil query/grid | `QueryTabControl.xaml.cs`, `App.xaml` |
| Ubah Monaco editor | `sql_editor.html`, `QueryTabControl.xaml.cs` |
| Ubah connection dialog | `ConnectionWindow.xaml`, `ConnectionWindow.xaml.cs` |
| Ubah theme global | `App.xaml` |
| Ubah package/build | `SSMS.csproj` |

## Build dan Verifikasi

Perintah dasar:

```powershell
dotnet restore
dotnet build
```

Jika aplikasi `SSMS.exe` sedang berjalan, build normal bisa gagal karena file output terkunci. Untuk cek kompilasi tanpa menimpa output aktif:

```powershell
dotnet build -o .\obj\verify-build
```

Catatan: jangan kill proses aplikasi kecuali user meminta atau mengizinkan.

## Git / Working Tree

Repo ini bisa punya perubahan user yang belum di-commit. Sebelum edit besar:

```powershell
git status --short
```

Jangan revert perubahan lain yang tidak dibuat agent. Jika ada diff di file yang sama, baca dan lanjutkan dengan hati-hati.

## Dokumentasi Agent

Setiap kali ada perubahan project yang mengubah struktur file, alur fitur, lokasi logic, dependency, build/verifikasi, atau aturan kerja, agent wajib ikut memperbarui:

- `.agents/PROJECT_MAP.md`
- `.agents/AGENT_ROLE.md`

Jika perubahan sangat kecil dan tidak mengubah peta project atau aturan kerja, agent boleh tidak mengubah dokumen ini, tetapi harus tetap mempertimbangkannya sebelum final.
