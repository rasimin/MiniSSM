# Visual Studio Code Style UI & Layout Design System

Dokumen ini berisi panduan baku tata letak visual (layout, spacing, alignment) dan sistem warna tema gelap (VS Code inspired) untuk aplikasi **MiniSSMS**. Semua komponen dan fitur baru WAJIB mengikuti standar ini.

---

## 🎨 Palet Warna Terpusat (Color Palette)

### Backgrounds
- **Application Root**: `#181818` (Toolbar, Statusbar, Header, Sidebar background)
- **Main Workspace Canvas**: `#1E1E1E` (Editor, Tab content, Grid container)
- **Secondary Panels**: `#202020` (Log, Details, Alternate DataGrid Row)
- **Control & Inputs**: `#252526` (Input, Dropdown, DataGrid Header, Button)
- **Inactive Tabs**: `#2D2D2D`
- **Hover State**: `#2A2D2E`
- **Selected Item / Row**: `#37373D`
- **Editor / Table Selection**: `#264F78`

### Borders & Dividers
- **Main Border**: `#3C3C3C` (Setipis mungkin, 1px)
- **Panel Divider**: `#2B2B2B`
- **Focus Border**: `#007FD4`

### Text Colors
- **Primary Text**: `#CCCCCC`
- **Secondary / Muted Text**: `#9D9D9D`
- **Placeholder**: `#808080`
- **Disabled Text**: `#6A6A6A`
- **Primary Button Text**: `#FFFFFF`

### Accent Colors (Penggunaan Terbatas)
- **Main Accent**: `#007ACC` (Focus, Active Tab indicator, Primary Action)
- **Accent Hover**: `#1C97EA`
- **Information**: `#3794FF`
- **Success / Execute**: `#4EC9B0`
- **Warning**: `#CCA700`
- **Error / Danger**: `#F14C4C` / `#C42B1C`

---

## 📐 Layout, Spacing & Alignment

### Spacing Scale
- **Extra Small (XS)**: `4px`
- **Small (S)**: `8px`
- **Medium (M)**: `12px`
- **Large (L)**: `16px`
- **Extra Large (XL)**: `24px`

### Dimensions & Heights
- **Toolbar Height**: `36px` – `42px`
- **Input & Button Height**: `30px` – `34px`
- **Tab Height**: `32px`
- **Status Bar Height**: `24px` – `28px`
- **Border Radius**: `2px` – `4px` (Flat, compact, tanpa rounded ekstrim)

---

## 🧩 Aturan Komponen UI

1. **Toolbar & Header**:
   - Background `#181818`, pembatas antar-grup dengan divider tipis `#2B2B2B`.
   - Tombol toolbar: background `Transparent`, hover `#2A2D2E`, active `#37373D`.

2. **Sidebar & Object Explorer**:
   - Background `#181818`, item hover `#2A2D2E`, item selected `#37373D`.
   - Padding item compact (4–6px vertikal), font 12px Segoe UI.

3. **Tabs**:
   - Tab Aktif: `#1E1E1E` dengan garis aksen atas `#007ACC` (2px), teks `#FFFFFF`.
   - Tab Tidak Aktif: `#2D2D2D`, teks `#9D9D9D`, hover `#333333`.

4. **Input, Form & Dropdown**:
   - Background `#252526`, border `#3C3C3C`, focus border `#007FD4`.
   - Label sejajar vertikal dengan input (`VerticalAlignment="Center"`).

5. **Tombol (Buttons)**:
   - **Primary**: Background `#007ACC`, Hover `#1C97EA`, Text `#FFFFFF`.
   - **Secondary**: Background `#3A3D41`, Hover `#45494E`, Text `#CCCCCC`.
   - **Danger**: Background `#C42B1C`, Hover `#E03E2F`, Text `#FFFFFF`.
   - **Disabled**: Background `#2A2A2A`, Text `#6A6A6A`.

6. **DataGrid / Tabel**:
   - Header: Background `#252526`, Text `#CCCCCC`, Border `#2D2D30`.
   - Row Normal: `#1E1E1E`, Row Alternate: `#202020`, Row Hover: `#2A2D2E`, Row Selected: `#37373D`.
   - Gridlines: `#2B2B2B` tipis.

7. **Status Bar**:
   - Background `#181818`, Connected: `#4EC9B0`, Warning: `#CCA700`, Disconnected: `#F14C4C`.

---

## 🚫 Larangan Perubahan Fungsional

- **JANGAN** merubah ID/Name control, binding, event handler, async flow, logic business, koneksi database, atau query SQL.
- Selalu pertahankan nama event `Click`, `SelectionChanged`, `KeyDown`, dll.
- Perubahan visual HANYA boleh mencakup warna, padding, margin, border, font size, alignment, dan konsistensi tema gelap.
