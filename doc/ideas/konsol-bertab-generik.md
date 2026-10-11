# Konsol bertab generik

Tanggal: 2026-10-11. Status: diskusi.

## Keputusan yang sudah jelas

`EmTabStrip` sudah mendukung leading content, tab aktif, close dan reorder. Produk dapat membuat konsol sendiri dengan menu bertingkat dan pencarian; belum ada perintah untuk membangun abstraksi konsol engine.

## Gagasan

Pertimbangkan wadah konsol generik dengan katalog item, factory body, hak akses dari host, pengaktifan ulang tab yang sudah terbuka, dan pelepasan body saat tab ditutup atau identitas berganti. Data, judul, kelompok menu, dan pemuatan tetap disediakan produk.

## Pertanyaan terbuka

- Apakah cukup menggunakan control yang sudah ada atau perlu kontrak konsol bersama?
- Apakah penyimpanan workspace perlu menjadi opsi?
- Bagaimana host menyediakan cek akses dan cancellation?

Plan turunan: belum ada.
