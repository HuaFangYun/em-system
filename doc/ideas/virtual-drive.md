# Virtual drive (drive Windows yang isinya di server)

- **Tanggal:** 2026-10-11
- **Status:** diskusi; hanya ide, tidak dilanjutkan ke plan (keputusan pengguna 2026-10-11); belum ada kode
- **Plan turunan:** tidak ada (sengaja)
- **Catatan terkait:** [object-storage.md](object-storage.md) dan [object-storage-lanjutan.md](object-storage-lanjutan.md) (calon penyimpanan sisi server), [stream-dua-arah.md](stream-dua-arah.md) (calon jalur notifikasi perubahan)

Catatan ini merangkum diskusi tentang produk yang tampil seperti drive di Windows (mirip Google Drive), isinya ada di server, dengan cache lokal. Isinya gagasan, bukan perintah kerja; agent tidak boleh mengeksekusinya sebelum dijadikan plan di `plan/unexecuted/`. Bagian lisensi dan signing adalah analisis teknis dari diskusi, bukan nasihat hukum; verifikasi ke sumber resmi sebelum memutuskan.

## Tujuan produk

- Kantor dengan data terdistribusi di laptop (sering berpindah, kadang offline) dan folder yang dibagi ke pengguna lain.
- Harus bersifat drive: aplikasi lain membaca dan menulis file tanpa tahu bahwa isinya cloud.
- Server mengontrol kebijakan per drive/folder: ukuran file maksimum dan ekstensi yang boleh masuk (contoh ekstrem: hanya `.xlsx`; menyalin file lain harus gagal).
- Cache lokal supaya file yang sering dipakai tetap bisa dibuka offline.
- Bukan block storage (bukan iSCSI): server tahu file dan folder, bukan sektor disk.

## Keputusan yang sudah jelas

1. **Level file system, bukan block device.** Block storage tidak memberi versi per file, share, kuota, atau sinkronisasi per file, dan satu volume tidak aman dipakai dua komputer sekaligus.
2. **Protokol ke server: HTTPS** ke `Em.Api` (REST untuk metadata, `Range` untuk baca sebagian, `ETag`/`If-Match` untuk deteksi konflik, upload per bagian untuk file besar). Notifikasi perubahan dari server lewat WebSocket/SSE (lihat `stream-dua-arah.md`). TCP/protokol sendiri dinilai tidak sebanding.
3. **Bahasa sisi Windows: C#** (.NET, satu bahasa dengan engine dan UI); Rust dipertimbangkan sebagai alternatif (Launcher sudah Rust).
4. **Server menegakkan aturan** (ukuran, ekstensi, kuota) apa pun pilihan client. Client hanya menyalin kebijakan untuk respons cepat dan untuk bekerja saat offline.
5. **Struktur menurut aturan library bersama:** kontrak dan DTO di `Em.Libs`, backend di `Em.Api.Core` di atas engine object storage, client drive sebagai library engine baru; host/produk hanya menambah UI tray atau pengaturan.

## Pilihan teknologi drive di Windows

| Pendekatan | Hasil | Catatan |
|---|---|---|
| **Cloud Files API** (Windows 10 1709+, driver `cldflt` milik Microsoft) | Folder sync root dengan placeholder, ikon status, *available offline* | Bebas lisensi (MIT bersih). Folder harus NTFS; bukan drive letter (opsi: `subst Z:`, perlu diuji). **Tidak bisa menolak penyalinan saat itu juga**: file tersimpan lokal dulu, penolakan terjadi saat sync |
| **WinFsp** (+ binding .NET `winfsp-msil`) | Drive letter, file system sepenuhnya virtual | Bisa menolak di `Create`/rename saat itu juga. Lisensi GPLv3 dengan FLOSS exception: syaratnya proyek berlisensi FLOSS, menampilkan teks "WinFsp - Windows File System Proxy" dengan tautan repo, dan tidak di-link/didistribusikan bersama software proprietary; selain itu ada lisensi komersial. Cache dan offline harus dibuat sendiri. Pengguna proprietary yang mengaktifkan paket ini perlu lisensi komersial sendiri |
| **Dokany** | Mirip WinFsp | Lisensi per komponen berbeda; cek ulang versi terbaru sebelum dipakai |
| **ProjFS** | Folder proyeksi (gaya Git VFS) | Tanpa drive letter |
| **Cloud Files + minifilter kecil milik sendiri** | Kebijakan ekstensi/ukuran ditegakkan di muka, sisanya (placeholder, cache, offline) tetap oleh Microsoft | Driver kecil (mencegat Create dan Rename), kebijakan didorong dari service user-mode lewat communication port. Tetap butuh signing kernel, altitude dari Microsoft, uji lintas versi Windows dan antivirus. Dukungan Rust (`windows-drivers-rs`) untuk API minifilter **belum diverifikasi** |
| **Driver file system penuh sendiri** (tulis ulang WinFsp) | Drive letter, bebas lisensi | Sangat besar: semantik NTFS, signing, keamanan, perawatan lintas versi Windows. Tidak disarankan; bila dilakukan harus clean-room |

Catatan signing: driver kernel di Windows 10/11 harus ditandatangani lewat Microsoft Partner Center (attestation/WHQL); `winfsp-x64.sys` yang dipasang pengguna sudah melewati proses itu oleh pembuat WinFsp. Driver milik sendiri berarti akun Partner Center, sertifikat EV, dan pengujian rilis demi rilis (cek persyaratan terbaru).

## Arah yang dipilih (kecenderungan, belum final)

1. Mulai dengan **Cloud Files API saja**: penolakan file terlarang saat sync, dengan ikon error, notifikasi jelas, dan karantina. Sudah menghasilkan produk yang berfungsi tanpa driver apa pun dan menjaga em-system tetap MIT.
2. Tambahkan **minifilter kecil** sebagai tahap berikutnya hanya jika penolakan di muka benar-benar diminta pelanggan.
3. **WinFsp** hanya sebagai paket drive opsional dan terpisah, dengan catatan lisensi di README dan release note. Jangan menyertakan installer atau DLL WinFsp di repo atau paket NuGet.
4. Driver file system penuh sendiri (Rust) ditetapkan pengguna pada 2026-10-11 sebagai target **jangka panjang**; lihat bagian "Keputusan jangka panjang" di bawah. Jalur jangka pendek (butir 1-3) tetap berlaku sampai studi kelayakan driver selesai.

## Kebijakan server (ukuran dan ekstensi)

- Cakupan perlu diputuskan: per drive, per folder, atau per pengguna; daftar boleh (allowlist) atau daftar larang.
- **Jebakan Office:** Excel/Word menyimpan lewat file sementara (nama acak tanpa ekstensi atau `.tmp`), file kunci `~$nama.xlsx`, lalu rename. Aturan "hanya xlsx" yang kaku membuat penyimpanan gagal. Rancangan: izinkan pola file sementara (`~$*`, `*.tmp`, tanpa ekstensi) hanya di cache lokal dan **jangan pernah diunggah**; tegakkan aturan pada titik rename ke nama akhir; bersihkan sisa file sementara saat ditutup.
- **Rename menipu:** cek ekstensi juga saat rename. Server dapat memeriksa tanda isi file (xlsx adalah ZIP berstruktur, mis. `[Content_Types].xml`), bukan hanya nama.
- **Perubahan kebijakan:** tentukan perlakuan file yang sudah ada tetapi melanggar aturan baru (dibiarkan, ditandai, atau dikarantina).
- Kebijakan perlu tersimpan di client agar tetap berlaku saat offline.

## Sync engine (porsi pekerjaan terbesar)

- Delta sync: nomor urut perubahan (change journal) di server; client menyimpan kursor terakhir dan hanya meminta perubahan sesudahnya.
- Konflik: simpan dua versi ("nama (konflik oleh X)"); jangan menimpa diam-diam.
- Locking lunak untuk file Office ("sedang dibuka oleh X").
- Hak akses per folder bersama memakai role/claim yang sudah ada di em-system.
- Database lokal client (mis. SQLite) untuk status tiap file dan antrean upload yang bisa dilanjutkan setelah koneksi putus.
- Upload/unduh per bagian untuk file besar; notifikasi perubahan lewat WebSocket.
- Versi dan pemulihan file, kuota, audit (sebagian sudah ada di engine object storage).

## Urutan pengembangan (usulan)

1. Baca saja: list folder dan buka file dari server, dengan cache.
2. Tulis: upload perubahan dari client.
3. Offline, pin, dan antrean upload.
4. Folder bersama, delta sync, notifikasi.
5. Konflik dan locking.
6. Kebijakan ukuran/ekstensi (sisi server dulu, lalu sisi client).
7. Versi, pemulihan, kuota, audit. Minifilter bila terbukti perlu.

## Rencana uji ketangguhan

Tujuan: mengukur seberapa "asli" drive-nya. Tentukan kriteria lolos di awal (contoh: tidak ada data hilang setelah 100 crash acak, `DBCC CHECKDB` bersih, tidak ada file setengah-tertulis di server, Excel selalu bisa menyimpan).

- **Database sebagai uji siksa:** `CREATE DATABASE`/`ATTACH` file `.mdf`/`.ldf` di drive, beban tulis berat, `DBCC CHECKDB` sesudahnya, matikan paksa proses atau VM di tengah transaksi lalu cek crash recovery. SQLite (WAL dan rollback journal) sebagai versi ringan. Harapan: model sync (Cloud Files) hampir pasti gagal atau tidak stabil dan SQL Server tidak mendukung data file di penyimpanan seperti ini; cukup dicatat sebagai "tidak didukung". File system virtual penuh bisa lolos hanya bila write-through, `FlushFileBuffers`, byte-range lock, dan I/O tanpa buffer benar. Pengguna database dianjurkan menjalankan SQL Server di server dan terhubung lewat jaringan.
- **I/O dan semantik file:** `diskspd` (acak/berurutan, write-through, multi-thread), byte-range lock dan share mode antarproses, memory-mapped file, rename atomik, `ReplaceFile`, path panjang, file puluhan GB, ribuan file kecil, folder dengan banyak isi. Bila memakai WinFsp, jalankan suite `winfsp-tests` (periksa keberadaannya di versi yang dipakai).
- **Aplikasi nyata:** Excel dan Word (simpan, simpan sebagai, autorecover, buka dari dua mesin), `git clone` dan build, `robocopy` massal, copy/paste massal di Explorer, dengan Defender dan Windows indexer aktif.
- **Gangguan:** jaringan putus saat upload/unduh, proses sync dimatikan paksa, laptop dimatikan tiba-tiba, server tak terjangkau saat file terbuka, dua pengguna mengedit file sama, jam client dan server berbeda.
- **Kebijakan server:** salin file non-xlsx (harus ditolak), rename `.exe` menjadi `.xlsx`, file melebihi ukuran maksimum, file sementara Excel.
- Hasil dicatat sebagai matriks "didukung / didukung dengan batas / tidak didukung" untuk dokumentasi produk.

## Pertanyaan terbuka

- Apakah produk ini nantinya dibuka sebagai MIT bersama em-system atau dijual komersial tertutup? Menentukan apakah WinFsp cukup dengan FLOSS exception atau perlu lisensi komersial.
- Apakah penolakan saat sync (dengan notifikasi jelas) dapat diterima, atau pengguna harus melihat error langsung di aplikasi saat menyimpan? Menentukan Cloud Files saja, Cloud Files + minifilter, atau WinFsp.
- Apakah drive letter (`Z:`) wajib, atau entri di panel Explorer sudah cukup? Bila wajib, uji dulu apakah `subst` bekerja baik dengan sync root Cloud Files.
- Perkiraan total data per kantor dan jumlah pengguna (GB atau TB; puluhan atau ratusan orang).
- Seberapa sering pengguna mengedit file Office bersamaan di folder bersama (menentukan prioritas locking).
- Cakupan kebijakan (per drive, folder, atau pengguna) dan jenis daftar (boleh atau larang).
- Dukungan binding Rust untuk API minifilter dan file system driver (`windows-drivers-rs`, header `ntifs.h`, Cache Manager, `FsRtl*`) belum diverifikasi.
- Apakah Windows saja untuk versi pertama? macOS/Linux memerlukan implementasi lain (File Provider, FUSE).

## Keputusan jangka panjang: driver kernel sendiri dengan Rust (pengguna, 2026-10-11)

Pengguna memutuskan untuk jangka panjang menulis driver kernel file system sendiri dengan Rust, memakai panduan driver dari Microsoft, dengan WinFsp sebagai referensi karena dinilai lebih murah. Tujuannya: drive letter asli, penolakan ekstensi/ukuran di muka, tanpa ketergantungan pihak ketiga, dan lisensi MIT.

### Rancangan: driver tipis, logika di user-mode

```
Aplikasi -> I/O Manager Windows
              | IRP (Create, Read, Write, QueryDirectory, SetInformation, ...)
   Driver Rust (kernel): terima IRP, antre, teruskan
              | IOCTL / antrean permintaan
   Service user-mode (Rust atau C#): kebijakan xlsx-saja, cache, sync, HTTPS ke Em.Api
              | jawaban
   Driver menyelesaikan IRP
```

- Kebijakan bisnis (ekstensi, ukuran, kuota, cache, sinkronisasi) tinggal di user-mode agar aman di-debug dan tidak menyebabkan BSOD.
- Driver tetap harus benar pada semantik Windows: objek volume/device, integrasi Cache Manager dan Memory Manager (mapped file, paging I/O), byte-range lock, oplock, share mode (`FsRtl*`), notifikasi perubahan direktori, security descriptor, rename atomik, reparse point, pembatalan IRP, unload driver, dan jalur error tanpa kebocoran.

### Rust di kernel

- Microsoft menyediakan `windows-drivers-rs` (`wdk-sys`, `wdk`, `wdk-alloc`, `wdk-panic`, `cargo-wdk`) untuk WDM/KMDF; proyeknya masih berkembang.
- **Belum diverifikasi:** apakah binding untuk header file system (`ntifs.h`: Cache Manager, `FsRtl*`) sudah ada dan stabil. Bila belum, binding dibuat sendiri (`bindgen`), menambah pekerjaan dan risiko.
- Hampir semua panggilan kernel tetap `unsafe`. Tidak boleh ada `panic!` (BSOD), alokasi yang bisa gagal harus ditangani.

### Aturan referensi dan lisensi (penting untuk tujuan MIT)

- **Rujukan utama:** dokumentasi driver Microsoft dan contoh di repo Windows-driver-samples (cek lisensi tiap sampel sebelum meniru strukturnya).
- **WinFsp sebagai referensi hanya secara perilaku (black-box):** dokumentasi publik dan API DLL yang terdokumentasi, mengamati perilakunya, dan menjalankan suite uji untuk membandingkan hasil. Itu boleh dan murah.
- **Membaca kode sumber WinFsp (GPLv3) lalu menulis ulang adalah risiko turunan.** Hasilnya bisa dianggap karya turunan GPL sehingga tidak bisa dirilis MIT. Jika sumbernya ingin dibaca, perlu clean-room sungguhan: satu orang/tim membaca dan menulis spesifikasi perilaku tanpa kode, orang/tim lain yang tidak pernah melihat sumbernya yang mengimplementasikan; atau terima bahwa driver akan GPL. Struct, nilai IOCTL, dan nama internal tidak boleh disalin dari header GPL mereka.
- Ini analisis teknis, bukan nasihat hukum; konsultasikan ke ahli hukum lisensi sebelum menulis driver.

### Studi kelayakan (go / no-go) sebelum komitmen penuh

1. Driver Rust paling sederhana (`DriverEntry`, device, unload) dibangun, ditandatangani test, dan dimuat di VM.
2. Periksa binding header file system yang dibutuhkan (`ntifs`, `FsRtl`, Cache Manager).
3. Volume baca-saja dengan satu file tetap, tanpa user-mode; pastikan Explorer dan `type` bisa membacanya.
4. Antrean IRP ke service user-mode.
5. Jika langkah 1-3 berjalan lancar dalam beberapa minggu, lanjut; jika tidak, kembali ke Cloud Files + minifilter kecil.

Catatan 2026-10-11: gagasan ini hanya disimpan sebagai ide; tidak dibuat plan atau dikerjakan agent kecuali pengguna meminta kelak.

### Lingkungan, signing, dan keamanan

- Build dan uji di Windows dengan WDK dan LLVM; uji hanya di VM dengan snapshot dan WinDbg, bukan di mesin kerja. Sesi Claude Code di Linux/cloud tidak bisa membangun atau menguji driver.
- Alat uji: Driver Verifier, uji I/O WDK/HLK (IFS), serta rencana uji ketangguhan di atas.
- Signing: test signing untuk VM; produksi lewat Microsoft Partner Center (sertifikat EV, attestation/WHQL). Cek persyaratan terbaru.
- Driver adalah permukaan eskalasi hak akses: review keamanan dan fuzzing input IOCTL.
- Perkiraan waktu: prototipe mount dan baca file dalam hitungan minggu sampai beberapa bulan; driver yang aman untuk pelanggan umumnya lebih dari satu tahun.

### Penempatan kode

- Driver Rust dianjurkan di repo terpisah berlisensi MIT (toolchain berbeda dari .NET). Kontrak, server, dan sync engine tetap di engine em-system sesuai aturan library bersama.

### Pertanyaan terbuka tambahan

- Jalur jangka pendek selama driver belum siap: Cloud Files saja, atau WinFsp opsional? (kecenderungan saat ini: Cloud Files).
- ~~Cara memakai WinFsp sebagai referensi~~ diputuskan 2026-10-11: hanya dokumentasi publik dan pengamatan perilaku; lihat bagian "Protokol dan antarmuka sendiri".
- Apakah service user-mode ditulis dengan Rust (sejalan dengan driver) atau C# (sejalan dengan engine)?
- Siapa yang menanggung akun Partner Center dan sertifikat EV, dan bagaimana alur rilis driver.

## Protokol dan antarmuka sendiri (keputusan pengguna, 2026-10-11)

- **Tidak perlu kompatibel dengan WinFsp.** Antarmuka antara driver kernel dan service user-mode (format IOCTL/antrean, struktur pesan, nama operasi) dirancang sendiri sesuai kebutuhan Windows dan produk ini, bukan mengikuti format WinFsp. Dengan begitu tidak ada antarmuka GPL yang ikut tersalin, desain bisa lebih sederhana, dan produk tidak terikat versi WinFsp. Aplikasi yang memakai WinFsp (rclone, SSHFS-Win, dan sebagainya) tidak akan otomatis berjalan di atas driver ini; kompatibilitas, bila kelak diperlukan, diputuskan terpisah dan dengan pertimbangan hukum terpisah.
- **"Spesifikasi" WinFsp berarti dokumentasi publik**, bukan kode sumber: dokumentasi dan catatan desain di situs resminya (semantik file system dan perbedaannya dengan NTFS), ditambah pengamatan perilaku. Dokumentasi Microsoft (IFS, contoh di Windows-driver-samples) tetap rujukan utama. Dilarang menyalin atau menerjemahkan kode sumber, struct, nilai IOCTL, atau nama fungsi internal WinFsp.
- **Langkah penyusunan:** (1) daftar operasi IRP yang wajib didukung (Create, Read, Write, QueryDirectory, Query/SetInformation, Cleanup/Close, Lock, FlushBuffers, dan seterusnya) dari dokumentasi Microsoft; (2) spesifikasi perilaku buatan sendiri, dengan penanda bagian yang bersumber dari dokumen publik; (3) rancangan protokol driver-service; (4) studi kelayakan.
- **Prinsip rancangan protokol (usulan, belum final):** versi protokol di setiap pesan dan negosiasi versi saat service terhubung; pesan berbingkai dengan panjang yang divalidasi ketat di sisi driver; semua input dari user-mode dianggap tidak tepercaya (validasi, batas ukuran, fuzzing); permintaan punya ID dan bisa dibatalkan (pembatalan IRP); batas waktu bila service tidak menjawab; perilaku jelas saat service mati (driver menjawab error, tidak menggantung); hanya operasi yang dibutuhkan produk drive cloud yang diimplementasikan lebih dulu.

## Driver tanpa signing produksi (keputusan pengguna, 2026-10-11)

- Driver dijalankan **tanpa tanda tangan Microsoft** (unsigned / test signing) untuk pengembangan dan pemakaian internal. Tidak perlu akun Partner Center atau sertifikat EV pada tahap ini.
- Cara kerja (sepengetahuan saat ini; verifikasi pada versi Windows yang dipakai): Windows 10/11 menolak memuat driver kernel tanpa tanda tangan Microsoft kecuali mesin berada di **test signing mode** (`bcdedit /set testsigning on`, perlu reboot, driver ditandatangani sertifikat uji sendiri). Windows menampilkan watermark "Test Mode".
- Batasan yang perlu diketahui:
  - Pada mesin UEFI dengan Secure Boot, test signing umumnya hanya bisa dinyalakan setelah Secure Boot dimatikan. Mengubah konfigurasi boot dapat memicu permintaan recovery key BitLocker.
  - Memory Integrity (HVCI), Smart App Control, atau kebijakan perusahaan/EDR dapat memblokir driver atau menandainya mencurigakan.
  - Mode ini cocok untuk VM pengembangan dan mesin sendiri; **tidak cocok untuk didistribusikan ke pelanggan atau karyawan umum**. Pemakaian luas tetap membutuhkan signing produksi (Partner Center, sertifikat EV, attestation/WHQL).
- Konsekuensi untuk produk: selama driver unsigned, produk yang dibagikan ke pihak lain tetap memakai jalur jangka pendek (Cloud Files) atau ditunda sampai signing diurus.
