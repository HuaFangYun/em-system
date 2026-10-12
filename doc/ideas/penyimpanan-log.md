# Penyimpanan log engine

- **Tanggal:** 2026-10-12
- **Status:** diskusi; belum ada kode. Arah melebar menjadi log engine multi-app dengan telemetry ala Seq
  (pengguna 2026-10-12), lihat bagian terakhir
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
