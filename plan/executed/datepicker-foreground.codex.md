# Perbaikan warna teks date picker

Tanggal: 2026-10-11. Status: implementasi dan pemeriksaan console selesai; belum commit/push.

## Masalah dan perubahan

Pada tema gelap, teks tanggal dan sebagian isi kalender tampak seperti disabled karena foreground bawaan
WPF mengalahkan warna yang diwariskan. Pemilihan tanggal tidak diubah.

`src/shared/Em.Ui.Wpf.Core/Styles/Inputs.xaml` kini menetapkan `themeWindowForegroundBrush` melalui
DynamicResource pada `inlineDatePickerStyle`, `materialCalendarItemStyle`,
`materialCalendarDayButtonStyle`, dan `materialCalendarButtonStyle`. Field framed mewarisi style inline.
Teks pilihan tetap memakai `themeOnAccentBrush`; tanggal bulan tetangga dan blackout tetap lebih pudar.
Perbaikan berlaku bagi seluruh pengonsumsi style bersama.

## Verifikasi

- Build solution WPF pengonsumsi lulus: 0 warning, 0 error.
- Harness console `..\.artefacts\em-system\scripts\datepicker-theme-smoke\` lulus **270 assertion**.
  Template nyata dipasang pada window yang tidak ditampilkan. Pemeriksaan mencakup teks field inline/framed,
  42 tanggal, tujuh nama hari, pilihan bulan/tahun, warna selected, blackout, disabled, perubahan tanggal,
  serta pergantian tema dark → light → dark pada instance yang sama.
- Tidak menampilkan window, merender PNG, atau mengubah database/Registry. Interaksi mouse dan verifikasi
  visual aplikasi berjalan belum diuji.
- Bukti: `out/verification.log` dan `out/frontend-build.log` milik harness.

Aplikasi yang sedang terbuka perlu dibangun ulang dan dibuka kembali untuk memuat resource baru.
