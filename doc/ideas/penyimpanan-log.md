# Penyimpanan log engine

- **Tanggal:** 2026-10-12
- **Status:** dieksekusi (2026-10-12; sebelumnya jadi plan 2026-10-12, atas permintaan pengguna; pertanyaan terbuka yang tersisa memakai usulan
  bawaan di §2 plan). Arah (pengguna 2026-10-12): satu log engine terpusat untuk audit data dan telemetry sistem
- **Plan turunan:** [plan/executed/penyimpanan-log.md](../../plan/executed/penyimpanan-log.md) (dieksekusi
  2026-10-12; panduan [doc/engine/engine-log.md](../engine/engine-log.md))
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

Keputusan pengguna 2026-10-12:

1. Dibangun **log engine** yang mendukung **audit data** (perubahan data entitas, termasuk riwayat dokumen
   ERP) dan **telemetry sistem**.
2. Semua log **terpusat**: banyak aplikasi mengirim ke satu log server dan bisa dicari di satu tempat.
3. Log bisa **ditarik oleh modul** (API baca dan feed perubahan, lihat bagian "Log bisa ditarik oleh modul").
4. Ada **Log Manager** untuk membaca audit dokumen sampai telemetry seperti error (lihat bagian "Log Manager").
5. **Log server adalah engine `Em.Api` yang dijalankan** dengan fitur log server dinyalakan, bukan produk
   atau proses terpisah (lihat bagian "Log server = Em.Api").
6. **Database store log diatur dari Log Manager:** pilih database internal (yang dipakai API) atau koneksi baru
   dengan provider MSSQL/MySQL/PostgreSQL dan credential, lalu tombol **Prepare** membangun database-nya
   (lihat bagian "Database store log").
7. **Satu request menyimpan ke dua database:** developer modul yang menentukan entry audit ditulis ke database
   mana (lihat bagian "Audit ke database mana").
8. **Retensi harus bisa di-setup** (lihat bagian "Setup retensi").
9. **Topologi dasar = satu instalasi:** setiap `Em.Api` (mis. `osha.com/api`) adalah log server untuk dirinya dan
   client-nya; log masuk lewat endpoint logger di API itu; database store diatur di Log Manager (internal atau
   database lain). Lihat bagian "Contoh OSHA: satu instalasi".
10. **Belum memakai protokol OTLP.** Yang dibangun: service log internal lewat `ILogger` di server dan di UI;
    kiriman dari UI lewat action `/api`. Lihat bagian "Keputusan: tanpa OTLP dulu".
11. **Salinan lokal dikirim lalu dihapus:** setelah login, salinan lokal dikirim di background ke server lalu
    dihapus; bila pengiriman gagal, tetap disimpan lokal untuk dicoba lagi. Lihat bagian yang sama.
12. **OTLP nanti sebagai wrapper** di atas ingest internal, hanya bila ada aplikasi lain (non-em-system) yang
    ingin menumpang log di emsys. Lihat bagian "OTLP sebagai wrapper".

Rincian lain di catatan ini masih usulan.

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

## Diskusi 2026-10-12: log engine multi-app dan telemetry ala Seq

Arah dari pengguna: bukan sekadar tempat simpan log `Em.Api`, tetapi **log engine yang bisa dipakai banyak
aplikasi**, dan bila memungkinkan menjadi **telemetry seperti Seq** (kumpulkan, cari, tail live, alert).
Bagian A dan C di atas menjadi bagian dari engine ini; audit (B) tetap terpisah di database karena sifatnya
catatan bisnis, bukan telemetry.

### Usulan arsitektur

```
App (Em.Api / WPF / MAUI / app lain)        Log server (modul Em.Api.Core)        Pembaca
  ILogger + Activity                          POST /v1/logs, /v1/traces (OTLP)       Log Viewer WPF
  -> enrich (app, versi, instance, user)  ->  auth API key per app               ->  query + filter
  -> buffer disk lokal (tahan offline)        simpan per hari, retensi per app       tail live (stream)
  -> kirim batch OTLP/HTTP                    indeks: waktu, app, level, traceId     alert -> SMTP engine
```

1. **Kontrak kabel: OpenTelemetry (OTLP/HTTP)**, bukan format buatan sendiri. Akibatnya:
   - aplikasi apa pun (bukan hanya .NET/em-system) bisa mengirim log ke server kita;
   - SDK client kita bisa diarahkan ke **Seq asli** (Seq menerima OTLP; cek versi dan lisensi terbaru) atau
     backend lain (Grafana/Loki, SigNoz, OpenObserve) **tanpa ubah kode**, cukup ganti endpoint;
   - membangun server sendiri menjadi pilihan yang bisa ditunda atau dibatalkan, bukan prasyarat.
2. **SDK client (library engine, mis. `Em.Telemetry`)** untuk semua host em-system:
   - satu panggilan di host (mis. `AddEmTelemetry(...)`) memasang provider `ILogger` + `ActivitySource`;
   - enrichment otomatis: `app` (ApplicationName host), versi, instance/mesin, environment, user aktif,
     `traceId`/`spanId`;
   - **buffer di disk lokal** lalu kirim per batch dengan retry. Penting untuk client WPF di laptop yang
     sering offline; exporter OTel standar hanya menyimpan di memori;
   - tetap menulis file JSONL lokal (bagian A/C) sebagai cadangan bila server tidak terjangkau lama.
3. **Trace lintas client-server:** WPF/MAUI mengirim header `traceparent` pada setiap action, sehingga satu
   klik di client dan request di `Em.Api` (yang sudah punya `RequestTrace`) tampil sebagai satu rangkaian.
4. **Log server sebagai modul engine** di `Em.Api.Core`, aktif hanya bila host menyalakannya (pola sama dengan
   registry/NuPak/object storage):
   - endpoint penerima OTLP; tiap aplikasi pengirim punya API key sendiri (memakai mekanisme robot/token
     yang sudah ada), kuota dan retensi per app;
   - penyimpanan: tabel SQL Server per hari (partisi atau tabel bergulir) dengan kolom tetap (waktu, app,
     level, kategori, pesan, traceId) + properti JSON; columnstore untuk volume besar. Alternatif: segmen
     file + indeks sendiri. Pilihan bergantung volume (pertanyaan terbuka);
   - query sederhana dulu (filter kolom + teks + properti), bahasa query ala Seq belakangan.
5. **Pembaca:** layar WPF "Log Viewer" (filter, detail event, buka seluruh trace, tail live lewat
   [stream-dua-arah.md](stream-dua-arah.md)), "signal"/query tersimpan, dan alert sederhana (level/hitungan
   per jendela waktu) yang mengirim email lewat engine SMTP.

### Tahapan (usulan)

1. SDK client: enrichment, file JSONL lokal, buffer disk, export OTLP. Diuji dengan Seq lokal (container)
   sebagai penerima, tanpa server sendiri.
2. Propagasi `traceparent` WPF/MAUI ke `Em.Api`.
3. Log server modul engine: penerima OTLP logs, penyimpanan, retensi, API key per app.
4. Log Viewer WPF + tail live.
5. Traces di server, signal/alert, metrics (paling akhir; metrics bisa tetap diserahkan ke Prometheus).

### Pertanyaan terbuka (diskusi multi-app)

1. **"Multi app" itu siapa:** hanya aplikasi berbasis em-system (Em.Api, WPF, MAUI, OshaApp, EmPorium), atau
   juga aplikasi lain/non-.NET? Menentukan seberapa penting OTLP vs format sendiri.
2. **Topologi:** satu log server pusat untuk semua aplikasi dan pelanggan, atau tiap instalasi pelanggan punya
   log server sendiri (self-hosted)? Bila pusat: perlu pemisahan tenant dan hak baca per app.
3. **Bangun vs pakai:** cukup SDK + Seq/backend yang sudah ada, atau memang ingin server sendiri (alasan: tanpa
   lisensi pihak ketiga, satu login dan hak akses dengan em-system, terintegrasi di WPF)?
4. **Volume:** perkiraan event per hari per app. Menentukan apakah SQL Server cukup atau perlu storage khusus.
5. **Cakupan telemetry:** log saja, log + trace, atau juga metrics?
6. **Log client WPF ke server:** dikirim otomatis (dengan buffer offline) atau hanya atas persetujuan
   pengguna? Ada pertimbangan privasi (nama user, nama mesin, isi pesan).

## Definisi jenis log (usulan 2026-10-12)

Permintaan pengguna: definisikan jenis log dulu sebelum membahas penyimpanan dan server. Jenis dibedakan
menurut **tujuan dan pembacanya**, bukan menurut level. Level (Trace, Debug, Information, Warning, Error,
Critical) adalah atribut di dalam setiap jenis.

### Dua keluarga

- **Telemetry**: boleh hilang sebagian (sampling, buffer penuh, server mati), dikirim asinkron lewat pipeline
  log engine (OTLP), retensi pendek. Dipakai untuk mendiagnosis sistem.
- **Catatan (record)**: tidak boleh hilang, ditulis dalam transaksi yang sama dengan perubahan datanya, disimpan
  di database aplikasi, retensi panjang. Dipakai sebagai bukti dan riwayat.

Pemisahan ini menentukan jalurnya: telemetry tidak pernah menjadi satu-satunya tempat catatan, dan catatan
tidak dikirim lewat pipeline yang boleh membuang data. Catatan boleh **disalin** ke telemetry untuk dicari
bersama, tetapi sumber kebenarannya tetap di database.

### Daftar jenis

| # | Jenis | Keluarga | Contoh | Penulis | Skema | Pembaca | Retensi (usulan) |
|---|---|---|---|---|---|---|---|
| 1 | **Diagnostik** (log aplikasi) | Telemetry | "Cache bucket X dimuat ulang", warning modul | Otomatis, `ILogger` | Bebas: message template + properti | Developer, ops | 7-30 hari |
| 2 | **Error / crash** | Telemetry | Exception tak tertangani, crash client WPF | Otomatis, handler engine | Diagnostik + stack, versi, OS, layar aktif, fingerprint untuk pengelompokan | Developer | 30-90 hari |
| 3 | **Akses** (request) | Telemetry | IN/START/DONE dari `RequestTrace`: route, pemanggil, status, durasi | Otomatis, engine | Tetap | Ops, keamanan | 30-90 hari |
| 4 | **Trace** (span) | Telemetry | Klik di WPF -> action `Em.Api` -> query DB, dengan durasi tiap langkah | Otomatis, `Activity` | Tetap (OTel span) | Developer | 7-14 hari |
| 5 | **Metrik** | Telemetry | Jumlah request/menit, latensi p95, ukuran antrean | Otomatis + modul | Agregat angka | Ops, alert | Ringkasan panjang |
| 6 | **Keamanan** | Catatan | Login gagal/berhasil, lockout, token ditolak, impersonasi debug token, akses ditolak | Eksplisit, engine | Tetap | Admin, keamanan | 1 tahun+ |
| 7 | **Audit** | Catatan | Ubah role/hak user, hapus image registry, ubah kebijakan bucket | Eksplisit, engine + modul | Tetap: pelaku, aksi, target, nilai lama/baru | Admin, auditor | 1 tahun+ / tak terbatas |
| 8 | **Proses / job** | Catatan | Output satu run publish, maintenance object storage, task bisnis | Eksplisit, runner | Per run: status, mulai/selesai, baris output | Pengguna yang menjalankan, admin | Per jumlah run / 90 hari |
| 9 | **Komunikasi keluar** | Catatan | Email terkirim/gagal (SMTP), webhook/HTTP ke pihak luar | Eksplisit, engine | Tetap: tujuan, subjek, status, percobaan | Admin, support | 90 hari-1 tahun |
| 10 | **Aktivitas pengguna** (opsional) | Telemetry | Buka layar, pakai fitur | Otomatis, navigasi UI | Tetap | Produk | 30-90 hari |
| 11 | **Riwayat dokumen** | Catatan | Invoice dibuat, diubah, di-post, dicetak, di-void | Otomatis (diff) + eksplisit | Tetap: dokumen, kejadian, diff field | Pengguna bisnis, auditor | Seumur dokumen (lihat bagian "Log dokumen") |

Catatan per jenis:

- **2 vs 1:** error tetap event diagnostik level Error/Critical, tetapi diperlakukan khusus: tidak di-sampling,
  diberi fingerprint (jenis exception + frame teratas) agar error yang sama terhitung satu masalah, dan bisa
  memicu alert.
- **3 vs 6:** akses mencatat semua request apa adanya (boleh hilang); keamanan hanya kejadian yang relevan dan
  wajib tersimpan. Request yang ditolak gate muncul di keduanya.
- **6 vs 7:** keduanya catatan; bisa satu tabel dengan kolom `Kind` (security/audit) atau dua tabel. Keamanan
  sering tanpa target data (login gagal belum punya user), audit selalu punya target.
- **8:** inilah "log" yang dilihat pengguna akhir (mis. jendela output publish). Terikat pada id run, bukan aliran
  global; baris diagnostik selama run boleh disalin ke telemetry dengan `runId` sebagai properti.
- **10:** menyentuh privasi pengguna; hanya bila ada kebutuhan produk yang jelas.

### Amplop bersama (semua jenis)

Setiap event, apa pun jenisnya, membawa bidang yang sama agar bisa dicari bersama:

- **Kapan:** `timestamp` (UTC).
- **Dari mana:** `app` (ApplicationName host), `appVersion`, `instance` (mesin/container), `environment`
  (dev/staging/prod), `tenant` (bila server log dipakai banyak pelanggan).
- **Apa:** `kind` (jenis di atas), `level`, `category` (logger/modul), `messageTemplate`, `message` (sudah
  dirender), `properties` (JSON), `exception` (bila ada).
- **Rangkaian:** `traceId`, `spanId`, `requestId` (id `RequestTrace`), `runId` (jenis 8).
- **Siapa:** `userId`, `account`, `actingAs` (impersonasi), `clientAddress`.

Aturan data rahasia berlaku untuk semua jenis: password, token, API key, connection string, dan isi body
request tidak pernah masuk event; nilai lama/baru di audit untuk field rahasia ditulis sebagai "diubah" saja.

### Pertanyaan terbuka (jenis log)

1. Apakah sebelas jenis ini lengkap, atau ada jenis yang dibutuhkan aplikasi turunan (OshaApp, EmPorium)
   yang belum tercakup?
2. Jenis mana yang masuk **tahap pertama**? Usulan: 1, 2, 3 (telemetry dasar) + 6, 7 (sudah ada kebutuhan
   nyata: impersonasi dan perubahan user).
3. Keamanan dan audit: satu tabel dengan `Kind` atau dua tabel?
4. Aktivitas pengguna (10): masuk cakupan atau dibuang?
5. Apakah metrik (5) bagian dari engine ini, atau diserahkan ke Prometheus/OTel collector?

## Log dokumen (usulan 2026-10-12)

Permintaan pengguna: karena em-system dipakai untuk ERP, perlu **log terhadap dokumen**, misalnya user membuat
invoice baru lalu mengubahnya. Ini menjadi jenis **11. Riwayat dokumen** di keluarga *catatan*. Jenis ini
dipisah dari audit (7). Audit menjawab "apa yang dilakukan user/admin pada sistem". Riwayat dokumen menjawab
"apa yang terjadi pada dokumen ini", dibaca dari layar dokumen itu sendiri oleh pengguna bisnis.

### Identitas dokumen

Pakai identitas yang sudah dipakai engine approval: `docType` dari daftar jenis dokumen aplikasi dan kunci
dokumen berupa `record` bertanda `[KeyPart(n)]` (lihat `doc/engine/engine-approval.md`). Tambahan:

- `docNo`: nomor dokumen yang dibaca manusia (mis. `INV/2026/10/0012`), disimpan apa adanya saat kejadian.
- Opsional `company`/`branch` bila modul ERP memakai pemisahan itu, untuk filter dan hak baca.

### Jenis kejadian

| Kejadian | Contoh | Isi yang dicatat |
|---|---|---|
| `created` | Invoice baru dibuat | Snapshot awal (header + baris) |
| `updated` | Ubah customer, qty baris 2, tambah baris | Diff per field: header dan baris (baris ditambah/dihapus/diubah) |
| `status` | draft -> submitted -> approved -> posted | Status lama/baru, alasan bila ada |
| `approval` | Langkah approval ditandatangani/ditolak | Dari engine approval: langkah, penanda tangan, komentar |
| `voided` / `cancelled` / `deleted` | Invoice dibatalkan | Alasan **wajib**, snapshot terakhir |
| `output` | Dicetak, diekspor PDF, dikirim email | Format, tujuan, cetakan ke-berapa, snapshot saat itu |
| `attachment` | Lampiran ditambah/dihapus | Nama berkas, id object storage |
| `comment` | Catatan bebas user | Teks |
| `linked` | Invoice dibuat dari Sales Order / DO | Dokumen asal (docType + key + docNo) |

### Bentuk data

- Header kejadian: waktu (UTC), `docType`, kunci, `docNo`, kejadian, user, `actingAs`, alamat, `requestId`,
  alasan/komentar.
- Diff field (tabel anak): bagian (`header` atau nama koleksi baris + kunci baris), field, nilai lama, nilai
  baru, **dan teks tampilan lama/baru**. Teks tampilan disimpan karena master bisa berubah kemudian. Contoh:
  customer `C001` ditulis juga sebagai "PT Maju Jaya" saat itu.
- Snapshot JSON penuh hanya pada titik penting (`created`, `posted`, `output`, `voided`), supaya "isi
  invoice yang dicetak dulu" bisa ditampilkan lagi persis, tanpa menyimpan snapshot setiap edit.
- Field rahasia atau berisik (`ustamp`, `datestamp`, kolom teknis) dikecualikan lewat deklarasi.

### Cara menulis (usulan)

Gabungan dua cara:

1. **Diff otomatis** untuk `created`/`updated`/`deleted`: modul mendaftarkan jenis dokumen beserta entitas
   header dan barisnya (mis. `AddDocumentLog<InvoiceKey>("Invoice", d => d.Header<Invoice>().Lines<InvoiceLine>(...))`).
   Interceptor `SaveChanges` EF Core membaca `ChangeTracker` dan menulis diff **dalam transaksi yang sama**.
   Modul tidak perlu menulis kode log untuk edit biasa dan tidak ada perubahan yang terlewat.
2. **Kejadian bisnis eksplisit** untuk yang bukan sekadar perubahan field: `posted`, `voided`, `output`,
   `comment`, `linked`. Ditulis lewat service, mis. `DocumentLog.Record(docType, key, "posted", reason)`.
   Engine approval otomatis menulis kejadian `approval`.

Alternatif yang dipertimbangkan: temporal table / CDC SQL Server. Keduanya memberi versi baris secara gratis,
tetapi tanpa "siapa dan kenapa", tanpa kejadian bisnis, dan sulit dirangkai menjadi satu dokumen
(header + baris). Bisa dipakai sebagai pelengkap, bukan pengganti.

### Membaca

- Control WPF generik **"Riwayat"** (timeline) yang bisa ditempel ke layar dokumen mana pun: daftar kejadian,
  klik untuk melihat diff, tombol "lihat versi saat itu" untuk snapshot.
- Hak baca mengikuti hak baca dokumennya: siapa yang boleh membuka invoice boleh melihat riwayatnya.
- Laporan lintas dokumen (mis. "semua invoice yang di-void bulan ini") lewat query di tabel yang sama.

### Retensi

Ikuti umur dokumennya, bukan retensi log. Dokumen pembukuan di Indonesia wajib disimpan 10 tahun (UU KUP
Pasal 28; verifikasi ke sumber resmi). Usulan: riwayat dokumen tidak pernah dihapus selama dokumennya ada,
dan pengarsipan ke storage lain dibahas terpisah bila volumenya besar.

### Pertanyaan terbuka (log dokumen)

1. Selain dokumen transaksi (invoice, SO, PO), apakah **master data** (customer, item, harga) juga perlu
   riwayat dengan mekanisme yang sama?
2. Diff otomatis lewat EF Core interceptor: apakah semua modul ERP menyimpan lewat EF Core, atau ada yang lewat
   stored procedure/SQL langsung (yang tidak terlihat oleh interceptor)?
3. Cetak ulang: perlu dicatat setiap kali dan ditandai "cetakan ke-n / copy"?
4. Satu tabel riwayat untuk semua jenis dokumen (dipartisi), atau tabel per modul?
5. Apakah user boleh menulis komentar bebas di riwayat, atau riwayat murni otomatis?

## Log bisa ditarik oleh modul (usulan 2026-10-12)

Permintaan pengguna: log engine bisa **dibaca (ditarik) oleh modul**. Contohnya modul invoice mengambil dari
log untuk melihat pergerakan datanya. Log jadi sumber data untuk modul, bukan hanya untuk dibaca admin.

### Batas: yang boleh dipakai logika bisnis

- **Hanya keluarga catatan** (riwayat dokumen, audit, keamanan, proses/job, komunikasi keluar) yang boleh
  dibaca modul untuk logika atau laporan bisnis. Isinya lengkap dan transaksional, jadi hasilnya bisa dipercaya.
- **Telemetry** (diagnostik, akses, trace) boleh hilang dan di-sampling, jadi **tidak boleh** menjadi dasar
  logika bisnis. Modul hanya boleh membacanya untuk tampilan diagnosis, dengan hak admin.
- Log bersifat **append-only**. Modul membaca dan menambah lewat API engine, tidak pernah mengubah atau
  menghapus baris log.

### API baca untuk modul (server)

Disediakan engine di `Em.Api.Core` dan tersedia dari service modul (mis. lewat `ServicesBase`):

| Kebutuhan | Bentuk (usulan) |
|---|---|
| Riwayat satu dokumen | `History.For("Invoice", key)`: daftar kejadian berurutan beserta diff |
| Isi dokumen pada suatu waktu | `History.VersionAt("Invoice", key, waktu)`: snapshot terdekat + diff sesudahnya, bisa dibaca sebagai type modul (`Invoice`) |
| Jejak satu field | `History.FieldTimeline("Invoice", key, "Lines[2].Qty")`: perubahan qty/harga dari waktu ke waktu |
| Lintas dokumen | `History.Query(filter)`: jenis dokumen, rentang waktu, kejadian, user, field yang berubah. Contoh: semua invoice yang di-void bulan ini, semua perubahan harga oleh user X |
| Rantai dokumen | `History.Chain("Invoice", key)`: dokumen asal dan turunan dari kejadian `linked` (SO -> DO -> Invoice -> Payment) |
| Feed perubahan | `History.Since(cursor, filter)`: kejadian sesudah nomor urut tertentu, untuk modul yang memproses perubahan secara berkala |

Hasil umum berbentuk generik (field, nilai lama/baru, teks tampilan). Engine menyediakan helper untuk
membaca snapshot dan diff sebagai type milik modul.

### Feed perubahan (cursor)

- Setiap kejadian catatan mendapat **nomor urut global yang naik terus** (`Seq`).
- Modul menyimpan cursor terakhirnya, lalu meminta "kejadian sesudah `Seq` N" untuk jenis dokumen/kejadian
  tertentu. Contoh pemakaian: modul akuntansi menarik invoice yang baru di-post untuk dibuat jurnalnya,
  modul laporan memperbarui ringkasan, integrasi mengirim ke sistem lain.
- Semantik: **at-least-once**. Pemroses harus idempoten (memakai `Seq` atau id kejadian sebagai kunci
  pemrosesan).
- Kursor disimpan engine per konsumen (nama konsumen didaftarkan modul), supaya pemrosesan bisa dilanjutkan
  setelah restart. Pemanggilan bisa dijadwal lewat runner background yang sudah ada, dan nanti didorong
  lewat stream dua arah tanpa mengubah kontraknya.
- Kejadian yang belum lewat batas retensi bisa diproses ulang dengan memundurkan cursor.

### Hak baca

- **Atas nama user** (dipanggil dari action): hasil disaring dengan hak user itu. User yang tidak boleh
  membuka invoice tidak mendapat riwayat invoice, termasuk lewat query lintas dokumen.
- **Atas nama sistem** (job background, feed): modul mendeklarasikan jenis dokumen yang dibacanya saat
  registrasi. Mis. modul akuntansi mendeklarasikan baca `Invoice` dan `Payment`. Membaca jenis dokumen yang
  tidak dideklarasikan ditolak engine, supaya ketergantungan antarmodul terlihat jelas di kode.

### Client

Control "Riwayat" di WPF dan layar laporan modul memakai action engine yang sama (`GetMeta_DocumentHistory`
dan sejenisnya), sehingga aturan hak baca berlaku sama di client dan di server.

### Pertanyaan terbuka (log ditarik modul)

1. "Pergerakan data" yang dimaksud lebih ke **riwayat perubahan satu dokumen**, **rantai dokumen**
   (SO -> DO -> Invoice), atau **pergerakan nilai** (mis. stok/saldo dari waktu ke waktu)? Yang terakhir
   biasanya lebih tepat sebagai tabel mutasi/ledger milik modul, bukan dibaca dari log.
2. Apakah feed perubahan (cursor) dibutuhkan sekarang, atau cukup query riwayat dulu?
3. Bolehkah modul membaca riwayat jenis dokumen milik modul lain, dengan syarat dideklarasikan?
4. Perlukah modul juga bisa membaca **telemetry** (mis. error milik modul invoice) untuk ditampilkan di layar
   modul, atau telemetry tetap khusus admin?

## Audit data dan telemetry terpusat (usulan 2026-10-12)

Keputusan pengguna: log engine mendukung **audit data** dan **telemetry sistem**, dan semuanya **terpusat**.
Bagian ini menyatukan usulan sebelumnya menjadi satu arsitektur.

### Audit data sebagai inti, riwayat dokumen di atasnya

- **Audit data** = catatan perubahan baris entitas apa pun yang didaftarkan (opt-in): master (customer, item,
  harga) maupun transaksi. Isinya: entitas, kunci, operasi (insert/update/delete), diff field (nilai lama/baru
  + teks tampilan), pelaku, `actingAs`, waktu, `requestId`, alasan.
- Perubahan dalam satu transaksi dikumpulkan menjadi satu **change set**. Simpan invoice yang mengubah header
  dan tiga baris tercatat sebagai satu change set, bukan empat kejadian lepas.
- **Riwayat dokumen** (jenis 11) adalah lapisan di atas audit data: change set dikelompokkan menurut dokumen
  induknya (`docType` + kunci), lalu ditambah kejadian bisnis (post, void, cetak, approval).
- Dengan begitu pertanyaan "master data perlu riwayat?" terjawab oleh mekanisme yang sama: cukup daftarkan
  entitasnya.

### Masalah: transaksional vs terpusat

Audit harus ditulis dalam transaksi yang sama dengan perubahan datanya. Log server pusat ada di mesin lain
dan bisa mati atau tidak terjangkau. Menulis langsung ke server pusat berarti salah satu dari dua hal buruk:
penyimpanan data gagal karena log server mati, atau audit hilang.

Usulan: **pola outbox**.

```
App (mis. ERP)                                             Log server pusat
  SaveChanges ----+--> tabel data                            /ingest/audit   (idempoten per id kejadian)
                  +--> tabel audit lokal (transaksi sama)    /v1/logs, /v1/traces (OTLP telemetry)
  ILogger/Activity --> buffer disk lokal                         |
                                                                 +--> store audit   (append-only, retensi panjang)
  Shipper background: kirim audit lokal (urut Seq)  ------>      +--> store telemetry (retensi pendek)
                      + telemetry dari buffer        ------>     +--> query, tail live, alert, Log Viewer
```

1. Audit ditulis ke **tabel audit lokal di database aplikasi**, di transaksi yang sama. Ini sumber kebenaran.
2. **Shipper** di background mengirim audit lokal ke log server pusat berurutan menurut `Seq`, dengan retry.
   Server menerima secara idempoten (id kejadian unik), jadi kiriman ganda tidak menggandakan data.
3. Telemetry lewat jalur terpisah (buffer disk lokal, OTLP), boleh hilang sebagian.
4. Bila log server pusat mati, aplikasi tetap jalan normal; audit menumpuk di outbox lokal dan terkirim saat
   server kembali.

Akibatnya:

- **Modul membaca audit dari database lokal** (cepat, konsisten dengan data, tetap jalan saat pusat mati).
- **Pencarian lintas aplikasi** (audit + telemetry dari semua app) dilakukan di log server pusat.
- Satu `traceId`/`requestId` menyambungkan audit dan telemetry: dari satu change set bisa dibuka trace dan
  log diagnostik request yang membuatnya, dan sebaliknya.

### Cara menangkap perubahan data

| Cara | Kelebihan | Kekurangan |
|---|---|---|
| **Interceptor EF Core** (`SaveChanges` + `ChangeTracker`) | Tahu pelaku, request, alasan; bisa menyimpan teks tampilan; mengelompokkan per dokumen | Tidak melihat perubahan lewat SQL langsung/stored procedure |
| **Trigger SQL Server** + `SESSION_CONTEXT` (engine mengisi user/request saat koneksi dibuka) | Menangkap semua perubahan, termasuk SQL langsung | Tanpa teks tampilan dan kejadian bisnis; trigger per tabel perlu digenerate dan dirawat; beban tulis lebih berat |
| Temporal table / CDC | Bawaan SQL Server | Tanpa "siapa dan kenapa"; sulit dirangkai per dokumen |

Usulan: **interceptor EF Core sebagai jalur utama**; trigger + `SESSION_CONTEXT` hanya untuk tabel yang memang
ditulis di luar EF Core, dan menulis ke tabel audit yang sama. `Em.Api.Core` sudah memakai interceptor EF
Core (`ApprovalDatabaseSwitch`), jadi polanya sudah ada.

### Bentuk penyimpanan audit (usulan awal)

- `ChangeSet`: `Seq` (naik terus), id unik, waktu UTC, app, user, `actingAs`, alamat, `requestId`, `traceId`,
  alasan, status kirim ke pusat.
- `Change`: change set, entitas, kunci, operasi, `docType` + kunci induk (bila bagian dokumen), diff JSON
  (field, lama, baru, teks tampilan lama/baru).
- `DocumentEvent`: kejadian bisnis (post, void, cetak, approval, linked, comment), merujuk ke change set bila
  ada.
- Append-only: akun database aplikasi idealnya hanya punya hak INSERT/SELECT pada tabel ini. Opsional hash
  berantai antar change set sebagai bukti tidak diubah.

### Log server pusat

- Modul engine di `Em.Api.Core` yang bisa dijalankan sebagai host tersendiri (mis. "Em Log Server") atau
  menumpang di `Em.Api` yang sudah ada.
- Dua store dengan aturan berbeda: **audit** (append-only, retensi panjang, tidak di-sampling) dan
  **telemetry** (retensi pendek, boleh sampling).
- Pengirim memakai API key per aplikasi (mekanisme robot/token yang sudah ada); setiap event bertanda
  app/instance/tenant.
- Telemetry tetap OTLP, jadi bagian telemetry bisa juga diarahkan ke Seq bila diperlukan. Audit tidak dikirim
  ke Seq, karena Seq tidak dirancang sebagai arsip catatan bisnis 10 tahun.

### Pertanyaan terbuka (audit data terpusat)

1. **Topologi pusat:** satu log server untuk semua aplikasi dan semua pelanggan, atau satu per instalasi
   pelanggan? Bila semua pelanggan: data audit bisnis pelanggan keluar dari servernya (perlu persetujuan dan
   pemisahan tenant).
2. **Salinan lokal:** audit di database aplikasi disimpan selamanya (modul tetap bisa membaca riwayat 10
   tahun) atau dipangkas setelah terkirim ke pusat (modul membaca riwayat lama dari pusat)?
3. **Penangkapan:** setuju interceptor EF Core sebagai jalur utama? Ada tabel ERP yang ditulis lewat stored
   procedure?
4. **Entitas yang diaudit:** opt-in per entitas (usulan) atau semua tabel otomatis dengan daftar pengecualian?
5. **Tahap pertama:** usulan urutan: (a) audit data lokal + API baca modul, (b) riwayat dokumen + control
   "Riwayat", (c) SDK telemetry + log server pusat (ingest, store, Log Viewer), (d) shipper audit ke pusat,
   (e) trace, alert, metrik.

## Log Manager (usulan 2026-10-12)

Permintaan pengguna: ada **Log Manager** yang bisa membaca semua data log, dari dokumen yang diaudit sampai
telemetry seperti error. Bentuknya mengikuti manager engine yang sudah ada (Object Storage Manager, NuGet
Manager): navigasi admin (mis. `admin.logmanager`, Administrative Tools) dengan tab `EmTabStrip`, dan editor
yang terbuka di tab baru untuk satu objek (seperti bucket editor).

### Sumber data

- Log Manager membaca dari **log server pusat**: semua aplikasi, audit dan telemetry di satu tempat. Pemilih
  **aplikasi/instance/environment** ada di bagian atas dan berlaku untuk semua tab.
- Audit di pusat bisa tertinggal dari database aplikasi selama shipper belum mengirim. Log Manager menampilkan
  posisi terakhir yang diterima per aplikasi dan tanda "tertunda" bila tertinggal.
- Control "Riwayat" di layar dokumen modul tetap membaca dari database lokal aplikasi (lihat bagian sebelumnya).

### Tab

| Tab | Isi |
|---|---|
| **Ringkasan** | Per aplikasi: jumlah error 24 jam terakhir, error baru, volume audit, antrean shipper yang belum terkirim, status ingest. Klik angka membuka tab terkait dengan filter terisi |
| **Audit data** | Cari perubahan: aplikasi, entitas/`docType`, kunci atau `docNo`, user, operasi, field, rentang waktu. Tabel change set; detail menampilkan diff (lama -> baru, dengan teks tampilan) per entitas dalam change set itu |
| **Dokumen** | Cari dokumen lalu buka **editor riwayat dokumen** di tab baru: timeline kejadian, diff per kejadian, "isi pada waktu itu", rantai dokumen (SO -> DO -> Invoice -> Payment) |
| **Error** | Error dikelompokkan per fingerprint: jumlah, pertama/terakhir terlihat, aplikasi, versi, jumlah user terdampak. Status masalah: baru, diketahui, selesai, diabaikan (error yang muncul lagi setelah "selesai" kembali menjadi baru). Detail: stack trace, contoh kejadian, environment |
| **Log** | Penjelajah semua event telemetry: level, kategori, teks, properti, rentang waktu. Mode **tail live**. Query tersimpan |
| **Trace** | Satu `traceId` sebagai waterfall: klik di client -> action API -> query DB, dengan durasi (tahap belakangan) |
| **Keamanan** | Login gagal/berhasil, lockout, token ditolak, impersonasi debug token |
| **Pengaturan** | Aplikasi pengirim dan API key-nya, retensi per aplikasi dan per jenis log, aturan alert (mis. error baru, lebih dari N error per 5 menit -> email lewat engine SMTP), konsumen feed perubahan beserta posisi cursor-nya |

### Saling tertaut

Semua event membawa `traceId`/`requestId`, jadi setiap tampilan bisa melompat ke yang lain:

- Dari **change set audit** -> "Lihat request": log, error, dan trace request yang membuat perubahan itu.
- Dari **error** -> "Lihat request": log di sekitarnya, user, layar/action yang dipanggil. Catatan: bila error
  membatalkan transaksi, tidak ada audit untuk request itu (datanya memang tidak berubah). Log Manager
  menampilkannya sebagai "transaksi dibatalkan", bukan "tidak ada data".
- Dari **dokumen** -> setiap kejadian di timeline bisa dibuka ke request-nya.
- Dari **user** -> semua audit dan error milik user itu dalam rentang waktu.

### Hak akses (usulan)

Isi audit adalah data bisnis (harga, nilai invoice), sedangkan error adalah data teknis. Pembacanya sering
berbeda (staf IT vs auditor/finance), jadi hak dipisah:

| Claim | Mengizinkan |
|---|---|
| `Log Manager Access` | Membuka Log Manager |
| `Log Telemetry Read` | Tab Ringkasan (bagian teknis), Error, Log, Trace |
| `Log Audit Read` | Tab Audit data dan Dokumen |
| `Log Security Read` | Tab Keamanan |
| `Log Manager Settings` | Tab Pengaturan: API key, retensi, alert, cursor |

Hak bisa dibatasi per aplikasi (mis. auditor hanya melihat audit aplikasi ERP A). Field yang dideklarasikan
rahasia tetap tampil sebagai "diubah" tanpa nilai, juga untuk admin.

### Pertanyaan terbuka (Log Manager)

1. Log Manager menjadi bagian aplikasi WPF yang sudah ada (menu admin, terhubung ke log server pusat), atau
   aplikasi tersendiri khusus log server?
2. Pembagian hak di atas sudah cukup, atau perlu pembatasan audit per jenis dokumen di Log Manager pusat juga?
   Hak per dokumen ada di masing-masing aplikasi, sedangkan pusat tidak mengenal role aplikasi itu.
3. Perlukah ekspor (CSV/Excel/PDF) dari tab Audit dan Dokumen untuk kebutuhan auditor?
4. Status masalah di tab Error: cukup status, atau perlu penugasan ke orang dan catatan penanganan?

## Log server = Em.Api (keputusan 2026-10-12)

Keputusan pengguna: log server memakai **engine API yang dijalankan**, yaitu host `Em.Api` biasa dengan fitur
log server dinyalakan. Tidak ada produk atau proses log server tersendiri.

### Bentuk

- Fitur didaftarkan seperti fitur engine lain di `Program.cs` host, mis. `builder.AddLogServer()`, dan bisa
  dimatikan lewat `"modules": { "logServer": false }` di `emapi-config.json` (pola yang sama dengan
  `objectStorage`).
- Sisi pengirim juga fitur engine: `builder.AddLogShipping(...)` di `Em.Api` (audit lokal + telemetry ke
  server log) dan pemasangan setara di host WPF/MAUI untuk telemetry client.
- Yang dipakai ulang dari engine: autentikasi robot/token untuk aplikasi pengirim, claim untuk hak Log
  Manager, `emapi-config.json`, runner background (retensi, alert), engine SMTP (email alert), stream dua arah
  (tail live, nanti), dan object storage (calon arsip segmen lama).
- Endpoint penerima OTLP (`/v1/logs`, `/v1/traces`) adalah endpoint HTTP mentah, bukan action, mengikuti pola
  endpoint registry dan NuPak yang sudah ada. Penerima audit (`/ingest/audit`) dan query Log Manager memakai
  action biasa.
- **Log Manager** adalah navigasi di aplikasi WPF yang sudah ada dan terhubung ke `Em.Api` yang menyalakan log
  server, seperti manager lain (menjawab pertanyaan terbuka Log Manager nomor 1).

### Topologi yang didukung kode yang sama

| Topologi | Susunan |
|---|---|
| **Satu instalasi** | `Em.Api` aplikasi juga menyalakan log server. Audit dan telemetry miliknya ditulis langsung ke store lokal, tanpa lewat HTTP |
| **Log server pusat** | Satu `Em.Api` khusus yang hanya menyalakan log server (tanpa modul bisnis). Beberapa `Em.Api` aplikasi dan client WPF mengirim ke sana |

Pindah dari topologi pertama ke kedua cukup lewat konfigurasi (alamat log server dan API key), tanpa ubah
kode modul.

### Hal yang perlu dijaga

- **Tidak boleh memakan ekornya sendiri:** log yang dihasilkan saat menerima log (request trace endpoint
  ingest, error ingest) ditulis langsung ke store lokal, tidak dikirim lewat HTTP ke dirinya sendiri, dan
  request trace endpoint ingest diturunkan ke level Debug supaya tidak membanjiri.
- **Database terpisah untuk store log:** volume telemetry dan audit jauh lebih besar daripada data inti.
  Usulan: connection string tersendiri di `emapi-config.json` (bawaan: database inti, cocok untuk instalasi
  kecil). Audit **lokal** milik aplikasi (outbox) tetap di database aplikasi karena harus satu transaksi
  dengan datanya.
- **Satu instance:** seperti object storage, tahap awal mengasumsikan satu instance log server per store.
- **Log server mati** tidak boleh mengganggu aplikasi pengirim: pengirim hanya menumpuk di outbox/buffer.

### Pertanyaan terbuka (log server = Em.Api)

1. Store log memakai database terpisah (usulan) atau cukup database inti?
2. Pada log server pusat, siapa yang login ke Log Manager: akun di `Em.Api` log server itu sendiri (terpisah dari
   akun aplikasi), atau perlu login tunggal dengan aplikasi asal?

## Database store log (keputusan 2026-10-12)

Keputusan pengguna: di Log Manager bisa dipilih apakah store log memakai **database internal** (yang dipakai
API) atau **koneksi baru**. Untuk koneksi baru ada pilihan **provider MSSQL/MySQL/PostgreSQL**, isian
credential, dan tombol **Prepare** untuk membangun database. Ini menjawab pertanyaan "database terpisah atau
database inti": keduanya bisa, dipilih operator.

### Yang sudah ada di engine

- `DatabaseProvider` sudah mengenal `MicrosoftSqlServer`, `MySql`, `PostgreSql` (`SetDbProvider`,
  `EM_DB_PROVIDER`).
- `AddExtraDbConn(name, connectionString, provider)` mendaftarkan koneksi bernama selain "Default"; modul
  memakai nama koneksi, bukan connection string.
- Pola **pengaturan terkelola** (`AddManagedStorageSettings`): nilai disimpan di database, dibaca saat start,
  "Save" tidak mengubah server yang sedang berjalan dan berlaku setelah restart.
- Password SMTP disimpan terenkripsi (`SmtpSecrets`, kunci di `ta_Meta`). Credential database log memakai
  pola yang sama.

### Layar: Log Manager > Pengaturan > Database

```
Store log:  (o) Database internal (dipakai API)     ( ) Koneksi baru
            -------------------------------------------------------------
            Provider:  [ MSSQL v ]   (MSSQL / MySQL / PostgreSQL)
            Server:    [ db-log.local      ]   Port: [ 1433 ]
            Database:  [ em_log            ]
            Auth:      [ User + password v ]   (MSSQL di Windows: juga Integrated)
            User:      [ emlog_app ]   Password: [ ******** ]
            Opsi:      [x] Enkripsi koneksi (TLS)  [ ] Percayai sertifikat server
            [ Test koneksi ]  [ Prepare... ]  [ Simpan ]
            -------------------------------------------------------------
            Aktif sekarang: internal, skema v3        Tersimpan: em_log@db-log.local (perlu restart)
```

- **Test koneksi:** buka koneksi, baca versi server, cek hak user, cek ada/tidaknya skema log dan versinya.
- **Simpan:** menyimpan pengaturan di database inti (password terenkripsi). Mengikuti pola pengaturan
  terkelola: berlaku setelah restart `Em.Api`; layar menampilkan "aktif sekarang" vs "tersimpan" agar jelas.
  Simpan ditolak bila skema di koneksi tujuan belum siap (harus Prepare dulu).
- Hanya pemegang claim `Log Manager Settings`.
- Untuk container/secret manager: environment variable (mis. `EM_LOG_DB_CONNECTION`, `EM_LOG_DB_PROVIDER`)
  mengesampingkan pengaturan dari Log Manager, sama seperti `EM_DB_*`. Saat override aktif, layar hanya
  menampilkan nilai aktif dan tidak menawarkan Simpan.

### Prepare

Membangun atau memperbarui database store log, dengan langkah yang ditampilkan satu per satu (status dan
pesan tiap langkah):

1. **Koneksi** ke server dengan credential yang diisi.
2. **Buat database** bila belum ada (opsional, dicentang operator; butuh hak membuat database).
3. **Buat skema/tabel** store log sesuai provider: tabel telemetry, tabel audit pusat, tabel error/fingerprint,
   tabel versi skema.
4. **Upgrade** bila skema lama ditemukan: jalankan skrip pembaruan berurutan. Bila skema lebih baru dari
   versi engine: ditolak (jangan menurunkan versi).
5. **Verifikasi:** tulis dan baca satu event uji, lalu hapus.

Aturan keamanan:

- **Tidak pernah menghapus** tabel atau data yang sudah ada. Prepare bisa diulang kapan saja (idempoten).
- Semua objek memakai prefix/skema milik log engine (mis. skema `emlog` atau prefix `log_`), sehingga database
  yang sudah berisi tabel lain tidak terganggu.
- **Dua credential (usulan):** credential **pemilik** (boleh DDL) hanya diminta di dialog Prepare dan tidak
  disimpan; credential **runtime** yang disimpan cukup punya hak INSERT/SELECT (+ DELETE/drop partisi untuk
  retensi). Prepare bisa sekaligus memberi hak itu ke user runtime.
- Skrip per provider ikut di engine (embedded) dan juga disalin ke `doc/sqlscript/<provider>/` agar DBA bisa
  menjalankannya manual bila Prepare tidak diberi hak DDL. Saat ini `doc/sqlscript/` baru berisi `mssql`;
  log engine menjadi fitur pertama yang butuh skrip MySQL dan PostgreSQL.

### Perpindahan store

- Mengganti store (internal -> koneksi baru, atau sebaliknya) **tidak memindahkan data lama** secara otomatis.
  Setelah restart, event baru masuk ke store baru; store lama tidak dihapus.
- Audit **tidak hilang** saat perpindahan: outbox di aplikasi pengirim hanya ditandai terkirim setelah server
  menerima, jadi audit yang belum terkirim akan masuk ke store baru.
- Alat pindah data (salin store lama ke baru) dibahas belakangan bila dibutuhkan.

### Perbedaan per provider (perlu dirancang di plan)

- Retensi telemetry paling murah dengan partisi per hari yang dibuang utuh: SQL Server (partition
  function/switch), PostgreSQL (declarative partitioning), MySQL (`PARTITION BY RANGE`). Alternatif yang sama
  untuk semua provider: tabel per hari.
- Properti JSON: `nvarchar(max)` + `OPENJSON` (SQL Server), `jsonb` + indeks GIN (PostgreSQL), `JSON`
  (MySQL). Query Log Manager harus dibatasi ke operasi yang tersedia di ketiganya.
- Pencarian teks pesan: full-text index berbeda di tiap provider; tahap awal cukup `LIKE` dengan batas rentang
  waktu wajib.

### Pertanyaan terbuka (database store log)

1. Setuju pola **dua credential** (pemilik untuk Prepare, tidak disimpan; runtime disimpan dengan hak minimal)?
2. Perubahan store berlaku **setelah restart** (sama dengan pengaturan storage) atau perlu bisa pindah tanpa
   restart?
3. Ketiga provider wajib didukung sejak tahap pertama, atau mulai MSSQL dulu lalu MySQL/PostgreSQL menyusul?
4. Audit lokal (outbox) di aplikasi pengirim mengikuti provider database aplikasi itu. Apakah aplikasi ERP juga
   akan berjalan di MySQL/PostgreSQL, sehingga skrip outbox juga perlu tiga provider?

## Cara kerja 1: log operasional server (diskusi 2026-10-12)

Pengguna mulai membahas cara kerja satu per satu. Yang pertama: menyimpan log operasional **seperti yang
sekarang tampil di console server**, misalnya koneksi API masuk.

### Yang tampil sekarang

Semua lewat `ILogger`, ditulis `AddSimpleConsole` satu baris per entri (`EmApp.BuildApp`):

```
08:15:02 info: Em.Api.Request[0] #0001A IN     POST Invoice/Save from 10.0.0.5
08:15:02 info: Em.Api.Request[0] #0001A START  POST Invoice/Save from 10.0.0.5 as budi (U001)
08:15:02 info: Em.Api.Request[0] #0001A DONE   POST Invoice/Save from 10.0.0.5 as budi (U001) - 200 in 41.3 ms (total 43.0 ms)
08:15:09 warn: Em.Api.Request[0] #0001B REJECT GET  Robot/List from 10.0.0.9 as anonymous - 401 ... after 0.4 ms
08:16:00 fail: Em.Api.Core.ObjectStorage...[0] ...exception...
```

Sumbernya: `RequestTrace` (kategori `Em.Api.Request`: IN, START, DONE, ABORT, REJECT), `ServicesBase.Logger`
milik modul, service background engine, serta ASP.NET Core dan EF Core.

`RequestTrace` sudah memakai **message template** (`#{Id} DONE {Method} {Route} ... {StatusCode} in
{Elapsed} ms`). Artinya setiap nilai (`Method`, `Route`, `Caller`, `User`, `StatusCode`, `Elapsed`, `Total`)
sudah tersedia sebagai properti terpisah, bukan hanya teks. Itu yang membuat log bisa disimpan terstruktur
tanpa mengubah cara penulisan.

### Alur yang diusulkan

```
ILogger.Log(...)  (kode tidak berubah)
   |-- Console provider (tetap, tampilan sama seperti sekarang)
   +-- EmLog provider (baru)
         1. ubah jadi event: waktu UTC, level, kategori, template, pesan jadi, properti, exception, scope
         2. tambah identitas: app, instance, versi, environment
         3. masuk antrean memori (Channel, berbatas) -- tidak pernah memblokir atau melempar exception
                |
         Writer background (per 1 detik atau per 500 event)
         4. tulis batch ke segmen file di disk lokal (./data/logs/outbox/*.jsonl)
                |
         Sender
         5a. log server di Em.Api ini sendiri  -> bulk insert ke store log (database)
         5b. log server pusat                  -> kirim OTLP/HTTP ke log server
         6. segmen dihapus setelah berhasil; gagal -> coba lagi nanti
```

- **Satu jalur untuk dua topologi:** memori -> segmen disk -> sink. Sink-nya database lokal atau HTTP ke pusat.
  Bila store/log server mati, event aman di disk dan dikirim begitu tersambung lagi.
- **Tidak mengganggu request:** provider hanya memasukkan ke antrean. Bila antrean penuh, event level rendah
  (Debug/Information) dibuang lebih dulu, Warning ke atas dipertahankan. Jumlah yang dibuang dicatat sebagai
  satu event peringatan ("1.250 event dibuang karena antrean penuh").
- **Batas disk:** segmen outbox punya batas total (mis. 500 MB). Bila terlampaui, segmen terlama dibuang, juga
  dicatat.

### Contoh event tersimpan (baris DONE)

```json
{
  "time": "2026-10-12T01:15:02.481Z", "level": "Information", "category": "Em.Api.Request",
  "template": "#{Id} DONE   {Method} {Route} from {Caller} as {User} - {StatusCode} in {Elapsed:0.0} ms (total {Total:0.0} ms)",
  "message": "#0001A DONE   POST Invoice/Save from 10.0.0.5 as budi (U001) - 200 in 41.3 ms (total 43.0 ms)",
  "props": { "Id": "0001A", "Method": "POST", "Route": "Invoice/Save", "Caller": "10.0.0.5",
             "User": "budi (U001)", "StatusCode": 200, "Elapsed": 41.3, "Total": 43.0 },
  "app": "ErpApi", "instance": "srv-app-01", "version": "1.4.0", "env": "prod",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736", "requestId": "srv-app-01:0001A", "userId": "U001"
}
```

Log Manager tab **Log** menampilkan kolom `message` persis seperti console. Dengan properti, filter seperti
"status >= 500", "Elapsed > 1000 ms", "route Invoice/*", atau "semua baris request #0001A" bisa dilakukan.

### Penyesuaian kecil di engine (usulan)

1. **Id request yang unik.** `#0001A` adalah penghitung pendek yang mulai lagi dari 0 setiap restart dan
   berulang setelah 0xFFFFF. Console tetap menampilkan id pendek; event menyimpan juga `requestId` yang unik
   (instance + waktu start + id, atau `traceId`).
2. **Scope per request.** `RequestTrace` membuka `ILogger.BeginScope` berisi `requestId`, `traceId`, dan user,
   sehingga log modul di dalam request itu (mis. `Logger.LogWarning` di service invoice) ikut membawa id yang
   sama dan bisa dirangkai dengan baris IN/DONE.
3. **User sebagai properti terpisah.** Saat ini `User` berupa teks gabungan (`budi (U001) [debug:...]`).
   Tambahkan properti `UserId`, `Account`, `DebugKey`, `Impersonating` agar bisa difilter tanpa mengurai teks.
4. **Log saat startup.** `EmAppBuilder` memakai logger bootstrap sendiri yang hanya menulis ke console. Log itu
   juga perlu masuk ke segmen disk (atau ditahan di memori sampai provider siap), karena error konfigurasi saat
   start justru paling sering dicari.

### Level dan filter

- Memakai mekanisme standar `Logging:LogLevel` di `appsettings.json` yang sudah ada, ditambah bagian khusus
  provider (`Logging:EmLog:LogLevel`). Contoh: console tetap `Information`, store juga `Information` tetapi
  `Microsoft.EntityFrameworkCore` hanya `Warning`.
- Bawaan: Debug/Trace tidak disimpan.
- Nanti: level bisa diubah dari Log Manager tanpa restart (pertanyaan terbuka).

### Pertanyaan terbuka (log operasional)

1. **IN/START/DONE disimpan semua** (tiga baris per request, sama persis dengan console), atau hanya baris akhir
   (DONE/ABORT/REJECT) yang sudah berisi semua informasinya? Menyimpan semua melipatkan tiga volume; IN tetap
   berguna untuk request yang menggantung tanpa DONE.
2. Endpoint mentah (registry container, NuPak, CDN, nanti penerima OTLP) saat ini tidak lewat `RequestTrace`.
   Perlu ikut dicatat sebagai log akses?
3. Retensi bawaan log operasional: 14 hari?
4. Perlu mengubah level log dari Log Manager tanpa restart?
5. Selain database/log server, perlu juga file log harian yang bisa dibuka langsung di server (tanpa Log
   Manager), atau segmen outbox sudah cukup?

## Cara kerja 2: audit di modul Invoice (contoh asumsi, 2026-10-12)

Semua nama di bagian ini (`ta_Invoice`, `AddDocumentAudit`, `Audit.Event`, action `GetMeta_AuditActivity`)
adalah **asumsi untuk diskusi**, bukan API yang sudah ada. Pengguna: tidak membuat control baru untuk ini.

### 1. Registrasi di modul (sekali, saat startup)

```csharp
builder.AddDocumentAudit<InvoiceKey>("Invoice", doc => doc
   .Root<ta_Invoice>(r => new InvoiceKey(r.cInvoiceId), docNo: r => r.cInvoiceNo)
   .Lines<ta_InvoiceLine>("Lines", l => new InvoiceKey(l.cInvoiceId), lineKey: l => l.nLineNo)
   .Display<ta_Invoice>(r => r.cCustomerId,
      (db, id) => db.ta_Customers.Where(c => c.cCustomerId == id).Select(c => c.cCustomerName))
   .Display<ta_InvoiceLine>(l => l.cItemId,
      (db, id) => db.ta_Items.Where(i => i.cItemId == id).Select(i => i.cItemName))
   .Ignore("ustamp", "datestamp", "json_object")
   .CanRead(c => c.HasClaim(InvoiceClaims.View)));
```

- `Root`/`Lines`: entitas mana yang termasuk dokumen invoice dan bagaimana barisnya dikaitkan ke induk.
- `Display`: teks tampilan disimpan bersama id (customer `C001` = "PT Maju Jaya" saat itu).
- `Ignore`: kolom teknis tidak dicatat.
- `CanRead`: siapa yang boleh membaca riwayatnya (sama dengan hak membuka invoice).

### 2. Di service: edit biasa tidak perlu kode tambahan

```csharp
[PostAction(claim: InvoiceClaims.Edit)]
public async Task PostInvoice_Save(InvoiceSaveDto data) {
   // ... validasi, ctx.UpdateRow(header), tambah/ubah/hapus baris seperti biasa ...
   await ctx.SaveChangesAsync();   // interceptor menulis audit dalam transaksi yang sama
}

[PostAction(claim: InvoiceClaims.Post)]
public async Task PostInvoice_Post(string cInvoiceId) {
   var inv = await ctx.ta_Invoices.SingleAsync(r => r.cInvoiceId == cInvoiceId, AbortToken);
   inv.cInvoiceStatus = InvoiceStatus.Posted;
   Audit.Event("Invoice", new InvoiceKey(cInvoiceId), "posted");          // kejadian bisnis
   await ctx.SaveChangesAsync();                                          // perubahan status + event: satu transaksi
}

[PostAction(claim: InvoiceClaims.Void)]
public async Task PostInvoice_Void(string cInvoiceId, string reason) {
   // ... set status Void ...
   Audit.Event("Invoice", new InvoiceKey(cInvoiceId), "voided", reason: reason);   // alasan wajib
   await ctx.SaveChangesAsync();
}
```

`Audit.Event` hanya menitipkan kejadian; penulisannya ikut `SaveChanges` berikutnya, jadi bila simpan gagal,
kejadiannya juga tidak tercatat. Langkah approval ditulis otomatis oleh engine approval.

### 3. Contoh isi tersimpan

Skenario: Budi membuat INV/2026/10/0012 (2 baris). Sari mengganti customer, mengubah qty baris 2, dan menambah
baris 3. Andi mem-post dan mencetak.

**ChangeSet**

| Seq | Waktu (UTC) | User | Request | Alasan |
|---|---|---|---|---|
| 1041 | 2026-10-12 01:15:02 | U001 budi | srv-app-01:0001A | |
| 1057 | 2026-10-12 02:40:18 | U007 sari | srv-app-01:0003F | |
| 1063 | 2026-10-12 03:05:44 | U003 andi | srv-app-01:00052 | |

**Change**

| Seq | Entitas | Kunci | Operasi | Dokumen |
|---|---|---|---|---|
| 1041 | ta_Invoice | INV-0012 | insert | Invoice INV-0012 (INV/2026/10/0012) |
| 1041 | ta_InvoiceLine | INV-0012 / 1 | insert | sama |
| 1041 | ta_InvoiceLine | INV-0012 / 2 | insert | sama |
| 1057 | ta_Invoice | INV-0012 | update | sama |
| 1057 | ta_InvoiceLine | INV-0012 / 2 | update | sama |
| 1057 | ta_InvoiceLine | INV-0012 / 3 | insert | sama |
| 1063 | ta_Invoice | INV-0012 | update | sama |

Diff (kolom JSON) untuk change set 1057:

```json
[
  { "entity": "ta_Invoice", "key": "INV-0012", "op": "update", "fields": [
      { "f": "cCustomerId", "old": "C001", "new": "C014",
        "oldText": "PT Maju Jaya", "newText": "CV Sinar Abadi" },
      { "f": "nTotal", "old": 12500000, "new": 13750000 } ] },
  { "entity": "ta_InvoiceLine", "key": "INV-0012/2", "op": "update", "fields": [
      { "f": "nQty", "old": 10, "new": 12, "rowText": "Baris 2 - Kertas A4 80gr" } ] },
  { "entity": "ta_InvoiceLine", "key": "INV-0012/3", "op": "insert", "fields": [
      { "f": "cItemId", "new": "IT-220", "newText": "Tinta Printer Hitam" },
      { "f": "nQty", "new": 2 }, { "f": "nPrice", "new": 375000 } ] }
]
```

**DocumentEvent**

| Seq | Kejadian | User | Detail |
|---|---|---|---|
| 1041 | created | budi | snapshot awal |
| 1063 | posted | andi | status Draft -> Posted, snapshot |
| 1064 | output | andi | print, cetakan ke-1, snapshot |

### 4. Membaca untuk flyout "Aktivitas" di Invoice Editor

Action engine (asumsi): `GetMeta_AuditActivity(docType, docKey, page)`. Hak dicek lewat `CanRead`. Hasilnya
sudah diringkas untuk tampilan:

```json
[
  { "time": "2026-10-12T03:06:10Z", "user": "andi", "kind": "output",  "summary": "Mencetak (cetakan ke-1)" },
  { "time": "2026-10-12T03:05:44Z", "user": "andi", "kind": "posted",  "summary": "Mem-post invoice" },
  { "time": "2026-10-12T02:40:18Z", "user": "sari", "kind": "updated", "summary": "Mengubah 3 field, menambah 1 baris",
    "changes": [ "Customer: PT Maju Jaya -> CV Sinar Abadi",
                 "Total: 12.500.000 -> 13.750.000",
                 "Baris 2 Kertas A4 80gr, Qty: 10 -> 12",
                 "Baris 3 ditambah: Tinta Printer Hitam, Qty 2 x 375.000" ] },
  { "time": "2026-10-12T01:15:02Z", "user": "budi", "kind": "created", "summary": "Membuat invoice INV/2026/10/0012" }
]
```

Di Invoice Editor cukup elemen WPF standar, tanpa control baru: tombol "Aktivitas" di toolbar yang membuka
panel kanan (kolom Grid yang visibility-nya terikat ke `Vm.IsActivityOpen`, atau `Popup`), berisi
`ItemsControl` dengan `ItemTemplate` (waktu, user, ringkasan, daftar perubahan). Saat panel dibuka, VM
memanggil action di atas dengan kunci invoice yang sedang dibuka, dan memuat ulang setelah Save/Post. Bila
banyak modul membutuhkan panel yang sama, baru dipertimbangkan control engine bersama.

### Pertanyaan terbuka (audit modul)

1. Teks ringkasan ("Mengubah 3 field") dibuat server (satu bahasa, seragam) atau client (bisa dilokalkan)?
2. Nama field yang tampil: nama kolom (`nQty`) atau label yang dideklarasikan modul (`Qty`)? Usulan: modul
   memberi label lewat registrasi atau atribut `[Display]`.
3. Field total hasil hitungan (`nTotal`) ikut dicatat, atau hanya field yang diinput user?

### 5. Mengambil activity: satu invoice dan semua invoice (asumsi)

Mengikuti pola engine approval: kontrak di `Em.Libs` (seperti `IApprovalServices`), client WPF/MAUI di
`Api.Core` (seperti `ApprovalService`), kunci dokumen dikirim sebagai kunci kanonik string (seperti
`GetMeta_ApprovalRequestsByDoc(docType, docKey)`). Parameter GET berupa nilai sederhana per field, bukan objek
filter (lihat [get-parameter-per-field.md](get-parameter-per-field.md)).

```csharp
// Em.Libs (asumsi)
public interface IAuditServices
{
   // Satu dokumen, menurut kunci
   Task<AuditActivityPage> GetMeta_AuditActivity_ByDoc(string docType, string docKey, long? beforeSeq, int take);

   // Satu dokumen, menurut nomor dokumen (INV/2026/10/0012)
   Task<AuditActivityPage> GetMeta_AuditActivity_ByDocNo(string docType, string docNo, long? beforeSeq, int take);

   // Semua dokumen satu jenis (semua invoice), dengan filter opsional
   Task<AuditActivityPage> GetMeta_AuditActivity_ByType(string docType, DateTime? from, DateTime? to,
      string? userId, string? kinds, long? beforeSeq, int take);

   // Yang lebih baru dari Seq tertentu (refresh flyout tanpa memuat ulang semua); docKey null = semua dokumen
   Task<AuditActivity[]> GetMeta_AuditActivity_Since(string docType, string? docKey, long afterSeq);
}

public sealed class AuditActivityPage
{
   public AuditActivity[] Items { get; set; } = [];
   public long? NextBeforeSeq { get; set; }        // null = sudah habis
}

public sealed class AuditActivity
{
   public long Seq { get; set; }
   public DateTime Time { get; set; }              // UTC
   public string UserId { get; set; } = "";
   public string Account { get; set; } = "";
   public string? ActingAs { get; set; }
   public string Kind { get; set; } = "";          // created, updated, posted, voided, output, approval, ...
   public string DocType { get; set; } = "";
   public string DocKey { get; set; } = "";        // kunci kanonik, untuk membuka editor
   public string DocNo { get; set; } = "";
   public string Summary { get; set; } = "";       // "Mengubah 3 field, menambah 1 baris"
   public string? Reason { get; set; }
   public AuditFieldChange[] Changes { get; set; } = [];
}

public sealed class AuditFieldChange
{
   public string Row { get; set; } = "";           // "" = header, "Baris 2 - Kertas A4 80gr"
   public string Field { get; set; } = "";         // label: "Qty"
   public string? Old { get; set; }                // teks tampilan
   public string? New { get; set; }
   public string Op { get; set; } = "update";      // insert/update/delete (baris)
}
```

- **Paging pakai `Seq`, bukan nomor halaman.** "Muat lagi" mengirim `beforeSeq = NextBeforeSeq`. Kejadian baru
  yang masuk saat pengguna menggulir tidak menggeser halaman.
- **Nomor dokumen:** engine mencari kunci dari `DocNo` yang tersimpan. Bila satu nomor dipakai lebih dari satu
  dokumen (mis. nomor dipakai ulang setelah dihapus), action menolak dan meminta kunci.
- **Hak:** `ByDoc`/`ByDocNo` dicek dengan `CanRead` jenis dokumen. `ByType` juga, ditambah filter baris
  opsional dari registrasi (mis. `.ReadFilter(...)` per cabang) supaya pengguna tidak melihat aktivitas invoice
  cabang lain.
- **Indeks** (store audit): (`DocType`, `DocKey`, `Seq` desc), (`DocType`, `Seq` desc), (`DocType`, `DocNo`).

Pemakaian di VM Invoice Editor (flyout satu invoice):

```csharp
public async Task OpenActivityAsync() {
   var key = AuditKey.Of(new InvoiceKey(Invoice.cInvoiceId));          // kunci kanonik
   var page = await _audit.GetMeta_AuditActivity_ByDoc("Invoice", key, beforeSeq: null, take: 30);
   Activities.ReplaceWith(page.Items);
   _nextBefore = page.NextBeforeSeq;
   IsActivityOpen = true;
}

public async Task LoadMoreAsync() {                                    // gulir ke bawah
   if (_nextBefore is null) return;
   var page = await _audit.GetMeta_AuditActivity_ByDoc("Invoice", _key, _nextBefore, 30);
   Activities.AddRange(page.Items);
   _nextBefore = page.NextBeforeSeq;
}

public async Task RefreshActivityAsync() {                             // setelah Save/Post
   var newer = await _audit.GetMeta_AuditActivity_Since("Invoice", _key, Activities.FirstOrDefault()?.Seq ?? 0);
   Activities.InsertRange(0, newer);
}
```

Flyout semua invoice (mis. di layar daftar invoice) sama, hanya memanggil `ByType("Invoice", ...)`; setiap item
membawa `DocNo` untuk ditampilkan dan `DocKey` untuk membuka Invoice Editor saat diklik.

Kode server modul (mis. laporan) memakai pembaca yang sama tanpa HTTP, mis. `IAuditReader` lewat DI, dengan
method yang sama tanpa prefix `GetMeta_`.

## Cara kerja 3: audit User Manager (contoh asumsi, 2026-10-12)

Pertanyaan pengguna: bagaimana mendaftarkan audit untuk User Manager dan bagaimana tercatat saat admin
menambah user. Nama API tetap asumsi. Kebutuhan ini sama dengan tab Activity User Editor yang masih mockup
tersembunyi ([user-editor-activity.md](user-editor-activity.md)); audit data menjadi sumber datanya.

### Bedanya dengan Invoice

- User Manager adalah fitur **engine** (`CredentialServices` di `Em.Api.Core`), bukan modul. Registrasinya
  dilakukan engine sendiri saat inisialisasi inti, bukan di `Program.cs` host, sehingga setiap aplikasi
  mendapat audit akun tanpa langkah tambahan.
- User adalah **master data**, bukan dokumen transaksi: tidak ada nomor dokumen, post, atau cetak. Mekanisme
  sama, kelompoknya bernama `User` dengan kunci `cUserId`.
- Data user tersebar di beberapa tabel, dan **contact tidak menunjuk ke user**: `ta_User.cContactId` yang
  menunjuk ke contact, dan satu contact bisa dipakai lebih dari satu user (`GetTa_Users_ByContactId`).
  Perubahan contact/alamat/komunikasi harus dipetakan ke user lewat query, bukan lewat kolom `cUserId`.
- Ada **data rahasia**: `ta_UserCredential.cCredentialSecret` (hash password/secret) tidak boleh masuk audit.

### Registrasi (di inti engine)

```csharp
builder.AddEntityAudit<UserKey>("User", g => g
   .Root<ta_User>(u => new UserKey(u.cUserId), title: u => u.cUserAccount)
   .Child<ta_UserRole>(r => new UserKey(r.cUserId), rowKey: r => r.cRoleId)
   .Child<ta_UserClaim>(c => new UserKey(c.cUserId), rowKey: c => c.cClaimKey)
   .Child<ta_UserCredential>(c => new UserKey(c.cUserId), rowKey: c => c.cCredentialType)
   // contact, alamat, komunikasi tidak punya cUserId: peta ke user lewat query
   .Related<ta_Contact>((db, c) => db.ta_Users.Where(u => u.cContactId == c.cContactId).Select(u => new UserKey(u.cUserId)))
   .Related<ta_Address>((db, a) => db.ta_Users.Where(u => u.cContactId == a.cContactId).Select(u => new UserKey(u.cUserId)))
   .Related<ta_Comm>((db, m) => db.ta_Users.Where(u => u.cContactId == m.cContactId).Select(u => new UserKey(u.cUserId)))
   .Display<ta_UserRole>(r => r.cRoleId,
      (db, id) => db.ta_Roles.Where(x => x.cRoleId == id).Select(x => x.cRoleName))
   .Secret<ta_UserCredential>(c => c.cCredentialSecret, c => c.cCredentialKey)   // dicatat "diset/diubah" saja
   .Label<ta_User>(u => u.cUserIsAdmin, "Administrator")
   .Label<ta_User>(u => u.cUserState, "Status akun")
   .Ignore("ustamp", "datestamp", "json_object")
   .CanRead(c => c.HasClaim(UserManagerClaims.Access)));
```

`Related` membuat perubahan pada contact bersama muncul di riwayat **setiap** user yang memakai contact itu.

### Saat admin menambah user

`PostTa_User_New` tidak perlu diubah. Method itu sudah memakai satu transaksi eksplisit dengan empat kali
`SaveChangesAsync` (contact, lalu alamat + komunikasi, lalu user, lalu credential). Aturan pengelompokan:

- **Satu change set per transaksi database.** Bila ada transaksi eksplisit, semua `SaveChanges` di dalamnya masuk
  ke change set yang sama; tanpa transaksi eksplisit, satu `SaveChanges` = satu change set.
- Baris audit ditulis di transaksi yang sama, jadi bila `RollbackAsync` terjadi (mis. credential gagal), audit
  ikut batal. Tidak ada riwayat "user dibuat" untuk user yang tidak jadi dibuat.

Contoh: admin `aan` membuat akun `budi` dengan contact baru, lalu memberi role Finance (action
`PostTa_UserRole_New` terpisah, change set kedua).

**ChangeSet**

| Seq | Waktu (UTC) | User | Request | Kelompok |
|---|---|---|---|---|
| 2210 | 2026-10-12 04:02:11 | U000 aan | srv-app-01:0009C | User U014 (budi) |
| 2211 | 2026-10-12 04:02:40 | U000 aan | srv-app-01:0009E | User U014 (budi) |

**Diff change set 2210**

```json
[
  { "entity": "ta_Contact", "key": "CT-0301", "op": "insert", "fields": [
      { "f": "cContactFullName", "new": "Budi Santoso" },
      { "f": "cContactType", "new": "Person" } ] },
  { "entity": "ta_Comm", "key": "CM-0877", "op": "insert", "fields": [
      { "f": "cCommType", "new": "Email" }, { "f": "cCommValue", "new": "budi@contoh.co.id" } ] },
  { "entity": "ta_Address", "key": "AD-0412", "op": "insert", "fields": [ "..." ] },
  { "entity": "ta_User", "key": "U014", "op": "insert", "fields": [
      { "f": "cUserAccount", "label": "Akun", "new": "budi" },
      { "f": "cUserState", "label": "Status akun", "new": "Active" },
      { "f": "cUserIsAdmin", "label": "Administrator", "new": false } ] },
  { "entity": "ta_UserCredential", "key": "U014/Password", "op": "insert", "fields": [
      { "f": "cCredentialSecret", "label": "Password", "secret": true, "new": "(diset)" } ] }
]
```

**Diff change set 2211**

```json
[
  { "entity": "ta_UserRole", "key": "U014/R-FIN", "op": "insert", "fields": [
      { "f": "cRoleId", "label": "Role", "new": "R-FIN", "newText": "Finance" },
      { "f": "cUserRoleExpiry", "label": "Berlaku sampai", "new": "2026-12-31" } ] }
]
```

### Tampil di tab Activity User Editor

Action yang sama dengan dokumen, kelompok `User`: `GetMeta_AuditActivity_ByDoc("User", userKey, null, 30)`:

```
04:02  aan  Memberi role Finance (berlaku sampai 31-12-2026)
04:02  aan  Membuat akun budi
            • Nama: Budi Santoso   • Email: budi@contoh.co.id
            • Status akun: Active  • Administrator: tidak
            • Password diset
```

Kejadian lain untuk tab ini:

- Ubah status (Active -> Suspended), saklar administrator, ubah/cabut role: otomatis dari diff.
- Reset password oleh admin: diff credential (rahasia) + kejadian eksplisit `password.reset` dengan alasan.
- Sign-in berhasil/gagal dan token dicabut: bukan audit data, melainkan **log keamanan** (jenis 6) dengan target
  user yang sama. Tab Activity bisa menggabungkan keduanya menurut waktu (pertanyaan terbuka).

### Pertanyaan terbuka (audit User Manager)

1. Audit akun **selalu aktif** di setiap aplikasi (tidak bisa dimatikan host), atau bisa dimatikan?
2. Perubahan contact bersama ditampilkan di riwayat semua user yang memakainya (usulan), atau hanya dicatat di
   kelompok `Contact` tersendiri?
3. Tab Activity menggabungkan audit data dan log keamanan (sign-in), atau dipisah menjadi dua tab?
4. Perubahan role dan claim role (`ta_Role`, `ta_RoleClaim`) juga diaudit sebagai kelompok `Role` tersendiri?
   Perubahan claim sebuah role memengaruhi semua user pemegang role itu.

### Kunci kelompok audit (`UserKey`, `InvoiceKey`)

`UserKey`/`InvoiceKey` di contoh adalah **asumsi**, belum ada di kode. Bentuknya sama dengan kunci dokumen engine
approval (`TestDocKey` di modul Em.Test): `record` biasa yang bagian-bagiannya ditandai `[KeyPart(n)]`.

```csharp
public record UserKey([property: KeyPart(1)] string UserId);
public record InvoiceKey([property: KeyPart(1)] string InvoiceId);
public record StockMoveKey([property: KeyPart(1)] string WarehouseId, [property: KeyPart(2)] DateTime Date);  // kunci gabungan
```

Engine mengubahnya ke bentuk kanonik string lewat konverter yang sudah ada (`ApprovalKey.ToCanonical`: daftar
nilai berurutan dalam JSON, mis. `["U014"]`) untuk disimpan dan diindeks, lalu mengembalikannya ke record saat
dibaca. Untuk log engine, `KeyPartAttribute` dan konverternya dipindah atau dibagi dari namespace approval agar
dipakai bersama, sehingga kunci approval dan kunci audit untuk dokumen yang sama selalu identik.

### Revisi: kunci tanpa class tambahan dan tanpa bergantung pada approval (2026-10-12)

Pengguna: approval untuk user masih konsep dan bisa saja dibatalkan, jadi log engine **tidak boleh bergantung**
pada engine approval. Pertanyaan pengguna: apakah `UserKey` sama dengan class `ta_User`? Jawabannya tidak:
`ta_User` adalah baris data lengkap, sedangkan `UserKey` hanya pembungkus nilai kunci (`cUserId`). Karena
itu `UserKey`/`InvoiceKey` dan `[KeyPart]` **dihapus dari usulan**.

Penggantinya: kunci diambil dari **primary key di model EF Core** yang sudah ada. Contoh: `[Key]` pada
`ta_User.cUserId`, atau `HasKey(k => new { k.cUserId, k.cRoleId })` untuk `ta_UserRole` di `ApiCoreContext`.
Modul tidak perlu membuat class kunci apa pun.

```csharp
builder.AddEntityAudit("User", g => g
   .Root<ta_User>(title: u => u.cUserAccount)              // kunci = primary key ta_User (cUserId)
   .Child<ta_UserRole>(parent: r => r.cUserId)              // baris anak: cukup sebut kolom yang menunjuk induk
   .Child<ta_UserCredential>(parent: c => c.cUserId)
   .Related<ta_Contact>((db, c) => db.ta_Users.Where(u => u.cContactId == c.cContactId).Select(u => u.cUserId))
   .Secret<ta_UserCredential>(c => c.cCredentialSecret, c => c.cCredentialKey)
   .Ignore("ustamp", "datestamp", "json_object")
   .CanRead(c => c.HasClaim(UserManagerClaims.Access)));
```

- **Kunci satu kolom** disimpan apa adanya: `"U014"`. Membaca: `GetMeta_AuditActivity_ByDoc("User", "U014", null, 30)`.
- **Kunci gabungan** (mis. baris `ta_UserRole`) disimpan engine sebagai nilai berurutan sesuai urutan primary key
  di model EF, mis. `["U014","R-FIN"]`. Modul tidak merangkainya sendiri.
- Bila suatu saat approval dipakai untuk dokumen yang sama, cukup dipastikan kedua engine menghasilkan bentuk
  kunci yang sama; itu urusan integrasi, bukan prasyarat log engine.

### Kenapa ditulis di backend, bukan dari UI (diskusi 2026-10-12)

Pertanyaan pengguna: kenapa audit didefinisikan di backend, bukan lewat service di UI seperti
`Audit.AddEntry("Invoice", "CreateNew", ...)`?

Alasan audit data ditulis di server:

1. **UI bisa dilewati.** Action bisa dipanggil langsung oleh Postman, robot, integrasi, MAUI, atau aplikasi lain.
   Bila audit ditulis UI, perubahan dari jalur lain tidak tercatat.
2. **Tidak atomik.** Save dan `AddEntry` menjadi dua request. Save berhasil tetapi `AddEntry` gagal (jaringan
   putus, aplikasi ditutup) berarti audit hilang; `AddEntry` berhasil tetapi save gagal berarti audit palsu. Di
   server keduanya satu transaksi.
3. **Diff yang benar hanya ada di server.** UI hanya tahu isi form-nya; nilai lama yang sebenarnya ada di
   database (bisa sudah diubah user lain sejak form dibuka).
4. **Bukti, bukan klaim.** Isi yang dikirim client bisa diubah. Prinsip yang sama sudah dipakai `RequestTrace`:
   log yang mencatat klaim sebagai fakta lebih buruk daripada log yang diam.

Namun gaya `AddEntry` tetap dipakai, dalam dua tempat:

**a. Di server, tanpa registrasi (level paling sederhana).** Modul cukup memanggil `AddEntry` di action-nya,
tanpa `AddEntityAudit`. Tidak ada diff otomatis; modul mengisi detail sendiri. Registrasi hanya diperlukan bila
ingin diff otomatis.

```csharp
[PostAction(claim: InvoiceClaims.Edit)]
public async Task PostInvoice_New(InvoiceSaveDto data) {
   // ... simpan invoice ...
   Audit.AddEntry("Invoice", data.cInvoiceId, "created", docNo: data.cInvoiceNo,
      summary: $"Membuat invoice {data.cInvoiceNo}, total {data.nTotal:N0}");
   await ctx.SaveChangesAsync();              // entry ikut transaksi yang sama
}
```

| Level | Yang ditulis modul | Hasil |
|---|---|---|
| 1. `AddEntry` saja | Satu baris per kejadian | Timeline kejadian dengan ringkasan buatan modul |
| 2. Registrasi entitas | Registrasi sekali | Level 1 + diff field otomatis untuk setiap insert/update/delete |

**b. Dari UI, untuk kejadian yang memang hanya terjadi di client.** Contoh: invoice dibuka/dilihat, dicetak
ke printer lokal, diekspor ke Excel, dikirim lewat email dari PC pengguna. Server tidak melihat kejadian ini,
jadi UI yang melapor:

```csharp
await _audit.PostMeta_AuditEntry("Invoice", invoiceId, "exported", detail: "Excel, 1 halaman");
```

- Server tetap mengisi sendiri user, waktu, alamat, dan aplikasi; nilai itu tidak diambil dari client.
- Entry ditandai `source = client` sehingga Log Manager dan flyout bisa membedakannya dari kejadian yang
  dibuktikan server.
- Hak: user hanya boleh menulis entry untuk dokumen yang boleh ia baca (`CanRead`); kejadian bebas
  ditentukan modul (daftar `kind` yang diizinkan dari client).
- Bila request gagal, UI boleh mencoba lagi; kehilangan sesekali dapat diterima untuk jenis kejadian ini.

### Pertanyaan terbuka (AddEntry)

1. Tahap pertama cukup level 1 (`AddEntry` di server), lalu diff otomatis menyusul?
2. Kejadian dari UI (lihat, cetak lokal, ekspor) dibutuhkan? Bila ya, "dibuka/dilihat" ikut dicatat? Volumenya
   besar dan menyentuh privasi.

### Modul mendaftarkan audit entry di builder + class audit per modul dari DI (2026-10-12)

Usulan pengguna: satu modul cukup **mendaftarkan audit entry di builder**, lalu di action memanggil helper
bertipe milik modul, mis. `InvoiceAudit.NewInvoice(entity)` di `PostInvoice_New()`. `InvoiceAudit` **diambil
dari DI**. Ini menjadi bentuk utama level 1; diff otomatis (registrasi entitas) tetap opsional.

**1. Registrasi di builder modul**: katalog kejadian, mirip katalog claim (`AddClaims`) yang dibekukan saat start.

```csharp
public static void AddInvoiceModule(this EmAppBuilder builder) {
   builder.AddDbContext<InvoiceDbContext>();
   builder.AddAudit("Invoice", a => a
      .Title("Invoice")
      .Entry("created",  "Membuat invoice")
      .Entry("updated",  "Mengubah invoice")
      .Entry("posted",   "Mem-post invoice")
      .Entry("voided",   "Membatalkan invoice", requireReason: true)
      .Entry("printed",  "Mencetak invoice")
      .Entry("exported", "Mengekspor invoice", fromClient: true)      // boleh dikirim dari UI
      .CanRead(c => c.HasClaim(InvoiceClaims.View)));
   builder.Services.AddScoped<InvoiceAudit>();
}
```

Manfaat katalog: Log Manager dan flyout tahu daftar kejadian beserta labelnya (untuk filter dan teks), kejadian
yang tidak terdaftar ditolak, alasan wajib ditegakkan engine, dan hanya kejadian `fromClient` yang boleh
dikirim dari UI.

**2. Class audit milik modul** (ditulis modul, membungkus `IAuditWriter` dari engine):

```csharp
public sealed class InvoiceAudit(IAuditWriter audit)
{
   private const string Group = "Invoice";

   public void NewInvoice(ta_Invoice inv, IReadOnlyList<ta_InvoiceLine> lines, string customerName) =>
      audit.AddEntry(Group, inv.cInvoiceId, "created", docNo: inv.cInvoiceNo,
         summary: $"Membuat invoice {inv.cInvoiceNo} untuk {customerName}, {lines.Count} baris, total {inv.nTotal:N0}",
         snapshot: new { inv, lines });

   public void Changed(ta_Invoice before, ta_Invoice after) =>
      audit.AddEntry(Group, after.cInvoiceId, "updated", docNo: after.cInvoiceNo,
         changes: AuditDiff.Of(before, after, ignore: ["ustamp", "datestamp", "json_object"]));

   public void Posted(ta_Invoice inv) =>
      audit.AddEntry(Group, inv.cInvoiceId, "posted", docNo: inv.cInvoiceNo, snapshot: inv);

   public void Voided(ta_Invoice inv, string reason) =>
      audit.AddEntry(Group, inv.cInvoiceId, "voided", docNo: inv.cInvoiceNo, reason: reason);
}
```

`AuditDiff.Of(before, after)` adalah helper engine untuk membuat daftar perubahan field secara manual, tanpa
registrasi entitas.

**3. Pemakaian di service** (konstruktor primer, seperti `TestServices(TestDbContext ctx)`):

```csharp
[Module("Invoice")]
public partial class InvoiceServices(InvoiceDbContext ctx, InvoiceAudit audit) : ServicesBase
{
   [PostAction(claim: InvoiceClaims.Edit)]
   public async Task PostInvoice_New(InvoiceSaveDto data) {
      var inv = data.Header; var lines = data.Lines;
      ctx.ta_Invoices.Add(inv);
      ctx.ta_InvoiceLines.AddRange(lines);
      audit.NewInvoice(inv, lines, data.CustomerName);
      await ctx.SaveChangesAsync();        // invoice + entry audit: satu transaksi
   }

   [PostAction(claim: InvoiceClaims.Void)]
   public async Task PostInvoice_Void(string cInvoiceId, string reason) {
      var inv = await ctx.ta_Invoices.SingleAsync(r => r.cInvoiceId == cInvoiceId, AbortToken);
      inv.cInvoiceStatus = InvoiceStatus.Void;
      audit.Voided(inv, reason);
      await ctx.SaveChangesAsync();
   }
}
```

**Cara kerja di balik layar**

- `IAuditWriter` di-scope per request. `AddEntry` hanya **menitipkan** entry, lalu engine menuliskannya pada
  `SaveChanges` berikutnya dalam request itu, di transaksi yang sama. Engine bisa memasang interceptor-nya ke
  setiap DbContext modul karena semua context didaftarkan lewat `EmAppBuilder.AddDbContext`.
- User, waktu, alamat, `requestId`, dan aplikasi diisi engine, bukan oleh modul.
- `AddEntry` harus dipanggil **sebelum** `SaveChanges`. Bila request selesai dengan entry yang belum tertulis
  (lupa `SaveChanges` atau dipanggil sesudahnya), engine mencatat error diagnostik agar bug modul terlihat saat
  pengujian.
- Kejadian yang tidak terdaftar, atau `voided` tanpa alasan, langsung melempar exception di `AddEntry`, sebelum
  data tersimpan.

### Pertanyaan terbuka (audit per modul)

1. **Database modul:** tabel audit lokal (outbox) harus berada di database yang sama dengan data modul agar
   satu transaksi. Bila `InvoiceDbContext` memakai database lain (`AddExtraDbConn`), tabel audit juga dibuat di
   sana (skrip/Prepare per database)?
2. Satu request menyimpan ke dua DbContext/database berbeda: entry ikut `SaveChanges` context yang mana? Usulan:
   `AddEntry` menerima context secara eksplisit untuk kasus ini, bawaan context pertama yang menyimpan.
3. Perlu snapshot di setiap `created`/`posted`, atau modul yang memutuskan per kejadian (seperti contoh)?

## Cara kerja 4: audit sistem (usulan 2026-10-12)

Pertanyaan pengguna: setelah audit modul jelas, bagaimana dengan **audit sistem**, yaitu kejadian milik engine
sendiri (sign-in, user/role, robot, pengaturan, penghapusan di registry/storage)?

### Prinsip: mekanisme sama dengan modul, pemiliknya engine

- Engine mendaftarkan katalognya sendiri dengan `AddAudit` yang sama seperti modul, tetapi **selalu aktif**:
  host tidak perlu (dan tidak bisa) menambah atau mematikannya di `Program.cs`. Fitur opsional (registry,
  NuPak, object storage, log server) mendaftarkan katalognya saat fitur itu dinyalakan.
- Nama kelompok sistem memakai prefix yang dicadangkan, mis. `System.Auth`, `System.User`. Modul tidak boleh
  mendaftarkan kelompok berprefix `System.` (ditolak saat start).
- Engine punya class audit internal per area, setara `InvoiceAudit`, diambil dari DI oleh service engine:
  `AuthAudit`, `UserAudit`, `RobotAudit`, `SettingsAudit`, `StorageAudit`.
- Disimpan di tabel audit yang sama di **database inti** (outbox yang sama), lalu dikirim ke log server pusat
  seperti audit modul.

### Katalog awal (dari action yang sudah ada)

| Kelompok | Kejadian | Sumber di engine |
|---|---|---|
| `System.Auth` | `signin.success`, `signin.failed`, `signin.locked`, `signout`, `session.revoked`, `token.rejected`, `debug.accepted`, `debug.impersonate` | `PostGetMeta_SignIn`, sign-in admin, sesi (`ta_SystemSession`), gate debug token di `EmApp` |
| `System.Password` | `changed.self`, `reset.byAdmin`, `admin.changed`, `admin.reset` | `PostMeta_ChangeMyPassword`, `PostMeta_ResetPassword`, `PostMeta_ChangeAdminPassword`, `PostMeta_ResetAdminPassword` |
| `System.User` | `created`, `updated`, `deleted`, `state.changed`, `admin.toggled`, `role.granted`, `role.revoked`, `claim.granted`, `claim.revoked` | `PostTa_User_*`, `PostTa_UserRole_*`, `PostTa_UserClaim_*` |
| `System.Role` | `created`, `updated`, `deleted`, `claim.added`, `claim.removed` | `PostTa_Role_*`, `PostTa_RoleClaim_*` |
| `System.Robot` | `created`, `updated`, `deleted`, `regenerated`, `access.set` | `PostGetMeta_RobotCreate`, `PostGetMeta_RobotRegenerate`, `PostMeta_RobotUpdate`, `PostMeta_RobotDelete`, `PostMeta_RobotAccessSet` |
| `System.Settings` | `smtp.changed`, `storage.changed`, `logstore.changed`, `logstore.prepared` | Pengaturan SMTP, storage terkelola, store log (Prepare) |
| `System.Registry` | `image.deleted`, `tag.deleted`, `manifest.deleted`, `folder.deleted`, `deploy.target.deleted`, `deploy.run` | `PostMeta_Ctn*Delete`, deploy runner |
| `System.NuPak` | `feed.deleted`, `prefix.deleted`, `version.purged` | `PostMeta_NuPak*` (lihat pertanyaan terbuka tentang audit NuPak yang sudah ada) |
| `System.ObjectStorage` | `bucket.deleted`, `object.deleted`, `version.purged`, `policy.deleted`, `volume.deleted` | `PostMeta_Store*` |

### Dua cara menulis: ikut transaksi atau langsung

Berbeda dengan modul, banyak kejadian sistem **tidak mengubah data**, atau justru terjadi saat request gagal:

| Cara | Kapan | Contoh |
|---|---|---|
| `AddEntry` (ikut `SaveChanges`, satu transaksi) | Kejadian yang mengubah data | Buat user, beri role, hapus robot, ubah pengaturan SMTP |
| `WriteNowAsync` (transaksi sendiri, langsung) | Kejadian tanpa perubahan data, atau request yang ditolak/gagal | Sign-in gagal, token ditolak, impersonasi debug token, sign-in berhasil |

`WriteNowAsync` tetap ditulis ke outbox lokal (bukan langsung ke log server), jadi tidak bergantung pada log
server pusat.

### Pelaku (actor)

Audit modul hampir selalu dilakukan user yang login. Audit sistem punya beberapa jenis pelaku, dicatat eksplisit:

- `user`: user login biasa (`cUserId`, akun).
- `robot`: token robot (nama robot), mis. push NuPak/registry dari CI.
- `debug`: debug token, dengan `actingAs` bila impersonasi. Menggantikan baris log di `EmApp` yang sekarang
  menyatakan "Until an audit system exists, this log line is the only trace...".
- `system`: job background (retensi, GC registry, maintenance object storage), dengan nama job.
- `anonymous`: belum terautentikasi, mis. sign-in gagal. Akun yang dicoba dicatat sebagai teks, bukan sebagai
  target user, karena akunnya belum tentu ada.

### Target

Target opsional dan bisa lebih dari satu jenis: user, role, robot, bucket, image, pengaturan. Satu kejadian bisa
punya pelaku dan target yang sama-sama user (admin `aan` mereset password `budi`), sehingga tab Activity User
Editor bisa menampilkan "apa yang dilakukan budi" maupun "apa yang dilakukan terhadap budi".

### Perlindungan

- Admin aplikasi **tidak bisa menghapus atau mengubah** audit sistem dari UI. Retensi hanya lewat job dengan batas
  minimal yang ditetapkan engine (mis. tidak kurang dari 1 tahun).
- Salinan di log server pusat membuat penghapusan langsung di database aplikasi tetap terlihat (urutan `Seq`
  bolong). Hash berantai opsional untuk bukti tambahan.
- Sign-in gagal bisa bervolume tinggi saat serangan. Setiap percobaan tetap dicatat, tetapi alert dan tampilan
  mengelompokkannya (mis. "250 sign-in gagal untuk akun admin dari 3 alamat dalam 5 menit").
- Data rahasia tidak pernah masuk: password yang dicoba, token, secret robot, password SMTP. Untuk pengaturan
  hanya dicatat field yang berubah, dengan nilai rahasia ditulis "diubah".

### Dibaca di mana

- Log Manager: tab **Keamanan** (`System.Auth`, `System.Password`) dengan claim `Log Security Read`, dan tab
  **Audit data** dengan filter kelompok `System.*`.
- User Editor tab Activity: `System.User` + `System.Password` + `System.Auth` untuk user itu.
- Robot Manager, Object Storage Manager, NuGet Manager: masing-masing bisa menampilkan riwayat kelompoknya.

### Pertanyaan terbuka (audit sistem)

1. Sign-in **berhasil** dicatat juga di audit (volume besar di aplikasi dengan banyak user), atau cukup di log
   akses/telemetry dan hanya kegagalan yang masuk audit?
2. Audit NuPak yang sudah ada (tabel sendiri, tab Audit di NuGet Manager): dipindah ke `System.NuPak` atau
   dibiarkan?
3. Batas minimal retensi audit sistem: 1 tahun?
4. Perlu alert bawaan untuk kejadian sistem tertentu (mis. impersonasi debug token di production, sign-in gagal
   beruntun, admin direset) dikirim lewat SMTP?

### Login, logout, dan sesi (rincian `System.Auth`, 2026-10-12)

Pengguna menegaskan bahwa audit sistem yang dimaksud terutama **login, logout, dan sejenisnya**. Titik catat di
kode yang ada:

| Kejadian | Titik di engine | Cara tulis |
|---|---|---|
| `signin.success` | `PostGetMeta_SignIn` / `SignInAdminAsync` -> `Tokens.IssueAsync` | `WriteNowAsync` |
| `signin.failed` (akun tidak ada, status tidak aktif, password salah) | `PostGetMeta_SignIn`, sebelum `throw ActionException(401)` | `WriteNowAsync` (request gagal, tidak ada `SaveChanges`) |
| `signout` | `PostMeta_SignOut` -> `Tokens.RevokeAsync(sessionId)` | ikut transaksi revoke |
| `signout.all` | `PostMeta_SignOutAll` -> `Tokens.RevokeAllAsync(userId)` | ikut transaksi revoke |
| `session.revoked` (sebab: password diubah sendiri, direset admin) | `PostMeta_ChangeMyPassword`, `PostMeta_ResetPassword` -> `RevokeAllAsync` | ikut transaksi |
| `session.expired` | Tidak ada request saat sesi kedaluwarsa; dicatat saat refresh ditolak karena kedaluwarsa, atau oleh job pembersih sesi | `WriteNowAsync`, pelaku `system` |
| `token.refreshed` | `PostGetMeta_RefreshToken` | **bukan audit** (terlalu sering); cukup telemetry |

Usulan penempatan: pencatatan sesi (`signin.success`, `signout`, `session.*`) ditaruh **di dalam service `Tokens`**,
bukan di setiap action. Semua jalur yang membuat atau mencabut sesi lewat `Tokens`, jadi tidak ada jalur yang
terlewat bila nanti ada cara login baru (mis. SSO). Hanya `signin.failed` yang dicatat di action sign-in, karena
kegagalan terjadi sebelum `Tokens` dipanggil.

**Id sesi masuk amplop.** Setiap event (audit dan telemetry) membawa `sessionId`. Dengan itu Log Manager bisa
menjawab "apa saja yang dilakukan budi selama sesi ini": dari login, invoice yang dibuat, sampai logout.

Contoh isi:

```json
{ "seq": 3301, "group": "System.Auth", "kind": "signin.success", "time": "2026-10-12T00:58:03Z",
  "actor": { "type": "user", "userId": "U014", "account": "budi" },
  "target": { "type": "user", "id": "U014" },
  "sessionId": "S-7F3A", "address": "10.0.0.5", "app": "ErpWpf 1.4.0",
  "detail": { "method": "password", "sessionExpiry": "2026-10-19T00:58:03Z" } }

{ "seq": 3302, "group": "System.Auth", "kind": "signin.failed", "time": "2026-10-12T01:02:47Z",
  "actor": { "type": "anonymous" },
  "detail": { "accountTried": "budi", "reason": "password" }, "address": "10.0.0.77" }

{ "seq": 3388, "group": "System.Auth", "kind": "signout", "time": "2026-10-12T09:31:10Z",
  "actor": { "type": "user", "userId": "U014", "account": "budi" },
  "target": { "type": "user", "id": "U014" }, "sessionId": "S-7F3A", "address": "10.0.0.5" }

{ "seq": 3402, "group": "System.Auth", "kind": "session.revoked", "time": "2026-10-12T10:05:00Z",
  "actor": { "type": "user", "userId": "U000", "account": "aan" },
  "target": { "type": "user", "id": "U014" }, "reason": "password.reset.byAdmin",
  "detail": { "sessions": 2 } }
```

Catatan:

- Alasan gagal (`reason`: akun tidak ada / status tidak aktif / password salah) **hanya ada di audit** untuk admin.
  Jawaban ke client tetap "Incorrect username or password." seperti sekarang, agar tidak membocorkan akun mana
  yang ada.
- `accountTried` dicatat sebagai teks; bila akun memang ada, event juga ditautkan ke user itu sebagai target
  sehingga tampil di Activity user tersebut ("3 percobaan login gagal").
- Password yang dicoba tidak pernah dicatat.
- Client WPF: saat `SessionEnded` terjadi (token kedaluwarsa/ditolak), client tidak perlu melapor; server yang
  mencatat penyebabnya.

### Audit ke database mana (keputusan 2026-10-12)

Keputusan pengguna: bila satu request menyimpan ke dua database, **developer modul yang bertanggung jawab**
menentukan ke mana entry audit ditulis. Engine tidak menebak.

```csharp
public partial class InvoiceServices(InvoiceDbContext ctx, StockDbContext stock, InvoiceAudit audit) : ServicesBase
{
   [PostAction(claim: InvoiceClaims.Post)]
   public async Task PostInvoice_Post(string cInvoiceId) {
      // ... ubah status invoice di ctx, kurangi stok di stock ...
      audit.Use(ctx).Posted(inv);            // entry ikut transaksi InvoiceDbContext
      await ctx.SaveChangesAsync();
      await stock.SaveChangesAsync();
   }
}
```

Aturan engine:

- **Satu database dalam request** (kasus umum): tidak perlu `Use(...)`; entry ikut `SaveChanges` berikutnya.
- **Lebih dari satu context menyimpan** sementara masih ada entry yang belum diarahkan: engine **menolak dengan
  exception** pada `SaveChanges` context kedua, dengan pesan yang menyebut entry dan context-nya. Tidak ada
  tebakan diam-diam, sehingga kesalahan terlihat saat pengujian.
- `Use(ctx)` mengarahkan entry ke context tertentu; database itu harus sudah punya tabel audit (skrip/Prepare).
- Konsistensi lintas dua database (satu berhasil, satu gagal) tetap tanggung jawab developer, sama seperti data
  modulnya sendiri; audit hanya mengikuti transaksi database yang dipilih.

## Setup retensi (keputusan 2026-10-12)

Keputusan pengguna: retensi tidak boleh hanya nilai bawaan di kode; **harus ada setup-nya**. Usulan bentuknya:

### Tempat dan penyimpanan

- Layar **Log Manager > Pengaturan > Retensi**, claim `Log Manager Settings`.
- Disimpan di database (seperti pengaturan terkelola lain), **berlaku tanpa restart**: job retensi membaca
  pengaturan terbaru setiap kali berjalan.
- Setiap perubahan pengaturan retensi dicatat sebagai audit sistem `System.Settings / retention.changed`
  (nilai lama dan baru).

### Aturan yang bisa diatur

| Cakupan | Satuan | Bawaan (usulan) | Batas bawah |
|---|---|---|---|
| Telemetry per level (Debug/Info, Warning, Error/Critical) | hari + batas ukuran total (GB) | 7 / 30 / 90 hari, 20 GB | 1 hari |
| Log akses (request) | hari | 30 hari | 1 hari |
| Trace | hari | 7 hari | 1 hari |
| Error yang dikelompokkan (fingerprint) | hari sejak terakhir muncul | 180 hari | 30 hari |
| Audit sistem per kelompok (`System.Auth`, `System.User`, ...) | bulan | 24 bulan | 12 bulan (ditetapkan engine) |
| Audit modul per kelompok (`Invoice`, `Customer`, ...) | bulan atau "tidak pernah dihapus" | tidak pernah dihapus | batas dari registrasi modul |
| Outbox lokal di database aplikasi (sudah terkirim ke pusat) | hari | lihat pertanyaan terbuka | 0 = hapus setelah terkirim |

- Pada log server pusat, setiap aturan bisa **ditimpa per aplikasi** (mis. ERP A menyimpan error 1 tahun).
- **Modul menetapkan batas bawah** di registrasi, karena modul yang tahu aturan hukum datanya:

  ```csharp
  builder.AddAudit("Invoice", a => a
     .MinRetention(TimeSpan.FromDays(3653))      // 10 tahun, dokumen pembukuan
     .Entry("created", "Membuat invoice") ...);
  ```

  Layar menolak nilai di bawah batas itu dan menampilkan alasannya.

### Tindakan saat kedaluwarsa

- **Hapus** (bawaan untuk telemetry).
- **Arsipkan lalu hapus** (pilihan untuk audit): data diekspor ke file JSONL terkompresi per bulan, disimpan di
  engine object storage (bucket arsip), baru dihapus dari database. Arsip bisa diunduh atau dimuat ulang ke
  Log Manager untuk dibaca.

### Job retensi

- Berjalan harian pada jam yang diatur (mis. 02:00, di luar jam kerja), memakai runner background engine.
- Menghapus per batch kecil (atau membuang partisi utuh bila provider mendukung), supaya tidak mengunci tabel.
- Hasil setiap run dicatat sebagai audit sistem `System.Settings / retention.run`: jumlah baris dihapus/diarsip
  per cakupan, durasi, dan error.
- **Simulasi:** tombol "Pratinjau" menampilkan berapa baris yang akan dihapus dengan pengaturan sekarang, sebelum
  disimpan.
- **Legal hold:** kelompok audit, dokumen tertentu, atau user tertentu bisa dibekukan (tidak ikut dihapus) selama
  pemeriksaan, dengan alasan; pemasangan dan pelepasannya juga diaudit.

### Pertanyaan terbuka (retensi)

1. Bawaan di tabel di atas sudah sesuai?
2. Outbox lokal: hapus segera setelah terkirim (modul membaca riwayat lama dari pusat) atau disimpan sama lama
   dengan pusat (modul selalu membaca lokal)?
3. Arsip ke object storage dibutuhkan sejak awal, atau cukup hapus dulu?
4. Legal hold dibutuhkan sejak awal?

## Penjelasan: pilihan topologi log server (2026-10-12)

Pertanyaan yang masih terbuka: log server "pusat" itu pusat bagi siapa. Kodenya sama untuk semua pilihan
(`Em.Api` + `AddLogServer()`); yang berbeda adalah siapa yang menjalankannya dan data siapa yang masuk.

| Pilihan | Susunan | Kelebihan | Kekurangan |
|---|---|---|---|
| **A. Satu instalasi** | `Em.Api` aplikasi sekaligus log server; tanpa pengiriman jaringan | Paling sederhana, tidak ada server tambahan | Tiap aplikasi punya log sendiri; tidak bisa mencari lintas aplikasi |
| **B. Pusat per pelanggan** | Satu `Em.Api` log server di jaringan pelanggan; semua aplikasi pelanggan itu (ERP, OshaApp, client WPF) mengirim ke sana | Satu tempat cari untuk satu organisasi; data tetap di pelanggan | Pelanggan menjalankan satu server tambahan |
| **C. Pusat bersama milik vendor** | Semua pelanggan mengirim ke satu log server milik Anda | Anda melihat error semua pelanggan dan semua versi | Audit bisnis pelanggan (invoice, harga, data user) keluar dari server mereka: perlu persetujuan/kontrak dan kepatuhan pelindungan data pribadi (UU 27/2022 tentang PDP; verifikasi ke sumber resmi); perlu pemisahan tenant yang ketat; server Anda menjadi titik beban |
| **D. Campuran** | Audit tetap di A atau B milik pelanggan; hanya telemetry (error, crash, versi) yang juga diteruskan ke log server vendor, setelah data pribadi dibuang | Anda tetap bisa memantau error dan rilis; data bisnis tidak keluar | Pengirim harus mendukung tujuan berbeda per jenis log dan penyaringan data pribadi |

Dampak ke rancangan, apa pun pilihannya:

- A dan B kodenya identik; B hanya A dengan host khusus. Pindah dari A ke B cukup lewat konfigurasi.
- Agar D tetap mungkin nanti tanpa mengubah format: amplop event membawa `tenant` sejak awal, dan pengirim
  mendukung **tujuan per jenis log** (audit ke satu alamat, telemetry ke satu atau beberapa alamat).
- C untuk audit tidak disarankan; bila dibutuhkan, cukup sebagai D (hanya telemetry).

Usulan: mulai dari **A dan B**, siapkan amplop dan konfigurasi tujuan agar **D** bisa ditambahkan belakangan.
Keputusan pengguna 2026-10-12: topologi dasar adalah A (satu instalasi), lihat "Contoh OSHA".

Pertanyaan untuk pengguna:

1. Aplikasi berbasis em-system dipasang **on-premise di pelanggan** atau **di-hosting oleh Anda**? Bila di-hosting
   Anda, B dan C hampir sama (server Anda juga).
2. Apakah Anda (sebagai vendor) ingin melihat **error dan crash** aplikasi di semua pelanggan?

### Contoh OSHA: satu instalasi (keputusan 2026-10-12)

Penjelasan pengguna tentang "satu instalasi": OSHA memakai engine em-system dan menjalankan server di
`osha.com/api`. Log masuk ke API itu sendiri (mis. `osha.com/api/logger/...`). Di Log Manager ada setup
database untuk logger: **internal** berarti memakai database yang sama dengan data lain, dan bisa **diganti ke
database lain**.

```
                        osha.com/api  (Em.Api OSHA + AddLogServer)
                        +-------------------------------------------------+
Client WPF/MAUI OSHA -->| /logger/v1/logs, /logger/v1/traces (telemetry)  |
 (token user)           |                                                 |
                        | ILogger API sendiri ----> langsung, tanpa HTTP  |--> Store log
Aplikasi lain milik  -->| /logger/... (API key robot)                     |    internal = DB OSHA
 OSHA (opsional)        |                                                 |    atau DB lain (Log Manager)
                        | Audit modul OSHA ----> tabel audit di DB OSHA   |
                        |                        (satu transaksi data)    |
                        +-------------------------------------------------+
Log Manager (WPF OSHA) --> action biasa di osha.com/api
```

- **Telemetry API itu sendiri** ditulis langsung ke store, tanpa lewat HTTP ke dirinya sendiri.
- **Client WPF/MAUI** mengirim telemetry (error, crash) ke endpoint logger dengan token user yang sedang login.
  Aplikasi lain milik OSHA bisa mengirim ke endpoint yang sama dengan API key robot. Dengan begitu pilihan "pusat
  per pelanggan" (B) hanyalah kasus yang sama dengan lebih banyak pengirim.
- **Audit** selalu ditulis di database data modul, dalam transaksi yang sama:
  - store log **internal**: tabel audit itu sekaligus store audit; tidak ada penyalinan;
  - store log **database lain**: telemetry langsung ke database lain itu, sedangkan audit tetap ditulis dulu di
    database OSHA lalu disalin oleh shipper di dalam proses yang sama (tanpa HTTP) ke database log. Riwayat
    dokumen untuk modul dibaca dari database OSHA.
- Path endpoint mengikuti base path API (`osha.com/api/logger/...`); nama akhirnya ditentukan di plan.

## Protokol logger: lewat action `/api` atau endpoint sendiri (diskusi 2026-10-12)

Pertanyaan pengguna: logger memakai protokol sendiri, atau masuk ke `/api`?

### Yang sudah ada di engine

- Action dilayani di `/api/{module}/{action}` (`EmApp`: `MapMethods("/api/{module}/{action}", ...)`), lengkap
  dengan gate identitas, claim, `RequestTrace`, timeout, dan rate limit.
- Protokol lain memakai **cabang endpoint terpisah** di host yang sama: CDN (`/cdn`), container registry
  (`/v2`), dan endpoint protokol modul lewat `builder.AddPublicEndpoint("/prefix", handler)`
  ([engine-public-endpoints.md](../engine/engine-public-endpoints.md)). Cabang ini mengurus auth, batas body,
  dan error sendiri, di luar claim/timeout action. Prefix `/api`, `/cdn`, `/v2` dicadangkan; endpoint protokol
  tidak bisa berada di dalam `/api`.

### Perbandingan

| Pilihan | Kelebihan | Kekurangan |
|---|---|---|
| **1. Action biasa** (`/api/Logger/PostMeta_Ingest`, body JSON milik kita) | Paling cepat dibuat; auth, claim, client `ServiceWpfBase.PostAsync` dipakai ulang | Format buatan sendiri, hanya client em-system yang bisa mengirim; setiap kiriman log menghasilkan baris `RequestTrace` (log tentang log); terikat timeout dan batas action |
| **2. Endpoint protokol standar OTLP/HTTP** di cabang sendiri (`/otlp/v1/logs`, `/otlp/v1/traces`) | Standar: SDK OpenTelemetry dan aplikasi non-.NET bisa mengirim langsung; client yang sama bisa diarahkan ke Seq/backend lain; tidak lewat pipeline action (tanpa `RequestTrace`, mendukung gzip dan body besar) | Perlu membaca format OTLP (JSON dan protobuf) dan memasang auth sendiri di cabang itu |
| **3. Protokol jaringan sendiri** (TCP/UDP, port lain) | Tidak ada yang berarti | Port dan firewall tambahan, TLS sendiri, tidak lewat reverse proxy; tidak disarankan |

Semua pilihan 1 dan 2 tetap di **host, port, dan TLS yang sama** dengan API (contoh OSHA: server yang sama
dengan action OSHA); yang berbeda hanya path dan format.

### Usulan: dipisah menurut fungsi

| Fungsi | Jalur |
|---|---|
| Kirim telemetry (log, error, trace) dari client WPF/MAUI dan aplikasi lain | **Endpoint OTLP/HTTP** lewat `AddPublicEndpoint`, mis. `<server>/otlp/v1/logs` dan `/otlp/v1/traces`. Auth: token user yang login (client) atau API key robot (aplikasi lain), divalidasi dengan mekanisme token engine |
| Log Manager (query, pengaturan database, Prepare, retensi, error), flyout activity, `PostMeta_AuditEntry` dari UI | **Action biasa** di `/api/...` (`GetMeta_`/`PostMeta_`), karena memang operasi aplikasi yang butuh claim |
| Telemetry dan audit milik API itu sendiri | Tidak lewat HTTP; ditulis langsung di dalam proses |
| Kirim audit antar-server (hanya bila nanti ada pusat B/D) | Ditunda. OTLP tidak cocok untuk catatan yang wajib idempoten dan diakui per `Seq`; nanti endpoint/action khusus |
| Tail live | Nanti lewat engine stream dua arah |

Catatan untuk endpoint OTLP:

- Request ke endpoint ini tidak menghasilkan `RequestTrace` per kiriman; cukup penghitung (diterima, ditolak,
  ukuran) sebagai metrik, agar tidak terjadi log tentang log.
- Batas ukuran body, rate limit per pengirim, dan penolakan event yang terlalu besar diatur di cabang itu.
- Nama prefix (`/otlp`, `/logger`, atau lain) ditentukan di plan; tidak boleh tumpang tindih dengan `/api`,
  `/cdn`, `/v2`.

### Pertanyaan terbuka (protokol)

1. Setuju telemetry lewat endpoint OTLP standar (pilihan 2), dan semua fungsi lain lewat action `/api`?
2. OTLP JSON saja di tahap awal (lebih mudah dibaca dan diuji), protobuf menyusul?

## Keputusan: tanpa OTLP dulu, salinan lokal dikirim lalu dihapus (2026-10-12)

Keputusan pengguna:

1. Untuk rancangan ini **belum perlu protokol OTLP**. Yang harus ada: **service log internal** lewat `ILogger` di
   server dan di UI.
2. **Salinan lokal:** setelah login, salinan lokal **dikirim di background ke server lalu dihapus**. Bila
   pengiriman masih gagal, **tetap disimpan lokal** dan dicoba lagi.

Bagian "Protokol logger" di atas tetap disimpan sebagai riwayat; endpoint OTLP menjadi kemungkinan tahap
lanjut, bukan bagian rancangan ini.

### Service log di server

- Provider `ILogger` milik engine (lihat "Cara kerja 1"), menulis langsung ke store log di dalam proses.
- Modul tetap menulis lewat `ServicesBase.Logger`/`ILogger` seperti sekarang; tidak ada API log baru untuk modul.

### Service log di UI (WPF/MAUI)

- Engine UI memasang `ILogger` (provider yang sama bentuknya) di host WPF/MAUI, plus handler exception yang tidak
  tertangani (`DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`,
  `AppDomain.UnhandledException`). Kode layar dan modul UI menulis lewat `ILogger` biasa.
- Event ditulis dulu ke **file lokal** di `%LocalAppData%\<ApplicationName>\Logs\outbox\` (segmen JSONL), mengikuti
  nama aplikasi host.
- Kejadian audit dari UI (`fromClient`: ekspor, cetak lokal) memakai antrean lokal yang sama.

### Alur kirim dari UI

```
ILogger / audit UI --> segmen lokal (selalu, juga sebelum login dan saat offline)
                            |
           sesudah login (dan berkala selama sesi, dan sebelum sign out)
                            v
           background: kirim batch lewat action /api, mis. PostMeta_LogIngest(batch)
                            |
              berhasil --> hapus segmen
              gagal    --> segmen tetap di lokal, dicoba lagi pada jadwal/login berikutnya
```

- Kirim lewat **action biasa** dengan token user yang sedang login; tidak ada endpoint protokol baru.
- Action ingest tidak menghasilkan baris `RequestTrace` per kiriman (hanya penghitung), agar tidak menjadi log
  tentang log.
- Setiap event membawa **user saat event terjadi** (atau "belum login" untuk error saat startup/layar login).
  Server mencatat terpisah siapa yang mengirim batch, supaya log yang terekam di PC bersama tidak tercatat
  sebagai milik user yang kebetulan login berikutnya.
- Batas ukuran folder outbox (mis. 200 MB); bila terlampaui, segmen terlama dibuang dan jumlahnya dilaporkan
  pada kiriman berikutnya.
- Tiap batch punya id unik; server mengabaikan batch yang sudah pernah diterima, jadi kirim ulang setelah
  jaringan putus tidak menggandakan data.

### Audit di server saat store log memakai database lain

Prinsip yang sama berlaku untuk tabel audit lokal di database aplikasi (outbox):

- Audit ditulis ke database aplikasi dalam transaksi data (tetap wajib).
- Shipper background memindahkan ke store log (database lain), lalu **menghapus** dari database aplikasi setelah
  berhasil. Bila gagal, baris tetap di database aplikasi dan dicoba lagi.
- Bila store log = **internal**, tidak ada pemindahan; tabel audit itu sekaligus store.
- Akibatnya pembaca riwayat (flyout activity, `IAuditReader`) membaca dari **store log**, digabung dengan baris
  outbox yang belum terpindah, agar entry yang baru saja ditulis langsung tampil.

### Pertanyaan terbuka

1. Penafsiran "ketika login" di atas: pengiriman salinan lokal **client UI** dipicu saat login. Apakah benar,
   dan apakah aturan "kirim lalu hapus, gagal simpan" juga dimaksudkan untuk outbox audit di database aplikasi
   (seperti ditulis di atas)?

### OTLP sebagai wrapper (keputusan 2026-10-12)

Keputusan pengguna: protokol OTLP bisa dibuat **nanti sebagai wrapper** bila ada aplikasi lain yang ingin
menumpang log di emsys.

Syarat rancangan sekarang agar wrapper itu murah dibuat nanti:

- Inti penerima log adalah **service ingest internal yang tidak tahu protokol**, mis. `ILogIngest.WriteAsync(
  IReadOnlyList<LogEvent> events, LogSender sender)`, dengan model event internal (amplop bersama di bagian
  "Definisi jenis log").
- Semua jalur memanggil service yang sama: provider `ILogger` di server (langsung), action
  `PostMeta_LogIngest` dari UI (setelah validasi), dan nanti endpoint OTLP (setelah menerjemahkan format OTLP ke
  `LogEvent`).
- Amplop internal memakai bidang yang bisa dipetakan ke OTLP (waktu, level/severity, kategori, pesan, atribut,
  `traceId`/`spanId`, atribut resource app/instance/versi), supaya terjemahannya satu arah tanpa kehilangan
  data.
- Pengirim (`LogSender`) dibedakan: proses sendiri, user (token), robot (API key). Wrapper OTLP nanti memakai
  robot/API key untuk aplikasi luar.

## Multi database untuk store log (diskusi 2026-10-12)

Pertanyaan pengguna: saat pindah database, mungkinkah **multi database**, sehingga data juga bisa dibaca dari
database yang berbeda (mis. database lama setelah pindah)?

Jawaban: mungkin. Usulan: store log bukan satu koneksi, melainkan **daftar store**.

### Model: daftar store

| Peran | Jumlah | Fungsi |
|---|---|---|
| **Store tulis** | satu per jenis log (lihat di bawah) | Tempat event baru ditulis |
| **Store baca** | nol atau lebih | Database lama atau arsip; hanya dibaca, tidak ditulisi event baru |

- Setiap store punya id, nama, provider, koneksi (credential terenkripsi), status (`tulis`/`baca`/`lepas`), dan
  rentang waktu data yang dimilikinya (waktu event terlama dan terbaru).
- **Pindah database** = store lama diubah menjadi `baca`, store baru (sesudah Prepare) menjadi `tulis`. Data lama
  tidak perlu disalin dan tetap bisa dicari.
- Provider boleh berbeda antar store (mis. store lama MSSQL, store baru PostgreSQL), karena query Log Manager
  sudah dibatasi ke operasi yang tersedia di ketiga provider.
- Store bisa **dilepas** (tidak dibaca lagi) dari Log Manager; database-nya tidak dihapus.

### Store tulis per jenis log (opsional)

Daftar store juga memungkinkan memisahkan tujuan menurut jenis: mis. audit ke database `em_audit` (dicadangkan
ketat, retensi panjang) dan telemetry ke database `em_telemetry` (besar, retensi pendek). Bawaan: satu store tulis
untuk semua jenis (internal).

### Cara membaca dari beberapa store

- Log Manager, flyout activity, dan `IAuditReader` memanggil satu **pembaca gabungan**. Pembaca itu:
  1. memilih store yang rentang waktunya beririsan dengan filter (store di luar rentang dilewati);
  2. menjalankan query yang sama di setiap store terpilih, masing-masing dibatasi N baris;
  3. menggabungkan hasil menurut waktu (dan `Seq` untuk audit), lalu mengembalikan satu halaman.
- Cursor halaman berisi posisi per store, sehingga "muat lagi" tetap benar meski data tersebar.
- `Seq` audit dibuat di outbox database aplikasi dan dibawa apa adanya ke store mana pun, sehingga urutan
  riwayat satu dokumen tetap benar walaupun sebagian ada di store lama dan sebagian di store baru.
- Bila satu store tidak bisa dihubungi, hasil tetap ditampilkan dari store lain dengan peringatan "sebagian data
  tidak tersedia: store X".

### Retensi pada store baca

Job retensi berjalan di semua store (tulis dan baca). Store baca yang sudah kosong ditandai dan bisa dilepas.

### Alternatif: pindahkan data

Selain multi store, bisa disediakan alat "pindahkan data store lama ke store baru" (salin per batch, lalu store
lama dilepas). Lebih sederhana untuk dibaca, tetapi lama untuk data besar dan butuh kedua database aktif selama
proses. Usulan: multi store sebagai dasar; alat pindah data menyusul bila dibutuhkan.

### Batasan

- Pengaturan store berlaku setelah restart (sama dengan pengaturan database store log), karena koneksi didaftarkan
  saat start (`AddExtraDbConn`).
- Query lintas store lebih lambat dari satu store; filter waktu wajib di Log Manager agar store yang tidak relevan
  dilewati.

### Pertanyaan terbuka (multi database)

1. Setuju model daftar store (satu tulis + beberapa baca)?
2. Pemisahan store tulis per jenis (audit vs telemetry) perlu sejak awal, atau cukup satu store tulis dulu?
3. Alat pindah data perlu di tahap awal?

### Kontrak untuk control activity di UI (2026-10-12)

Penegasan pengguna: intinya di UI bisa dibuat control yang me-render activity, dengan tombol **Load more** untuk
memuat halaman berikutnya. Multi database tidak boleh terlihat oleh control.

Revisi kecil pada bagian "Mengambil activity": karena satu halaman bisa berasal dari beberapa store, posisi
halaman memakai **cursor teks yang tidak perlu dipahami client** (berisi posisi per store), bukan `beforeSeq`.

```csharp
Task<AuditActivityPage> GetMeta_AuditActivity_ByDoc(string docType, string docKey, string? cursor, int take);
Task<AuditActivityPage> GetMeta_AuditActivity_ByType(string docType, DateTime? from, DateTime? to,
   string? userId, string? kinds, string? cursor, int take);

public sealed class AuditActivityPage
{
   public AuditActivity[] Items { get; set; } = [];
   public string? NextCursor { get; set; }   // null = tidak ada halaman berikutnya
}
```

Yang perlu dilakukan control:

```csharp
private string? _cursor;
public bool HasMore => _cursor is not null;              // tombol Load more tampil bila true

public async Task LoadFirstAsync() {
   var page = await _audit.GetMeta_AuditActivity_ByDoc(DocType, DocKey, cursor: null, take: 20);
   Items.ReplaceWith(page.Items);
   _cursor = page.NextCursor;
}

public async Task LoadMoreAsync() {                      // tombol Load more
   var page = await _audit.GetMeta_AuditActivity_ByDoc(DocType, DocKey, _cursor, take: 20);
   Items.AddRange(page.Items);
   _cursor = page.NextCursor;
}
```

- Control cukup tahu `DocType` dan `DocKey` (atau filter untuk `ByType`); dari store mana data berasal, urutan
  gabungan, dan store yang tidak bisa dihubungi diurus server.
- Bila sebagian store tidak tersedia, halaman membawa peringatan (mis. `Warnings`) yang bisa ditampilkan control
  sebagai satu baris info.
- Item yang sama tidak muncul dua kali di halaman berbeda, karena cursor menyimpan posisi terakhir per store.
