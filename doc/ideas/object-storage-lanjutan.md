# Object storage — usulan lanjutan dari eksekusi v1

- **Tanggal:** 2026-10-11
- **Status:** diskusi
- **Asal:** temuan saat mengeksekusi [plan/executed/object-storage.md](../../plan/executed/object-storage.md)

Catatan ini bukan perintah kerja. Isinya usulan yang muncul saat membangun object storage v1 dan belum
diputuskan pengguna.

## Usulan

1. **Fixture test integrasi dengan SQL authentication.** `SqlServerDatabase` hanya mendukung Windows
   Authentication lewat `EM_TEST_DB_SERVER`. Variabel tambahan berisi connection string master lengkap
   (mis. `EM_TEST_DB_CONNECTION_STRING`) akan memungkinkan test integrasi berjalan di Linux/CI atau container
   SQL Server. Saat eksekusi v1, 13 test integrasi object storage tidak bisa dijalankan karena hal ini dan
   karena unduhan SQL Server diblokir proxy sesi.
2. **Ukur listing berprefiks pada bucket besar.** Index memakai kolom terhitung `cStoreObjectKeyPrefix`
   (400 karakter pertama). Query listing memakai `LIKE prefix%` dan `> cursor` pada key penuh; perlu diukur
   dengan jutaan object apakah optimizer memakai index tersebut, atau query perlu menyaring lewat kolom
   prefiks secara eksplisit.
3. **Contoh pemakaian dari module uji `Em.Test`.** Plan menyebutnya opsional dan belum dibuat: satu action
   contoh yang menyimpan/membaca lewat `IObjectStore` akan menjadi uji ujung ke ujung lewat HTTP.
4. **Retry deadlock.** Transaksi bucket memakai serializable + `UPDLOCK`. Urutan kunci dirancang agar tidak
   membentuk siklus, tetapi retry otomatis pada error 1205 belum ada.
5. **Probe volume pada status.** `GetMeta_StoreStatus` dan daftar volume menulis berkas probe kecil ke
   `.staging` setiap volume pada tiap panggilan. Bila card home sering di-refresh, hasil probe bisa di-cache
   singkat.
6. **Batas ukuran unggah dari WPF.** Unggah layar manager membaca berkas lokal satu per satu tanpa progres
   per byte dan tanpa tombol batal; untuk berkas besar perlu progres dan pembatalan seperti CDN Manager.
