# Penyimpanan log engine

- **Tanggal:** 2026-10-12
- **Status:** diskusi; belum ada kode
- **Plan turunan:** belum ada
- **Catatan terkait:** [user-editor-activity.md](user-editor-activity.md) (butuh jejak siapa mengubah akun),
  [registry-purge-gc.md](registry-purge-gc.md) (audit hapus image), [stream-dua-arah.md](stream-dua-arah.md)
  (calon jalur tail log live), [status-bar.md](status-bar.md)

Catatan ini adalah usulan rancangan, bukan perintah kerja. Agent tidak mengeksekusinya sebelum dijadikan plan
di `plan/unexecuted/`.

## Keadaan sekarang

- `Em.Api` hanya menulis log ke **console** (`AddSimpleConsole` di `EmApp`, level diatur `appsettings.json`).
  Begitu proses berhenti atau container diganti, log hilang; di Docker yang tersisa hanya `docker logs`
  selama container masih ada.
- `RequestTrace` (kategori tetap `Em.Api.Request`) mencatat IN/START/DONE per request dengan id pendek,
  alamat, dan identitas pemanggil. Modul menulis lewat `ServicesBase.Logger`.
- Kejadian sensitif hanya tercatat sebagai baris log biasa. Contoh di `EmApp`: impersonasi debug token
  ("Until an audit system exists, this log line is the only trace..."). Perubahan role/profil user belum
  dicatat dengan pelakunya. Audit yang sudah ada hanya milik fitur tertentu (mis. audit NuPak, object
  storage).
- Client WPF/MAUI tidak menyimpan log sama sekali, dan belum punya `DispatcherUnhandledException`; exception
  yang lolos membuat proses berhenti tanpa jejak.

## Usulan: tiga jenis log yang dipisah

Kata "log" di sini mencakup tiga kebutuhan yang berbeda pemilik, umur, dan pembacanya. Menggabungkannya ke
satu tempat membuat salah satunya buruk: audit jadi bisa dihapus retensi, atau log operasional membanjiri
database.

| Jenis | Isi | Penulis | Tempat | Umur |
|---|---|---|---|---|
| **A. Log operasional server** | Semua `ILogger` (request trace, error, warning modul) | Otomatis, lewat provider | File bergulir di disk server | Pendek, retensi hari/ukuran |
| **B. Audit** | Siapa melakukan apa pada data/hak (login, impersonasi, ubah role, hapus) | Eksplisit oleh engine/modul | Tabel database | Panjang, tidak bisa dihapus dari UI |
| **C. Log client** | Error dan exception di aplikasi WPF/MAUI | Otomatis di client | File lokal di PC pengguna | Pendek, opsional dikirim ke server |

### A. Log operasional server (file)

- **Provider `ILogger` milik engine** di `Em.Api.Core`, ditambahkan di sebelah console (console tetap ada untuk
  Docker dan debugging). Penulisan lewat antrean `Channel` dan satu writer di background, supaya request
  tidak menunggu disk.
- **Format JSON Lines** (satu objek per baris): `ts`, `level`, `category`, `message`, `exception`, `requestId`
  (id `RequestTrace`), `user`, `props` (nilai template terstruktur). Mudah dibaca mesin, tetap bisa di-grep.
- **Bergulir** per hari dan per ukuran (`em-api-20261012.log`, `em-api-20261012.1.log`), dihapus otomatis
  setelah N hari atau total melewati batas MB.
- **Lokasi** di `EmApiConfig.Storage` seperti path lain, bawaan `./data/logs`; di Compose dipasang ke volume.
- **Konfigurasi** di `emapi-config.json`, contoh:

  ```json
  "storage": { "logPath": "./data/logs" },
  "log": { "file": { "enabled": true, "minLevel": "Information", "retentionDays": 14, "maxFileMb": 50, "maxTotalMb": 1024 } }
  ```

  Filter per kategori tetap dari `appsettings.json` (`Logging:LogLevel`), sehingga `Em.Api.Request` bisa
  dimatikan untuk file tanpa menyentuh yang lain.
- **Correlation:** `RequestTrace` membuka `BeginScope` berisi id request dan user, supaya baris log modul di
  dalam request itu ikut membawa id yang sama dengan baris IN/DONE.
- **Data rahasia:** token, password, isi body, dan connection string tidak pernah ditulis. Aturan ini sudah
  dijaga `RequestTrace`; provider tidak menambah apa pun di luar yang dikirim ke `ILogger`.

### B. Audit (database)

- Tabel inti baru, mis. `ta_AuditLog`: `Id`, `Time` (UTC), `UserId`, `Account`, `ActingAs` (bila
  impersonasi), `Source` (alamat), `Action` (kunci tetap, mis. `user.role.change`), `Target` (jenis + id),
  `Detail` (JSON kecil: nilai lama/baru), `RequestId`.
- Ditulis **eksplisit** lewat service engine, mis. `IAuditWriter.Write(action, target, detail)` yang tersedia di
  `ServicesBase`; bukan diturunkan dari `ILogger`, agar audit tidak ikut hilang saat level log diturunkan.
- Penulis pertama dari engine: login berhasil/gagal, impersonasi debug token, ubah role/hak/profil user,
  hapus di registry/object storage. Audit fitur yang sudah ada (NuPak, object storage) tetap di tabelnya
  sendiri atau dipindah belakangan (pertanyaan terbuka).
- Hanya bisa dibaca dengan claim khusus; tidak ada aksi hapus dari UI. Pembersihan hanya lewat job retensi
  dengan batas panjang yang dikonfigurasi.

### C. Log client (WPF/MAUI)

- File lokal di `%LocalAppData%\<ApplicationName>\Logs` (nama mengikuti aplikasi host, sejalan dengan aturan
  `EmApp.BaseRegKey`), format dan pergulirannya sama dengan A.
- Engine memasang handler `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`, dan
  `AppDomain.UnhandledException` yang menulis ke log sebelum menampilkan pesan.
- Opsional: tombol "Kirim log ke admin" yang mengunggah file terakhir ke server (lewat object storage).
  Tidak dikirim otomatis.

### Membaca log

- **Server (A):** action admin `GetLogs` dengan filter waktu, level, kategori, request id, teks; membaca file
  JSONL dari belakang dengan batas baris. Layar WPF "Log Viewer" di menu admin, memakai claim sendiri
  (mis. `system.log.read`). Tail live menyusul bila engine stream dua arah sudah ada.
- **Audit (B):** layar "Audit" terpisah dengan filter user, aksi, target, dan rentang waktu; bisa juga
  ditampilkan sebagai tab di User Editor (kebutuhan `user-editor-activity.md`).
- **Client (C):** tombol "Buka folder log" di menu bantuan.

## Keputusan yang sudah jelas

Belum ada; semua di atas masih usulan.

## Pertanyaan terbuka

1. **Pustaka atau buatan sendiri** untuk A dan C: provider kecil milik engine (tanpa dependensi, cukup untuk
   file JSONL bergulir) atau Serilog (`Serilog.Extensions.Hosting` + `Serilog.Sinks.File`, MIT, lebih lengkap
   tetapi menambah dependensi paket NuGet engine)?
2. **Cakupan tahap pertama:** A saja (paling cepat dan menyelesaikan "log hilang"), atau A+B sekaligus karena
   impersonasi dan perubahan user sudah menunggu audit?
3. **Log operasional ke database juga?** Usulan di atas menolak (volume tinggi, beban DB). Alternatifnya
   hanya `Warning` ke atas yang disalin ke tabel agar bisa dicari dari UI tanpa membaca file.
4. **Multi-instance:** bila `Em.Api` berjalan lebih dari satu replika, Log Viewer membaca file instance mana?
   Cukup "instance yang menjawab request" untuk tahap awal, atau perlu sink terpusat (Seq/Loki/OpenTelemetry)?
5. **Audit fitur lama** (NuPak, object storage): disatukan ke `ta_AuditLog` atau dibiarkan di tabel masing-masing
   dengan layar audit yang bisa menggabungkan?
6. **Retensi bawaan:** 14 hari / 1 GB untuk A dan 1 tahun untuk B masuk akal?
7. **Log client dikirim ke server:** perlu atau cukup dibaca lokal?
