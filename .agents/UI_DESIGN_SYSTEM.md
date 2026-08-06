# Visual Studio Code Style UI & Layout Design System

Dokumen ini adalah **standar baku arsitektur visual (layout, spacing, alignment, control templates)** dan sistem warna tema gelap (VS Code inspired) untuk seluruh window, dialog, dan komponen di **MiniSSMS**. 

Setiap kali membuat window, dialog, atau form baru, **WAJIB** mengacu dan menerapkan standar di dokumen ini.

---

## 🏛️ Arsitektur Window Frameless & Kartu Dialog (Frameless Card Dialog)

Seluruh window sekunder (`SettingsWindow`, `ImportExcelWindow`, `QueryHistoryWindow`, `ObjectSearchWindow`, `ConnectionWindow`, dialog popup, dll) WAJIB menggunakan struktur **Frameless Card**:

```xaml
<Window ...
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        Foreground="#D4D4D4"
        FontFamily="Segoe UI, Arial"
        KeyDown="Window_KeyDown">

    <Border Margin="8" Background="#181818" BorderBrush="#3C3C3C" BorderThickness="1" CornerRadius="6">
        <Border.Effect>
            <DropShadowEffect BlurRadius="12" ShadowDepth="3" Opacity="0.5" Color="#000000"/>
        </Border.Effect>

        <!-- Grid Content Window -->
    </Border>
</Window>
```

### Aturan Utama Window Frameless:
1. **Atribut Window**: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`.
2. **Kontainer Utama**: `<Border Margin="8" Background="#181818" BorderBrush="#3C3C3C" BorderThickness="1" CornerRadius="6">` dilengkapi `DropShadowEffect`.
3. **Window Dragging**: Grid header paling atas melampirkan event `MouseDown="HeaderGrid_MouseDown"`:
   ```csharp
   private void HeaderGrid_MouseDown(object sender, MouseButtonEventArgs e)
   {
       if (e.LeftButton == MouseButtonState.Pressed) DragMove();
   }
   ```
4. **Window Controls (Top-Right)**:
   - Tombol Maximize/Restore (`🗖`): Hover background `#2A2D2E`, Text `#FFFFFF`.
   - Tombol Close (`✕`): Hover background `#C42B1C` (Merah), Text `#FFFFFF`.
5. **Escape Key Shortcut**: Mendukung penutupan window saat menekan tombol `Escape` (`Window_KeyDown`).
6. **Auto-Close Workflow**: Window pencarian/history (`QueryHistoryWindow`, `ObjectSearchWindow`, dll) **WAJIB** menutup otomatis (`Close()`) setelah pengguna memilih/membuka objek ke query editor utama.
7. **Simetri Popup Notification**: Dialog konfirmasi/alert (`UnsavedChangesWindow`, `DarkMessageBoxWindow`) menggunakan `SizeToContent="Height"` dengan padding/margin seimbang (`18,14,18,16`) untuk menghindari ruang kosong vertikal.

---

## 🎨 Palet Warna Terpusat (Color Palette)

### Backgrounds
- **Application Root / Main Card**: `#181818` (Window Container, Outer Card, DataGrid Row)
- **Main Canvas / Data Area**: `#1E1E1E` (Editor, DataGrid Alternating Row, Code Text Area)
- **Secondary Containers & Cards**: `#202020` (Inner Card Box, Filter Container, Header Bar)
- **Control & Inputs**: `#252526` (Input Text, Dropdown, DatePicker, DataGrid Header)
- **Hover State**: `#2A2D2E` / `#3F3F46`
- **Selected Item / Row**: `#007ACC` (Primary Accent Selection)

### Borders & Dividers
- **Card Border**: `#3C3C3C` (Setipis mungkin, 1px)
- **Inner Divider**: `#2B2B2B`
- **Focus Border**: `#007ACC`

### Text Colors
- **Primary Text**: `#F2F2F2` / `#CCCCCC`
- **Secondary / Muted Text**: `#8A8A8A` / `#9D9D9D`
- **Disabled Text**: `#6A6A6A`
- **Primary Button Text**: `#FFFFFF`

### Accent Colors
- **Primary Accent**: `#007ACC` (Focus, Active Tab, Primary Button, Row Selection)
- **Accent Hover**: `#1C97EA`
- **Accent Active**: `#005A9E`
- **Danger / Close Hover**: `#C42B1C`

---

## 🎛️ Standar Komponen & Template Control

### 1. Tombol (Buttons)
Seluruh tombol menggunakan `CornerRadius="3"`, `Height="28px"` – `30px`, font size `12px`.

```xaml
<!-- Secondary Button Style -->
<Style x:Key="DarkSecondaryButtonStyle" TargetType="Button">
    <Setter Property="Height" Value="28"/>
    <Setter Property="MinWidth" Value="80"/>
    <Setter Property="Padding" Value="12,0"/>
    <Setter Property="Foreground" Value="#CCCCCC"/>
    <Setter Property="Background" Value="#2D2D30"/>
    <Setter Property="BorderBrush" Value="#3C3C3C"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" SnapsToDevicePixels="True">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Border" Property="Background" Value="#3F3F46"/>
                        <Setter TargetName="Border" Property="BorderBrush" Value="#555555"/>
                        <Setter Property="Foreground" Value="#FFFFFF"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Border" Property="Background" Value="#202020"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="Border" Property="Background" Value="#202020"/>
                        <Setter TargetName="Border" Property="BorderBrush" Value="#2B2B2B"/>
                        <Setter Property="Foreground" Value="#6A6A6A"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- Primary Button Style -->
<Style x:Key="DarkPrimaryButtonStyle" TargetType="Button" BasedOn="{StaticResource DarkSecondaryButtonStyle}">
    <Setter Property="Background" Value="#007ACC"/>
    <Setter Property="BorderBrush" Value="#007ACC"/>
    <Setter Property="Foreground" Value="#FFFFFF"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
            <Setter Property="Background" Value="#1C97EA"/>
            <Setter Property="BorderBrush" Value="#1C97EA"/>
        </Trigger>
        <Trigger Property="IsPressed" Value="True">
            <Setter Property="Background" Value="#005A9E"/>
        </Trigger>
        <Trigger Property="IsEnabled" Value="False">
            <Setter Property="Background" Value="#202020"/>
            <Setter Property="BorderBrush" Value="#2B2B2B"/>
            <Setter Property="Foreground" Value="#6A6A6A"/>
        </Trigger>
    </Style.Triggers>
</Style>
```

---

### 2. Input Form (TextBox & ComboBox)
- Background `#252526`, Border `#3C3C3C`, CornerRadius `3px`, Height `28px`.
- Seluruh elemen internal scroll host diset `Background="Transparent"` dan `BorderThickness="0"` untuk **mencegah kebocoran inner dark box**.

```xaml
<!-- Dark TextBox Style -->
<Style TargetType="TextBox">
    <Setter Property="Background" Value="#252526"/>
    <Setter Property="Foreground" Value="#F2F2F2"/>
    <Setter Property="BorderBrush" Value="#3C3C3C"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="8,4"/>
    <Setter Property="Height" Value="28"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="CaretBrush" Value="White"/>
    <Setter Property="SelectionBrush" Value="#007ACC"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="TextBox">
                <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" SnapsToDevicePixels="True">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="False" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="bd" Property="BorderBrush" Value="#007ACC"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

---

### 3. DatePicker (Filter Tanggal Dark Mode)
Wajib menggunakan `DarkDatePickerTextBoxStyle` tanpa border internal agar menyatu dengan `#252526` dan ikon kalender vektor dark mode:

```xaml
<!-- DatePickerTextBox Style (No Inner Border) -->
<Style x:Key="DarkDatePickerTextBoxStyle" TargetType="DatePickerTextBox">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Foreground" Value="#F2F2F2"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="SelectionBrush" Value="#007ACC"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="DatePickerTextBox">
                <Grid Background="Transparent">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="False" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" VerticalAlignment="Center" Margin="6,0,0,0"/>
                </Grid>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<!-- Custom Dark DatePicker Styling -->
<Style TargetType="DatePicker">
    <Setter Property="Background" Value="#252526"/>
    <Setter Property="Foreground" Value="#F2F2F2"/>
    <Setter Property="BorderBrush" Value="#3C3C3C"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Height" Value="28"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="DatePicker">
                <Border x:Name="OuterBorder" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" SnapsToDevicePixels="True">
                    <Grid x:Name="PART_Root" HorizontalAlignment="Stretch" VerticalAlignment="Stretch" Background="Transparent">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="26"/>
                        </Grid.ColumnDefinitions>
                        
                        <DatePickerTextBox x:Name="PART_TextBox" Grid.Column="0" Style="{StaticResource DarkDatePickerTextBoxStyle}" Focusable="{TemplateBinding Focusable}" HorizontalContentAlignment="Stretch" VerticalContentAlignment="Center" Background="Transparent" Foreground="{TemplateBinding Foreground}" BorderThickness="0" FontSize="12"/>

                        <Button x:Name="PART_Button" Grid.Column="1" Focusable="False" Background="Transparent" BorderThickness="0" Cursor="Hand">
                            <Button.Template>
                                <ControlTemplate TargetType="Button">
                                    <Border x:Name="btnBorder" Background="Transparent" CornerRadius="0,3,3,0">
                                        <Path x:Name="calIcon" Data="M 19 4 h-1 V 2 h-2 v 2 H 8 V 2 H 6 v 2 H 5 c-1.11 0-1.99.9-1.99 2 L 3 20 c 0 1.1.89 2 2 2 h 14 c 1.1 0 2-.9 2-2 V 6 c 0-1.1-.9-2-2-2 z m 0 16 H 5 V 10 h 14 v 10 z M 9 14 H 7 v-2 h 2 v 2 z m 4 0 h-2 v-2 h 2 v 2 z m 4 0 h-2 v-2 h 2 v 2 z M 9 18 H 7 v-2 h 2 v 2 z m 4 0 h-2 v-2 h 2 v 2 z m 4 0 h-2 v-2 h 2 v 2 z" Fill="#A0A0A0" Width="13" Height="13" Stretch="Uniform" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property="IsMouseOver" Value="True">
                                            <Setter TargetName="btnBorder" Property="Background" Value="#3A3A3C"/>
                                            <Setter TargetName="calIcon" Property="Fill" Value="#FFFFFF"/>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </Button.Template>
                        </Button>
                        <Popup x:Name="PART_Popup" AllowsTransparency="True" Placement="Bottom" StaysOpen="False"/>
                    </Grid>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="OuterBorder" Property="BorderBrush" Value="#555555"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

---

### 4. DataGrid (Tabel Data Dark Mode)
```xaml
<Style TargetType="DataGrid">
    <Setter Property="Background" Value="#1E1E1E"/>
    <Setter Property="Foreground" Value="#CCCCCC"/>
    <Setter Property="BorderBrush" Value="#2B2B2B"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="RowBackground" Value="#181818"/>
    <Setter Property="AlternatingRowBackground" Value="#1E1E1E"/>
    <Setter Property="HorizontalGridLinesBrush" Value="#2B2B2B"/>
    <Setter Property="VerticalGridLinesBrush" Value="#2B2B2B"/>
    <Setter Property="HeadersVisibility" Value="Column"/>
</Style>

<Style TargetType="DataGridColumnHeader">
    <Setter Property="Background" Value="#202020"/>
    <Setter Property="Foreground" Value="#CCCCCC"/>
    <Setter Property="Padding" Value="8,6"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="BorderBrush" Value="#2B2B2B"/>
    <Setter Property="BorderThickness" Value="0,0,1,1"/>
</Style>

<Style TargetType="DataGridRow">
    <Setter Property="Background" Value="#181818"/>
    <Setter Property="Foreground" Value="#CCCCCC"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Style.Triggers>
        <Trigger Property="IsSelected" Value="True">
            <Setter Property="Background" Value="#007ACC"/>
            <Setter Property="Foreground" Value="White"/>
        </Trigger>
        <Trigger Property="IsMouseOver" Value="True">
            <Setter Property="Background" Value="#2A2D2E"/>
        </Trigger>
    </Style.Triggers>
</Style>
```

---

### 5. Multi-line Code & Log TextBox
Untuk area teks berukuran besar (SQL Script, Definition, Execution Message, Log Output):

```xaml
<Style x:Key="CodeBoxStyle" TargetType="TextBox">
    <Setter Property="Background" Value="#181818"/>
    <Setter Property="Foreground" Value="#F2F2F2"/>
    <Setter Property="BorderBrush" Value="#2B2B2B"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="8,6"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="FontFamily" Value="Consolas"/>
    <Setter Property="CaretBrush" Value="White"/>
    <Setter Property="SelectionBrush" Value="#007ACC"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="TextBox">
                <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" SnapsToDevicePixels="True">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="True" VerticalAlignment="Stretch" HorizontalAlignment="Stretch"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="bd" Property="BorderBrush" Value="#007ACC"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

---

## 🚫 Larangan Perubahan Fungsional

- **JANGAN** merubah ID/Name control (`x:Name`), binding, event handler, async flow, logic business, koneksi database, atau query SQL.
- Selalu pertahankan nama event `Click`, `SelectionChanged`, `KeyDown`, `MouseDown`, dll.
- Perubahan visual HANYA boleh mencakup warna, padding, margin, border, font size, alignment, dan konsistensi tema gelap.
