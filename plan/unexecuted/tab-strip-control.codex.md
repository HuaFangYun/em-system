# Plan: Control tab strip bersama (`EmTabStrip`) dan style menu bertingkat

- **Tanggal:** 2026-10-10
- **Status:** belum dieksekusi — menunggu persetujuan eksekusi pengguna
- **Agent:** Codex (Claude Code boleh memakai file yang sama)
- **Repo:** hanya em-system. Konsumen pertama adalah layar konsol analytics di repo turunan privat; plan
  konsumennya ada di repo itu dan menautkan ke plan ini. Plan ini **tidak** menyebut nama produk, modul bisnis,
  server, atau database repo turunan (em-system repo publik).

Saat ini tampilan dan perilaku deretan tab `TabbedMainWindow` (tab bergaya material, scroll kiri/kanan lewat
`TabStripPanel`, tombol close, middle-click close, geser untuk mengurutkan) bersifat privat di
`Windows/TabbedMainWindow.xaml` dan `Windows/TabbedMainWindowTabDrag.cs`. Layar lain yang butuh tab di dalam
satu navigasi (mis. konsol yang membuka beberapa panel) tidak bisa memakainya. Plan ini membuat **control
publik baru** dengan tampilan yang sama, plus style tombol menu dan menu bertingkat yang bisa dipakai di depan
deretan tab.

---

## 1. Keputusan final (keputusan pengguna 2026-10-10)

| # | Topik | Keputusan |
| --- | --- | --- |
| K1 | Bentuk | Control publik baru di `Em.Ui.Wpf.Core`, bukan salinan di repo turunan. |
| K2 | Host | **`TabbedMainWindow` tidak diubah.** Control baru berdiri sendiri dan meniru tampilannya; tidak ada refactor host ke control baru. |
| K3 | Perilaku tab | Geser kiri/kanan untuk mengurutkan, middle-click menutup, tombol close (×). **Tanpa** detach/tear-off, tanpa pindah ke window lain, tanpa tombol reload, tanpa context menu. |
| K4 | Menu di depan tab | **Opsional, ditentukan pemakai** (keputusan pengguna 2026-10-10): `EmTabStrip` tidak punya menu bawaan. Bila `LeadingContent` kosong, tidak ada ruang/tombol apa pun di kiri tab. Engine hanya menyediakan slot konten di kiri deretan tab untuk tombol menu (bentuk seperti tombol "Apps" host: ikon + teks + chevron) dan style menu yang mendukung **submenu** serta **header dua baris (title + subtitle)**. |
| K5 | Uji visual | **Disetujui** pengguna 2026-10-10 untuk control baru ini: jalankan harness render tanpa bertanya lagi. |
| K6 | Commit | Commit per fase di branch `work-bench`, pesan Bahasa Indonesia, tanpa push. |

### Keputusan cadangan (tidak perlu ditanyakan saat eksekusi)

- **Branch:** kerja dan commit di `work-bench` (pengguna sudah memindahkan checkout ke `work-bench`
  2026-10-10). Bila saat eksekusi checkout tidak di `work-bench` atau tidak bersih: kerjakan perubahan, jangan
  commit, catat di laporan. Jangan `stash`/`reset`/`checkout --`.
- **Nama:** control `EmTabStrip` (namespace `Em.Ui.Wpf.Controls`, seperti `TabStripPanel`). Style:
  `tabStripMenuButtonStyle` (ToggleButton), `cascadeContextMenuStyle` (ContextMenu), `cascadeMenuItemStyle`
  (MenuItem). Bila nama bentrok dengan key yang sudah ada, tambahkan awalan `em` dan catat.
- **Pengurutan:** bila `ItemsSource` adalah `IList` yang bisa ditulis (mis. `ObservableCollection<T>` → pakai
  `Move`), control memindahkan item sendiri; selain itu control hanya menaikkan event dan tidak memindahkan.
- **Item terpilih saat digeser:** item yang digeser tetap terpilih; menggeser tidak mengganti tab aktif.

---

## 2. Aturan yang wajib diikuti (ringkasan dari `CLAUDE.md` em-system)

- Baca `CLAUDE.md` root em-system versi terbaru sebelum mulai.
- Komentar kode, XML doc, komentar XAML, teks UI, pesan exception/log: **bahasa Inggris**. Member public/
  protected baru wajib punya XML doc (CS1591 tidak di-`NoWarn`). Plan dan laporan berbahasa Indonesia.
- Generik: tanpa nama modul bisnis/produk turunan.
- Warna hanya dari token tema (`themeWindow*`, `themeAccentBrush`, `themePopupBackgroundBrush`, brush
  `MaterialDesign.xaml` seperti `stateHoverBrush`, `statePressedBrush`, `outlineStrongBrush`). Salin bentuk
  template dari `TabbedMainWindow.xaml` (`tabStripStyle`, `tabCloseButtonStyle`, `tabScrollButtonStyle`,
  `appsButtonStyle`, `menuPopupBorderStyle`, `menuRowStyle`), jangan mengarang tampilan baru.
- List/menu yang item-nya objek wajib punya display text eksplisit (aturan "List: pisahkan item/nilai pilihan
  dari teks tampilan"): judul tab diambil dari property yang ditunjuk `TitlePath`, bukan `ToString()`.
- Harness uji dan output di `..\.artefacts\em-system\scripts\<nama>-render\` / `-smoke\`, me-link
  `..\_shared\HarnessPaths.cs`; harness tidak boleh membuat berkas di repo.
- Tindakan yang ditolak policy: jangan diulang lewat jalan lain; siapkan `.ps1` di
  `plan/tab-strip-control-manual/` dan laporkan.
- Urutan kerja: tulis seluruh kode fase 1–2 dulu, baru build/test/render/review di fase 3.

---

## 3. Fase

### Fase 1 — Control `EmTabStrip`

Berkas: `src/shared/Em.Ui.Wpf.Core/Controls/EmTabStrip.cs`, default style di `Themes/Generic.xaml` (pola sama
dengan `EmStatusBar`/`WaitOverlay`: `DefaultStyleKeyProperty.OverrideMetadata`).

1. `EmTabStrip : ListBox` dengan dependency property:
   - `TitlePath` (`string`, bawaan `"Title"`) — property item yang menjadi teks tab; juga tooltip.
   - `CloseCommand` (`ICommand?`) — dipanggil dengan item sebagai parameter oleh tombol × dan middle-click.
   - `CanReorder` (`bool`, bawaan `true`).
   - `MinItemWidth` (`double`, bawaan `130`) — diteruskan ke `TabStripPanel`.
   - `LeadingContent` (`object?`) + `LeadingContentTemplate` — opsional, ditampilkan di kiri deretan tab, di
     luar area scroll (tempat tombol menu bila pemakai menginginkannya). Bawaan `null`: elemennya collapsed,
     tanpa margin, sehingga tab mulai dari tepi kiri. Control tidak membuat tombol/menu sendiri.
   - Routed event `ItemMoved` (`EmTabStripItemMovedEventArgs`: `Item`, `OldIndex`, `NewIndex`).
2. Template: `DockPanel` berisi `LeadingContent` lalu `ScrollViewer` (`CanContentScroll="True"`, tombol
   scroll kiri/kanan seperti `tabStripStyle`) dengan `ItemsPanel` = `TabStripPanel`. Item container: bentuk
   tab host (tinggi 40, state layer `Margin="2,5"` `CornerRadius="8"`, hover/selected seperti host, tombol
   close tersembunyi lalu tampil saat hover/selected, ukuran ikon dan font sama). Teks tab dibuat lewat
   binding ke `TitlePath` (binding dibuat di kode saat `PrepareContainerForItemOverride` atau lewat template
   yang membaca path; pilih yang paling sederhana dan dokumentasikan).
3. Middle-click pada tab → `CloseCommand.Execute(item)` bila `CanExecute`.
4. Geser: logika dari `TabbedMainWindowTabDrag` **disederhanakan dan disalin ke dalam control** (bukan
   dipindah dari host): mulai setelah melewati `SystemParameters.MinimumHorizontalDragDistance`, capture di
   `ListBoxItem` (bukan di strip, agar tidak memilih item yang dilewati), indeks tujuan dari posisi pointer
   relatif terhadap titik tengah tab lain, pindahkan saat melewati batas, tanpa ghost/tear-off. Klik pada
   tombol di dalam tab tidak memulai geser. `Escape` atau kehilangan capture membatalkan dengan posisi
   terakhir tetap.
5. Logika pemindahan murni dipisah ke method static internal/publik yang bisa diuji tanpa UI, mis.
   `EmTabStrip.TryMove(IList list, int from, int to)`, dan memakai `ObservableCollection<T>.Move` bila
   tersedia (lewat refleksi `Move(int,int)` atau pola generik yang aman).

**Commit:** "EmTabStrip: control tab strip bersama".

### Fase 2 — Style tombol menu dan menu bertingkat

Berkas: `src/shared/Em.Ui.Wpf.Core/Styles/Menus.xaml` (sudah di-merge `MaterialDesign.xaml`).

1. `tabStripMenuButtonStyle` (`ToggleButton`): bentuk tombol "Apps" host (`appsButtonStyle` berbasis
   `titleBarToggleStyle`), tetapi berdiri sendiri — tidak bergantung pada resource privat window. Konten
   (ikon + teks + chevron) diisi pemakai.
2. `cascadeContextMenuStyle` (`ContextMenu`) dan `cascadeMenuItemStyle` (`MenuItem`): popup bergaya
   `menuPopupBorderStyle` (radius 10, outline, bayangan), baris bergaya `menuRowStyle` (hover/pressed state
   layer, radius 6, `MinHeight` 34). `MenuItem` mendukung keempat `Role` (`TopLevelHeader`, `TopLevelItem`,
   `SubmenuHeader`, `SubmenuItem`): submenu menampilkan chevron kanan dan membuka `Popup` di kanan dengan
   gaya yang sama; `IsChecked` menampilkan tanda di kolom ikon; `Separator` bergaya garis `outlineBrush`;
   disabled = opacity 0.4. Header memakai `HeaderTemplate` dari pemakai, sehingga header dua baris (title +
   subtitle) cukup lewat template. Sertakan `DataTemplate` siap pakai `menuTitleSubtitleTemplate` yang membaca
   `Title` dan `Subtitle` (subtitle 11px, opacity 0.65, tersembunyi bila kosong).
3. `surfaceMenuItemStyle`/`surfaceContextMenuStyle` yang sudah ada **tidak diubah**.

**Commit:** "Style tombol menu dan menu bertingkat untuk EmTabStrip".

### Fase 3 — Uji, review, dokumen

1. `dotnet build src/frontend/Em.Ui.Wpf.slnx` dan `dotnet build src/backend/Em.Api.slnx` bersih (tanpa
   warning baru).
2. Test di `tests/Em.Ui.Wpf.Core.Tests` (masuk solution, MTP v2): `TryMove` (maju, mundur, indeks sama,
   di luar batas, list read-only), `CloseCommand` dipanggil oleh middle-click dan tombol × (uji STA dengan
   control nyata bila pola test yang ada mendukung; bila tidak, uji handler), `ItemMoved` terpicu dengan
   indeks benar, dan `SharedStylesTests` memastikan key style baru dapat dimuat dari `MaterialDesign.xaml`.
   Jalankan `dotnet test src/frontend/Em.Ui.Wpf.slnx`.
3. Harness render (disetujui, K5) `..\.artefacts\em-system\scripts\tabstrip-render\`: terang dan gelap —
   strip tanpa `LeadingContent` (tab mulai dari tepi kiri), strip dengan tombol menu + 3 tab (satu terpilih,
   satu hover), strip penuh dengan tombol scroll, menu
   terbuka dengan submenu dan header title+subtitle, item menu dengan tombol × di header. Bandingkan dengan
   render `TabbedMainWindow` yang sudah ada (tinggi tab, radius, warna) dan perbaiki selisih.
4. Review mandiri terhadap bagian 2; perbaiki.
5. Guide `doc/engine/engine-tab-strip.md` (bahasa Inggris): tujuan, properti/event, contoh XAML dengan tombol
   menu + menu bertingkat, cara mengurutkan item di view model, batasan (tanpa detach, host tidak memakai
   control ini). Tautkan dari tabel "Desktop (WPF) features" di `doc/engine/README.md`. Screenshot terang dari
   harness disalin manual ke `doc/engine/images/` bila dipakai.
6. Laporan `plan/executed/tab-strip-control-report.md`, pindahkan plan ke `plan/executed/`.

**Commit:** "EmTabStrip: test, guide, dan laporan".

---

## 4. Di luar cakupan

- Mengubah `TabbedMainWindow` atau `TabbedMainWindowTabDrag` (K2).
- MAUI (tidak ada padanan; WPF saja).
- Release NuGet / release note (repo turunan memakai ProjectReference).

## 5. Kriteria selesai

- `EmTabStrip` dan ketiga style tersedia publik, ter-build, test lulus, PNG terang/gelap diperiksa.
- Guide tertaut dari README engine; plan dan laporan di `plan/executed/`; commit di `work-bench` tanpa push.
