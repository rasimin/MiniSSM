# Project Map - MiniSSMS

Dokumen ini dipakai sebagai peta cepat saat agent diminta mengubah fitur di project ini.

## Ringkasan

MiniSSMS adalah aplikasi desktop WPF untuk SQL Server.

- Framework: `.NET 9`, `net9.0-windows`, WPF.
- Entry UI awal: `ConnectionWindow`, lalu `MainWindow`.
- SQL client library: `Microsoft.Data.SqlClient`.
- Query history lokal: `Microsoft.Data.Sqlite`, disimpan di `%LocalAppData%\MiniSSMS\Data\query-history.db`.
- Editor SQL: Monaco Editor di dalam `Microsoft.Web.WebView2`.
- File HTML editor: `sql_editor.html`, disalin ke output lewat `SSMS.csproj`.

## File Utama

| File | Isi / tanggung jawab |
| --- | --- |
| `SSMS.csproj` | Target framework, package WebView2 dan SqlClient, aturan copy `sql_editor.html`. |
| `Properties/PublishProfiles/FolderProfile.pubxml` | Publish profile Release ke folder lokal `D:\\Publish\\SSMS`. |
| `App.xaml` | Style global WPF: scrollbar, DataGrid, DataGrid cell/header, ContextMenu, MenuItem. |
| `App.xaml.cs` | Startup aplikasi. Biasanya membuka `ConnectionWindow`, lalu `MainWindow` jika koneksi sukses. |
| `ConnectionWindow.xaml` | UI dialog koneksi SQL Server. |
| `ConnectionWindow.xaml.cs` | Build connection string, test/connect, simpan history koneksi ke `connection_settings.json`. |
| `MainWindow.xaml` | Layout utama: toolbar, Object Explorer, tab query, status bar, style lokal toolbar/tree/tab. |
| `MainWindow.xaml.cs` | Orkestrasi aplikasi utama: tab query, Object Explorer, context menu, shortcut, open/save script, script object. |
| `QueryTabControl.xaml` | Layout satu tab query: WebView2 editor, splitter, Results/Messages tab, loading overlay. |
| `QueryTabControl.xaml.cs` | Inisialisasi WebView2/Monaco, eksekusi query, tampilkan result grid, cache autocomplete metadata. |
| `DatabaseHelper.cs` | Semua akses SQL Server: metadata database/object, eksekusi query, generate script. |
| `SqlBatchSplitter.cs` | Memecah script pada separator `GO`/`GO n` tanpa memecah `GO` di string atau comment; dipakai semua mode eksekusi. |
| `ObjectExplorerNode.cs` | Model data `Tag` untuk node TreeView Object Explorer. |
| `AppLogger.cs` | Logger file sederhana untuk error global dan event penting seperti create/close tab. Log tersimpan di `logs\minissms-YYYYMMDD.log` dalam output app. |
| `AppSettings.cs` | Model serta load/save parameter aplikasi dari `appsettings.json`. |
| `SettingsWindow.xaml`, `SettingsWindow.xaml.cs` | Dialog Settings dari ikon gear di kanan toolbar; saat ini mengatur query command timeout. |
| `UnsavedChangesWindow.xaml`, `UnsavedChangesWindow.xaml.cs` | Dialog dark-mode custom untuk konfirmasi Save, Don't Save, atau Cancel saat menutup query yang masih berubah. |
| `QueryHistoryEntry.cs` | Model satu record history eksekusi beserta properti display untuk grid. |
| `QueryHistoryService.cs` | Inisialisasi schema SQLite, insert history, retention maksimum 10.000 record, dan pembacaan maksimum 300 record terbaru. |
| `QueryHistoryWindow.xaml`, `QueryHistoryWindow.xaml.cs` | Window dark-mode untuk melihat query execution history, filter rentang tanggal/database/isi SQL, detail query/message, Copy Query, refresh, double-click, dan Open in New Query. |
| `ObjectSearchWindow.xaml`, `ObjectSearchWindow.xaml.cs` | Pencarian table/view/routine/trigger/column lintas database yang dapat diakses pada satu server, lalu membuka SELECT atau definition. |
| `sql_editor.html` | Monaco SQL editor, command JavaScript, autocomplete, bridge message ke WPF. |
| `Assets/MiniSSMS.ico`, `Assets/MiniSSMS.png` | Icon aplikasi untuk executable dan window WPF. |

## Alur Aplikasi

1. `App.xaml.cs` membuka `ConnectionWindow`.
2. `ConnectionWindow` membuat connection string dari input user.
3. Setelah koneksi sukses, `MainWindow` dibuat dengan connection string awal.
4. `MainWindow.Window_Loaded` memanggil `AddServerToExplorerAsync` dan membuat tab query pertama.
   Startup overlay tetap menutup workspace sampai Monaco pada tab pertama siap, dan background WebView2 dipaksa dark untuk mencegah blink putih saat transisi dari dialog koneksi.
5. Setiap tab query adalah instance `QueryTabControl`.
6. `QueryTabControl` memuat `sql_editor.html` ke WebView2, lalu update autocomplete dari metadata database.
7. Tombol Execute/F5 memanggil `QueryTabControl.ExecuteQuery()`.
8. Query dieksekusi oleh `DatabaseHelper.ExecuteQueryAsync()`, lalu hasil ditampilkan di DataGrid atau Messages.
9. Setiap query yang benar-benar dikirim ke SQL Server dicatat ke SQLite setelah selesai, termasuk server, database awal/akhir, waktu, durasi, status, message, rows affected, dan jumlah row hasil SELECT; kegagalan history tidak menggagalkan query utama.

## Object Explorer

Lokasi utama: `MainWindow.xaml.cs`.

- Root server dibuat di `AddServerToExplorerAsync`.
- Lazy loading tree ada di `TreeItem_Expanded`.
- Expand node menahan `RequestBringIntoView` agar posisi scroll Object Explorer tidak meloncat ke node yang dibuka.
- Panel Object Explorer bisa di-hide/show lewat tombol toolbar `BtnToggleObjectExplorer` atau shortcut `F8`.
- Toggle panel memakai `ObjectExplorerColumn`, `ObjectExplorerSplitterColumn`, `ObjectExplorerPanel`, dan `ObjectExplorerSplitter` di `MainWindow.xaml`, dengan logic di `ToggleObjectExplorer()` pada `MainWindow.xaml.cs`.
- Node type disimpan di `TreeViewItem.Tag` sebagai `ObjectExplorerNode`.
- Struktur umum:
  - `Server`
  - `DatabasesFolder`
  - `Database`
  - `TablesFolder`, `ViewsFolder`, `SpsFolder`, `FuncsFolder`
  - `ScalarFunctionsFolder`, `TableFunctionsFolder` di bawah `FuncsFolder`
  - `Table`, `View`, `StoredProcedure`, `Function`
  - `ColumnsFolder`, `IndexesFolder`, `TriggersFolder`
  - `Column`, `Index`, `Trigger`
- Folder filter ada di `_folderFilters`, `CreateFolderContextMenu`, `OpenFilterDialog`, `GetFolderHeader`.
- Folder Databases, Tables, Views, Stored Procedures, Scalar-valued Functions, dan Table-valued Functions menampilkan tombol filter saat header di-hover; filter Databases berlaku per koneksi server, sedangkan filter object berlaku per database.
- Context menu object ada di `CreateObjectContextMenu`.
- Tombol Refresh pada header Object Explorer dan context menu node/folder me-reload metadata node terpilih sekaligus menginvalidasi autocomplete tab terkait; shortcut `Ctrl+Shift+R`.
- Tombol Search pada header Object Explorer membuka pencarian object, schema, column, serta isi definition view/SP/function/trigger melalui `sys.sql_modules`, maksimum 1.000 hasil; hasil menampilkan lokasi dan cuplikan match. Window search memakai dark-mode ComboBox/button, menyediakan filter seluruh server aktif, filter semua/satu database, serta Cancel/Escape yang meneruskan cancellation ke proses load/query SQL.
- Window Object Search memakai layout master-detail: grid hasil di atas; selection memuat generated CREATE script untuk table atau module definition untuk view/SP/function/trigger di kiri bawah, sedangkan kanan bawah menampilkan server, database, schema, type, Object ID, create/modify date, dan informasi match.
- Semua node Object Explorer yang memiliki `ObjectExplorerNode` mendapat menu `Copy Name` secara otomatis saat diklik kanan; nama diambil dari `DetailName` atau identitas node/folder tanpa icon header.
- Context menu folder object dibuat oleh `CreateFolderContextMenu`; Tables, Views, Stored Procedures, dan kedua jenis Functions menyediakan template `Create New`.
- Context menu table menyediakan scripting CREATE, INSERT, UPDATE, DELETE, ALTER, dan DROP; template UPDATE/DELETE memakai variable `NULL` dan WHERE primary key sebagai default aman.
- Saat lazy-load metadata berjalan, node menampilkan teks titik bergerak lewat `CreateAnimatedLoadingItem`.
- Script object dibuat lewat `ScriptObjectToQueryTabAsync`.
- Node trigger di bawah table memiliki context menu `CREATE`, `ALTER`, dan `DROP` melalui alur scripting object yang sama.

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
- Scrollbar horizontal result grid menghitung viewport area kolom tanpa lebar row-number header, sehingga posisi paling kanan menampilkan kolom terakhir secara penuh.
- Lebar kolom result grid dapat disesuaikan tanpa ruang drag di tepi monitor: double-click divider menjalankan auto-fit, sedangkan context menu sel menyediakan `Auto Fit Column` dan `Widen Column (+200 px)`.
- Tab schema di area bawah Results memiliki context menu Close, Close All, dan Set Color yang hanya memengaruhi tab schema pada `TabResults`.
- Drag tab query di `MainWindow.xaml.cs` hanya boleh mulai dari header panel bertag `QueryTabDragHandle`; saat drag tab hanya bergerak visual, reorder `Items` dilakukan sekali di mouse-up lewat `CommitTabDrag()` agar tidak patah-patah.
- Logging global dipasang di `App.xaml.cs` untuk UI exception, domain exception, dan unobserved task exception. `MainWindow.xaml.cs` juga log create/close query tab.
- `TabQueryControls_SelectionChanged` sync status server/database dan combo database.
- Query tab melacak perubahan Monaco dengan indikator `*`; Save menghapus indikator, sedangkan close tab/close aplikasi memakai `UnsavedChangesWindow` dark-mode untuk konfirmasi Save/Don’t Save/Cancel pada tab yang masih dirty.
- Nama default dialog Save mengikuti judul tab tanpa indikator `*`, dan penyimpanan selalu mengambil seluruh isi editor meskipun ada selection.
- `BtnExecute_Click` memanggil `ExecuteQuery()` pada tab aktif.
- Query Tools menyediakan Parse (`Ctrl+F5`), Estimated Plan (`Ctrl+L`), Actual Plan (`Ctrl+Alt+L`), dan Format SQL (`Ctrl+Shift+F`).
- Execute/Parse/plan memakai `SqlBatchSplitter`; `GO` dan `GO n` tidak dikirim ke SQL Server, tetapi dipakai sebagai pemisah/repeat batch.
- Estimated/Actual execution plan ditampilkan sebagai tree operator (physical/logical operator, estimates/cost, actual rows bila tersedia) dan XML; XML dapat disimpan sebagai `.sqlplan` untuk tampilan graphical penuh di SSMS.
- Context menu setiap result grid dapat export result set asli ke CSV, tab-delimited TXT, JSON, atau XML; nilai export tidak memakai trimming khusus display grid.
- Setelah query sukses, database aktual pada koneksi dibaca kembali; perintah `USE <database>` menyinkronkan database tab, status, autocomplete, dan combo toolbar. Jika eksekusi gagal, context database tab tidak diubah.
- Ikon gear di pojok kanan toolbar membuka `SettingsWindow`; query timeout disimpan sebagai `Query.CommandTimeoutSeconds` di `appsettings.json` dan berlaku mulai eksekusi berikutnya (`0` berarti tanpa batas).
- Ikon jam `ToolbarQueryHistory` membuka satu instance `QueryHistoryWindow`; Open in New Query mencari koneksi server yang masih aktif dan membuat tab dirty baru pada database dari record history.
- Query History mengambil maksimum 300 record yang cocok langsung dari SQLite; rentang tanggal memakai hari lokal secara inklusif, sedangkan database dan isi SQL memakai pencarian substring literal.
- `Window_KeyDown` menangani shortcut seperti `F5`, `F8`, `Ctrl+N`, `Ctrl+S`, `Ctrl+O`, `Ctrl+K`, `Ctrl+Shift+K`.
- Urutan default toolbar mengikuti: Connect, Disconnect, Object Explorer, New Query, Save, Database, Execute, Comment, Uncomment, Save As, Open, Query History, Insert Script, lalu Query Tools; item tetap dapat di-drag untuk reorder.
- `MainWindow` meneruskan pesan native `WM_MOUSEHWHEEL` dari gesture dua jari touchpad ke `ScrollViewer` horizontal di bawah pointer.
- `RunEditorCommand(...)` menjalankan fungsi JavaScript di Monaco.
- Formatter Monaco memformat selection atau seluruh dokumen sebagai satu undo step dan menjaga string, quoted identifier, comment, serta separator `GO`.
- `SaveActiveTabQuery()` menyimpan ke path tab aktif, sedangkan Save As selalu meminta path baru; `OpenSqlFile()` dan drag-drop file `.sql` membuka setiap file sebagai tab baru. External drop WebView2 dimatikan agar drop di area Monaco tetap ditangani window.
- `QueryTabControl.CacheAndRefreshAutocompleteAsync()` mengirim payload metadata terpadu: tabel/view beserta kolom dan jenis object, stored procedure, scalar/table-valued function, parameter routine, daftar database, dan database aktif.
- Provider Monaco memfilter suggestion berdasarkan konteks: table/view/table-valued function setelah `FROM`/`JOIN`/`APPLY`, scalar function di expression, SP setelah `EXEC`/`EXECUTE`, dan parameter setelah routine dipilih.
- Autocomplete statis mencakup keyword, data type, serta built-in function T-SQL; function seperti `CAST`, `CONVERT`, dan `GETDATE` disisipkan sebagai snippet berparameter.
- Pada expression context seperti `SELECT`, `WHERE`, `GROUP BY`, dan `ORDER BY`, kolom dari table/view yang terbaca pada statement aktif mendapat ranking suggestion tertinggi, di atas function dan keyword; jika source memakai alias, insert text kolom otomatis memakai prefix alias seperti `a.ColumnName`, sementara detail suggestion tetap menampilkan table/source asal.
- Hover atau selection pada table/view/SP/function menampilkan aksi `View Schema / Definition in New Query`; definition diambil secara lazy hanya saat link diklik. Hover kolom menampilkan data type, nullable, identity, dan primary-key dari cache metadata.
- Autocomplete mengenali bracketed identifier, CTE, temporary table, dan kolom lokal yang dapat diinferensikan. Metadata lintas database dimuat on-demand melalui pesan `loadDatabaseMetadata` saat pola `Database.Schema.` diketik.
- Eksekusi DDL `CREATE`/`ALTER`/`DROP` untuk table, view, procedure, atau function menginvalidasi cache database aktif dan me-refresh autocomplete.
- Monaco mengirim pesan `editorReady` dan memasang binding eksplisit `Ctrl+Space`; metadata dikirim ulang setelah editor siap agar suggestion tidak kosong akibat race saat navigasi.

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
- `GetFunctionsAsync` (memisahkan scalar-valued dan table-valued)
- `GetColumnsAsync`
- `GetIndexesAsync`
- `GetTriggersAsync` (schema trigger diambil melalui `sys.objects`/`sys.schemas` karena `sys.triggers` tidak memiliki kolom `schema_id`)
- `ExecuteQueryAsync`
- `SearchObjectsAcrossDatabasesAsync`
- `GetObjectDefinitionAsync`
- `GenerateTableCreateScriptAsync`

Pola yang dipakai:

- Bangun connection string per database dengan `BuildConnectionString`.
- Pakai `using` untuk `SqlConnection`, `SqlCommand`, dan reader.
- Metadata memakai query ke `sys.*`.
- Parameter object name memakai parameter SQL seperti `@TableFullName`.
- Result set dengan nama kolom duplikat (misalnya `SELECT Units, *`) diberi suffix tampilan `(2)`, `(3)`, dan seterusnya karena `DataTable` memerlukan nama unik.
- `QueryResult` membawa database efektif, rows affected, status/message, data table, dan durasi yang dipakai oleh pencatatan query history.
- `QueryResult` juga membawa collection execution-plan XML; `ExecuteQueryAsync` mendukung mode Execute, Parse, EstimatedPlan, dan ActualPlan pada koneksi/session yang sama.
- Result grid memakai pixel scrolling, recycling virtualization, cache satu halaman, serta tinggi row/header tetap agar layout tidak mengukur ulang ukuran cell saat scroll; telemetry per-frame/visual-tree saat scroll tidak dipasang agar UI tetap ringan.
- Result grid menampilkan nomor baris melalui row header, menonaktifkan sort saat header diklik, dan memakai header text selectable agar nama kolom dapat disalin.
- Row header result grid mendukung klik Shift dan drag ke atas/bawah untuk memilih rentang beberapa row, termasuk auto-scroll sederhana saat pointer melewati batas grid.
- Padding string dari tipe SQL `CHAR/NCHAR` di-trim hanya saat ditampilkan; nilai asli pada `DataTable` tetap dipertahankan.
- Text cell result di-clip ke batas kolom, memakai ellipsis dan padding horizontal, serta garis vertikal lebih kontras supaya nilai panjang tidak terlihat menyatu antar-kolom.

## UI dan Style

- Style global ada di `App.xaml`.
- Style khusus window utama ada di `MainWindow.xaml`.
- Style connection dialog ada di `ConnectionWindow.xaml`.
- Style result grid mengikuti style global `DataGrid` di `App.xaml`.
- TreeView Object Explorer saat ini memakai header string langsung, termasuk icon emoji di `MainWindow.xaml.cs`.
- Template header TreeView memakai `ContentPresenter` tanpa `HeaderTemplate` berbasis `TextBlock`, sehingga header string dan header control interaktif untuk filter sama-sama dirender dengan benar.

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

Publish Release ke folder yang dikonfigurasi pada profile:

```powershell
dotnet publish -p:PublishProfile=FolderProfile
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
