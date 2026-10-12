# Plan — Log engine: audit data, audit sistem, dan telemetry (satu instalasi)

Status: **belum dieksekusi**
Dibuat: 2026-10-12, dipromosikan dari [doc/ideas/penyimpanan-log.md](../../doc/ideas/penyimpanan-log.md) atas
permintaan pengguna. Pertanyaan yang tersisa di catatan ide dijawab dengan usulan bawaan di §2; pengguna
menerima semua usulan itu (2026-10-12), sehingga plan siap dieksekusi tanpa pertanyaan lanjutan. Butir yang paling
berpengaruh pada rancangan tetap ditandai **(cek)** sebagai penanda.

---

## 0. Ringkasan

Membangun log engine di em-system dengan dua keluarga yang jalurnya berbeda:

- **Catatan (audit):** audit modul (mis. Invoice) dan audit sistem (sign-in, sign-out, user, role, robot,
  pengaturan). Ditulis di server, dalam transaksi yang sama dengan datanya, tidak boleh hilang, retensi panjang.
- **Telemetry:** log diagnostik `ILogger`, error/crash (dikelompokkan per fingerprint), dan log akses
  (`RequestTrace`). Boleh hilang sebagian, retensi pendek.

Topologi tahap ini adalah **satu instalasi**: `Em.Api` yang menyalakan `builder.AddLogServer()` adalah log server
untuk dirinya sendiri dan client WPF/MAUI-nya. Telemetry API ditulis langsung di dalam proses. Client mengirim lewat
action `/api` biasa. **Tanpa OTLP.** Store log memakai database internal atau koneksi lain yang diatur dan disiapkan
(Prepare) dari **Log Manager** di WPF.

Integrasi mengikuti aturan library bersama: backend di `Em.Api.Core/Api/Log/`, kontrak dan DTO di `Em.Libs`,
pipeline client di `Em.Ui.Core`, layar di `Em.Ui.Wpf.Core`, skrip SQL di `doc/sqlscript/`, panduan di
`doc/engine/engine-log.md`. Pola acuan terdekat: object storage (`EmAppBuilder.ObjectStorage.cs`,
`ObjectStorageManager`), pengaturan SMTP terenkripsi (`SmtpSettingsStore`), interceptor EF
(`ApprovalDatabaseSwitch`).

## 1. Keputusan pengguna yang sudah ada (2026-10-12)

Dari bagian "Keputusan yang sudah jelas" di catatan ide:

| # | Keputusan |
| --- | --- |
| 1 | Log engine mendukung audit data (termasuk riwayat dokumen) dan telemetry sistem |
| 2 | Semua log terpusat dan bisa dicari di satu tempat |
| 3 | Log bisa ditarik modul (API baca) |
| 4 | Ada Log Manager untuk audit sampai telemetry/error |
| 5 | Log server = `Em.Api` dengan fitur log server dinyalakan, bukan produk terpisah |
| 6 | Store log diatur dari Log Manager: database internal atau koneksi baru MSSQL/MySQL/PostgreSQL, tombol Prepare |
| 7 | Satu request menyimpan ke dua database: developer modul menentukan tujuan entry (`audit.Use(ctx)`), engine menolak bila ambigu |
| 8 | Retensi harus bisa di-setup |
| 9 | Topologi dasar = satu instalasi (contoh OSHA: `osha.com/api`) |
| 10 | Belum memakai OTLP; service log internal lewat `ILogger` di server dan UI; kiriman UI lewat action `/api` |
| 11 | Salinan lokal dikirim di background setelah login lalu dihapus; gagal → tetap lokal dan dicoba lagi |
| 12 | OTLP nanti sebagai wrapper di atas ingest internal (`ILogIngest`) |
| – | Log engine tidak bergantung pada engine approval; kunci diambil dari primary key model EF (tanpa `UserKey`/`[KeyPart]`) |
| – | Audit utama ditulis di backend lewat katalog `builder.AddAudit(...)` + class audit per modul dari DI (`InvoiceAudit`) |
| – | Control activity UI memakai cursor teks buram dan tombol Load more; multi database tidak terlihat oleh control |

## 2. Usulan bawaan untuk pertanyaan yang masih terbuka

Dipakai bila pengguna tidak mengubahnya sebelum eksekusi. Nomor merujuk ke bagian di catatan ide.

**Cakupan**

- **(cek) Jenis log tahap ini:** 1 diagnostik, 2 error/crash, 3 akses, 6 keamanan, 7 audit, 11 riwayat dokumen
  level 1. Di luar cakupan: 4 trace, 5 metrik, 8 proses/job, 9 komunikasi keluar, 10 aktivitas pengguna.
- **(cek) Audit level 1 dulu:** `AddEntry` eksplisit + helper `AuditDiff.Of(before, after)` untuk diff manual.
  Diff otomatis lewat registrasi entitas (`AddEntityAudit` + interceptor `ChangeTracker`) **tidak** di plan ini.
- **(cek) Provider:** semua kode store memakai EF Core lewat `UseEmProvider`, jadi tidak terikat SQL Server. Skrip
  DDL MSSQL dibuat di Fase 2; skrip MySQL dan PostgreSQL di Fase 9. Sampai Fase 9 selesai, Log Manager menolak
  provider selain MSSQL dengan pesan "not supported yet".
- **Multi store (daftar store):** model daftar store disiapkan di pengaturan sejak awal. Tahap ini hanya satu store
  tulis untuk semua jenis + nol atau lebih store baca (Fase 8). Store tulis terpisah per jenis dan alat pindah data
  di luar cakupan.
- **Keamanan + audit satu tabel:** dibedakan lewat kelompok (`System.Auth` dll.), bukan tabel terpisah.
- **Audit fitur lama:** audit NuPak (`ta_NuPakAudit`) dan object storage dibiarkan di tabelnya. Kejadian hapus di
  registry/NuPak/object storage masuk katalog sistem mulai sekarang (Fase 5), tanpa migrasi data lama.

**Telemetry server**

- Provider buatan sendiri (tanpa Serilog), karena format, antrean, dan sink milik engine.
- Baris `RequestTrace` yang disimpan: **DONE, ABORT, REJECT** saja. IN/START tetap tampil di console tetapi tidak
  disimpan (opsi `log.storeRequestStartLines`, bawaan `false`).
- Endpoint mentah (`/v2`, NuPak, `/cdn`, `/store`) tidak dicatat sebagai log akses tahap ini.
- Level log diatur lewat `appsettings.json` (`Logging:EmLog:LogLevel`); ubah level tanpa restart di luar cakupan.
- Tidak ada file log harian terpisah di server. Segmen disk hanya dipakai sebagai cadangan saat store tidak bisa
  ditulis.

**Audit**

- Sign-in **berhasil** dicatat di audit (`System.Auth/signin.success`), sesuai penegasan pengguna (login/logout).
- Audit akun dan sistem **selalu aktif**, tidak bisa dimatikan host.
- `System.Role` diaudit (role dan claim role).
- Tab Activity User Editor menggabungkan audit dan keamanan menurut waktu (satu tabel, jadi satu query).
- Ringkasan dan label kejadian berasal dari katalog (server), bahasa Inggris sesuai aturan string UI di
  `claude.md`. Label field memakai label registrasi atau `[Display]`, jatuh ke nama kolom.
- Snapshot dan field hitungan (`nTotal`) diputuskan modul per kejadian.
- Kejadian dari UI (`fromClient`, mis. ekspor/cetak lokal) didukung lewat `PostMeta_AuditEntry`; "dibuka/dilihat"
  tidak dicatat.
- **(cek) Outbox:** bila store = database lain, baris audit di database aplikasi dihapus setelah berhasil dipindah
  (keputusan 11 diterapkan juga ke outbox server). Pembaca menggabungkan store + baris yang belum terpindah.
- Tabel audit di database modul lain (`AddExtraDbConn`) dibuat dengan menjalankan skrip outbox di database itu;
  engine memeriksa skema saat start untuk setiap koneksi yang dipakai context dengan audit.

**Retensi**

- Bawaan mengikuti tabel "Aturan yang bisa diatur" di catatan ide (telemetry 7/30/90 hari, akses 30, error 180 hari
  sejak terakhir muncul, audit sistem 24 bulan dengan batas bawah 12, audit modul tidak pernah dihapus kecuali
  diatur, dengan `MinRetention` dari registrasi).
- Arsip ke object storage dan legal hold di luar cakupan; tindakan kedaluwarsa = hapus.

**Store dan Log Manager**

- **Dua credential:** credential pemilik hanya di dialog Prepare, tidak disimpan; credential runtime disimpan
  terenkripsi (pola `SmtpSecrets`).
- Perubahan store berlaku **setelah restart** (pola pengaturan terkelola); pengaturan retensi berlaku tanpa restart.
- Log Manager adalah navigasi `admin.logmanager` di WPF yang sudah ada; login memakai akun `Em.Api` itu sendiri.
- Tab tahap ini: Ringkasan, Audit, Keamanan, Error, Log, Pengaturan (Database, Retensi). Tab Trace, ekspor
  CSV/Excel, alert SMTP, dan penugasan error di luar cakupan. Error punya status (baru, diketahui, selesai,
  diabaikan) tanpa penugasan.
- Pembatasan baca per aplikasi tidak diperlukan karena satu instalasi.

**Client**

- Pengiriman dipicu setelah login, berkala selama sesi (bawaan 60 detik), dan sebelum sign out.
- Client mengirim error/crash, warning, dan diagnostik level Information ke atas (bisa diatur host). Nama mesin dan
  user ikut terkirim; isi body/form tidak pernah dicatat.

## 3. Konsep dan aturan inti

### 3.1 Amplop event (`LogEvent`, `Em.Libs`)

Satu model untuk semua telemetry, bidangnya bisa dipetakan ke OTLP kelak (keputusan 12):

`Time` (UTC), `Kind` (`Diagnostic`, `Error`, `Access`), `Level`, `Category`, `Template`, `Message`,
`Properties` (JSON), `Exception` (teks), `Fingerprint` (untuk error), `App`, `AppVersion`, `Instance`,
`Environment`, `Tenant` (kosong tahap ini), `TraceId`, `SpanId`, `RequestId`, `SessionId`, `UserId`, `Account`,
`ActingAs`, `ClientAddress`, `Source` (`server`/`client`).

### 3.2 Entry audit (`AuditEntry`)

`Id` (ULID), `Group` (`Invoice`, `System.Auth`), `Kind` (`created`, `signin.failed`), `Time` (UTC),
`ActorType` (`user`, `robot`, `debug`, `system`, `anonymous`), `ActorUserId`, `ActorAccount`, `ActingAs`,
`TargetType`, `TargetKey` (primary key EF; kunci gabungan sebagai array JSON berurutan), `DocNo`, `Summary`,
`Reason`, `Changes` (JSON daftar `{row, field, label, old, new, oldText, newText, secret}`), `Snapshot` (JSON,
opsional), `Detail` (JSON bebas kecil), `Source` (`server`/`client`), `App`, `Instance`, `ClientAddress`,
`RequestId`, `TraceId`, `SessionId`.

**Urutan dan Seq.** Catatan ide memakai `Seq` global. Karena audit bisa berasal dari beberapa database (database inti
dan database modul), plan ini memakai: `nLogAuditSeq` identity **per database**, sedangkan urutan baca =
(`Time` desc, `Id` desc). ULID dibuat dengan komponen waktu sehingga urutan stabil. Cursor halaman berisi posisi
(`Time`, `Id`) per sumber.

### 3.3 Aturan data rahasia

Password, token, secret robot, API key, connection string, password SMTP, dan isi body request tidak pernah masuk
event. Field yang dideklarasikan rahasia (`.Secret(...)` atau `AuditDiff` dengan daftar secret) ditulis
`(set)`/`(changed)` tanpa nilai. Pesan ke client untuk sign-in gagal tetap "Incorrect username or password.".

## 4. Skema database

Semua tabel memakai prefix `ta_Log*`, PK `char(26)` ULID, waktu UTC, mengikuti Dahlia Convention
(`ustamp`/`datestamp`/`json_object` pada tabel yang bisa diubah). Skrip idempoten, satu transaksi.

### 4.1 Outbox/audit — `doc/sqlscript/mssql/tables/070-log-audit.sql`

Dijalankan di **database inti** dan di setiap database modul yang memakai audit. Bila store = database internal,
tabel ini di database inti sekaligus store audit.

- **`ta_LogAudit`**: kolom §3.2 + `nLogAuditSeq bigint identity` (unik), `cLogAuditShipState int`
  (0 lokal/store, 1 menunggu kirim). Indeks: (`cLogAuditGroup`, `cLogAuditTargetKey`, `dLogAuditTime` desc),
  (`cLogAuditGroup`, `dLogAuditTime` desc), (`cLogAuditGroup`, `cLogAuditDocNo`), (`cLogAuditActorUserId`,
  `dLogAuditTime` desc), (`cLogAuditSessionId`), filtered index untuk `ShipState = 1`.
- Append-only: tanpa `ustamp`; skrip menyarankan (komentar) hak INSERT/SELECT/DELETE saja untuk akun aplikasi.

### 4.2 Store log — `doc/sqlscript/mssql/tables/071-log-store.sql`

Dijalankan di database store (internal = database inti). Juga disimpan embedded di `Em.Api.Core` untuk Prepare.

- **`ta_LogSchema`**: versi skema store (satu baris), dibaca Test koneksi/Prepare/startup.
- **`ta_LogEvent`**: kolom §3.1, `nLogEventSeq bigint identity` PK clustered (insert berurutan), indeks
  (`dLogEventTime`), (`cLogEventKind`, `cLogEventLevel`, `dLogEventTime`), (`cLogEventRequestId`),
  (`cLogEventFingerprint`, `dLogEventTime`).
- **`ta_LogError`**: kelompok error per fingerprint: `cLogErrorFingerprint` unik, jenis exception, frame teratas,
  pesan contoh, `nLogErrorCount`, pertama/terakhir terlihat, app/versi terakhir, jumlah user terdampak (perkiraan),
  `cLogErrorState int` (1 New, 2 Acknowledged, 3 Resolved, 4 Ignored; Resolved yang muncul lagi → New), stamps.
- **`ta_LogIngestBatch`**: `cLogIngestBatchId` PK (id batch dari client), waktu terima, pengirim, jumlah event.
  Untuk idempotensi kiriman ulang; dibersihkan job retensi setelah 7 hari.
- **`ta_LogAudit`**: bentuk sama dengan §4.1 (store audit). Saat store = internal, tabel §4.1 dan §4.2 adalah tabel
  yang sama; skrip memakai `IF OBJECT_ID(...) IS NULL` sehingga aman dijalankan keduanya.

### 4.3 Pengaturan (database inti, `ta_Meta`)

- `Em.Log.Stores`: daftar store (§9) dengan password runtime terenkripsi (kunci di `ta_Meta`, pola `SmtpSecrets`).
- `Em.Log.Retention`: aturan retensi (§10).
- `Em.Log.Retention.LastRun`: ringkasan run terakhir.

### 4.4 View

Tidak ada view baru; Log Manager membaca lewat EF. (Engine lain memakai view untuk adapter lama, yang tidak ada di
sini.)

## 5. Lapisan kode

### 5.1 Backend (`src/backend/Em.Api.Core/Api/Log/`)

| File | Isi |
| --- | --- |
| `LogEvent.cs` (di `Em.Libs`) | Amplop §3.1, `LogEventKind`, `LogSender` (`Process`, `User`, `Robot`) |
| `ILogIngest.cs`, `LogIngest.cs` | `WriteAsync(IReadOnlyList<LogEvent>, LogSender)`: validasi, enrich, hitung fingerprint, upsert `ta_LogError`, bulk insert `ta_LogEvent`. Satu-satunya jalan masuk telemetry |
| `EmLogProvider.cs`, `EmLogger.cs` | `ILoggerProvider`: ubah entri jadi `LogEvent` (template + properti + scope), masuk `Channel` berbatas. Tidak pernah memblokir atau melempar. Antrean penuh → buang Debug/Information dulu, hitung jumlah dibuang |
| `LogWriterBackground.cs` | Hosted service: kuras channel per 1 detik / 500 event → `ILogIngest`. Gagal → tulis segmen JSONL ke `<logPath>/outbox/` (batas total bawaan 500 MB, segmen terlama dibuang dan dicatat); segmen dicoba lagi berkala. Log yang dihasilkan di dalam writer/ingest tidak masuk kembali ke channel (guard `AsyncLocal`) |
| `ErrorFingerprint.cs` | Jenis exception + frame teratas yang bukan frame framework → hash pendek |
| `LogStoreDbContext.cs` | Context store (`ta_LogEvent`, `ta_LogError`, `ta_LogIngestBatch`, `ta_LogAudit`, `ta_LogSchema`), koneksi bernama `EmLogStore` |
| `LogStoreSettings.cs` | Baca/simpan `Em.Log.Stores`, enkripsi password, override env `EM_LOG_DB_CONNECTION`/`EM_LOG_DB_PROVIDER`; mendaftarkan `AddExtraDbConn("EmLogStore", ...)` saat start (internal → koneksi "Default") |
| `LogStorePrepare.cs` | Langkah Prepare §9.2, skrip embedded per provider, laporan per langkah |
| `Audit/AuditCatalog.cs` | Katalog kelompok + kejadian (label, `requireReason`, `fromClient`, `MinRetention`, `CanRead`), dibekukan saat start. Prefix `System.` dicadangkan untuk engine |
| `Audit/AuditBuilder.cs` | `builder.AddAudit(group, a => a.Title(...).Entry(...).CanRead(...).MinRetention(...))` |
| `Audit/IAuditWriter.cs`, `AuditWriter.cs` | Scoped per request. `AddEntry(...)` menitipkan entry; `Use(DbContext)` mengarahkan; `WriteNowAsync(...)` menulis langsung dalam transaksi sendiri ke database inti. Validasi katalog dan alasan wajib di `AddEntry` (exception sebelum data tersimpan) |
| `Audit/AuditSaveInterceptor.cs` | `SaveChangesInterceptor` yang dipasang ke setiap context dari `EmAppBuilder.AddDbContext`: pada `SavingChanges` menambahkan entry tertunda (yang diarahkan ke context ini, atau semua bila hanya satu context) sebagai `ta_LogAudit` ke context yang sama. Context kedua yang menyimpan saat masih ada entry tak diarahkan → exception dengan nama entry dan context (keputusan 7) |
| `Audit/AuditDiff.cs` | `AuditDiff.Of(before, after, ignore, secret, labels)` → daftar perubahan field; label dari `[Display]` |
| `Audit/AuditRequestGuard.cs` | Akhir request: entry yang belum tertulis → error diagnostik "audit entry not saved" (bug modul terlihat saat uji) |
| `Audit/AuditShipper.cs` | Hosted service: bila store ≠ database sumber, pindahkan `ta_LogAudit` (ShipState = 1) per batch ke store berurutan, idempoten per `Id`, hapus di sumber setelah berhasil. Sumber = database inti + koneksi modul yang punya context dengan audit |
| `Audit/IAuditReader.cs`, `AuditReader.cs` | Pembaca gabungan untuk modul (tanpa HTTP): `ByDoc`, `ByDocNo`, `ByType`, `Since`; gabung store + outbox yang belum terpindah (+ store baca di Fase 8), cursor buram, `Warnings` bila sumber tidak tersedia; terapkan `CanRead` |
| `Audit/SystemAudits.cs` | Class audit internal: `AuthAudit`, `PasswordAudit`, `UserAudit`, `RoleAudit`, `RobotAudit`, `SettingsAudit`, `StorageAudit` (registry/NuPak/object storage) |
| `LogQuery.cs` | Query Log Manager yang dibatasi ke operasi tersedia di ketiga provider; rentang waktu wajib; teks dengan `LIKE` |
| `LogRetention.cs`, `LogRetentionBackground.cs` | Job harian (jam diatur), hapus per batch kecil, pratinjau, catat `System.Settings/retention.run` |
| `LogServices*.cs` | Action (§7) |
| `EmAppBuilder.Log.cs` (di `Api/Shared/`) | `AddLogServer()` |

Penyesuaian engine yang ada:

- `EmApp.BuildApp`: provider `EmLog` dipasang di sebelah console bila log server aktif. Logger bootstrap
  `EmAppBuilder` (`BootstrapLoggerFactory`) menahan entri di memori dan menyerahkannya ke provider saat siap, agar
  error konfigurasi saat start ikut tersimpan.
- `RequestTrace`: id request unik `<instance>:<id pendek>:<waktu start>` di properti `RequestId` (console tetap
  `#0001A`); `BeginScope` berisi `RequestId`, `TraceId` (`Activity.Current`), `UserId`, `Account`, `SessionId`;
  properti terpisah `UserId`, `Account`, `DebugKey`, `Impersonating`; properti `Phase` (`IN`/`START`/`DONE`/...)
  agar provider bisa memilih baris yang disimpan dan menandai `Kind = Access`.
- `ServicesBase`: properti `Audit` (`IAuditWriter`) untuk service engine; modul memakai class audit dari DI.
- `EmAppBuilder.AddDbContext`/`AddDbContextFactory`: memasang `AuditSaveInterceptor` (selain interceptor yang sudah
  ada) dan mencatat koneksi yang dipakai, untuk cek skema audit dan sumber shipper.
- Action ingest dan query Log Manager tidak menghasilkan baris akses tersimpan (level `Debug` untuk route modul
  `Log`, guard "log tentang log").

### 5.2 Kontrak (`src/shared/Em.Libs/Api.Core.Models/`)

`ILogServices` (action + konstanta claim), `IAuditServices` (activity untuk modul/UI), DTO: `LogEvent`,
`LogIngestBatch`, `LogEventPage`, `LogErrorGroup`, `AuditActivity`, `AuditActivityPage` (`Items`, `NextCursor`,
`Warnings`), `AuditFieldChange`, `LogStoreInfo`, `LogStoreTestResult`, `LogPrepareStep`, `LogRetentionRules`,
`LogRetentionPreview`, `LogSummary`.

### 5.3 Client (`src/shared/Em.Ui.Core`, WPF, MAUI)

| Bagian | Isi |
| --- | --- |
| `Em.Ui.Core/.../ClientLogProvider.cs` | `ILoggerProvider` client: event → segmen JSONL lokal di `%LocalAppData%\<ApplicationName>\Logs\outbox\` (MAUI: `FileSystem.AppDataDirectory`), selalu, juga sebelum login dan saat offline. Batas folder bawaan 200 MB; segmen terlama dibuang dan jumlahnya dilaporkan di kiriman berikutnya |
| `Em.Ui.Core/.../ClientLogShipper.cs` | Setelah login, berkala, dan sebelum sign out: kirim segmen per batch (id batch unik) lewat `PostMeta_LogIngest`; berhasil → hapus segmen; gagal → tetap |
| `Em.Ui.Core/Api.Core/LogService.cs`, `AuditService.cs` | Proxy action (pola `ApprovalService`) |
| `Em.Ui.Wpf.Core/Core/EmApp.cs` | Pasang provider + handler `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`, `AppDomain.UnhandledException` yang menulis event `Error` sebelum menampilkan pesan; hook login/sign-out ke shipper; menu "Open log folder" |
| `Em.Ui.Maui.Core/Core/EmApp.cs` | Setara WPF, tanpa layar Log Manager |

Setiap event client membawa user **saat event terjadi** (atau kosong untuk "belum login"); server mencatat pengirim
batch terpisah dan mengisi ulang alamat/waktu terima, tidak mempercayai identitas dari body.

## 6. Alur inti

1. **Telemetry server:** `ILogger.Log` → `EmLogProvider` → channel → `LogWriterBackground` → `ILogIngest` → store.
   Store gagal → segmen disk → dicoba lagi.
2. **Telemetry client:** `ILogger` client → segmen lokal → (login) `ClientLogShipper` → `PostMeta_LogIngest`
   (claim: user login apa pun) → cek `ta_LogIngestBatch` → `ILogIngest` dengan `LogSender.User`.
3. **Audit modul:** `audit.NewInvoice(...)` → `IAuditWriter.AddEntry` → `SaveChanges` → interceptor menambah
   `ta_LogAudit` dalam transaksi yang sama → (store di database lain) `AuditShipper` memindahkan lalu menghapus.
4. **Audit sistem tanpa perubahan data** (sign-in gagal, debug impersonasi): `WriteNowAsync` → `ta_LogAudit` di
   database inti (transaksi sendiri), lalu ikut shipper seperti biasa.
5. **Baca activity:** control/VM → `GetMeta_AuditActivity_ByDoc(docType, docKey, cursor, take)` → `AuditReader`
   (cek `CanRead`) → gabung sumber → halaman + `NextCursor`.

## 7. Action dan claim

Claim baru (didaftarkan `AddLogServer`): `Log Manager Access`, `Log Telemetry Read`, `Log Audit Read`,
`Log Security Read`, `Log Manager Settings`.

| Action | Claim |
| --- | --- |
| `PostMeta_LogIngest(LogIngestBatch)` | User login (tanpa claim khusus) atau robot |
| `GetMeta_LogSummary(from, to)` | Access + Telemetry Read (bagian audit hanya bila Audit Read) |
| `GetMeta_LogEvents(from, to, kind, minLevel, category, text, requestId, userId, cursor, take)` | Telemetry Read |
| `GetMeta_LogErrors(from, to, state, cursor, take)`, `GetMeta_LogErrorDetail(fingerprint)` | Telemetry Read |
| `PostMeta_LogErrorState(fingerprint, state)` | Telemetry Read |
| `GetMeta_LogAudit(from, to, group, kind, targetKey, docNo, actorUserId, sessionId, cursor, take)` | Audit Read (kelompok `System.Auth`/`System.Password` butuh Security Read) |
| `GetMeta_AuditCatalog()` | Access |
| `GetMeta_LogStores()`, `PostGetMeta_LogStoreTest(...)`, `PostGetMeta_LogStorePrepare(...)`, `PostMeta_LogStoreSave(...)` | Settings |
| `GetMeta_LogRetention()`, `PostGetMeta_LogRetentionPreview(rules)`, `PostMeta_LogRetentionSave(rules)` | Settings |
| `IAuditServices.GetMeta_AuditActivity_ByDoc/ByDocNo/ByType/Since` | `CanRead` kelompok (bukan claim Log Manager) |
| `IAuditServices.PostMeta_AuditEntry(group, key, kind, detail)` | `CanRead` kelompok + kejadian `fromClient` |

Parameter GET berupa nilai sederhana per field (lihat `doc/ideas/get-parameter-per-field.md`). Parameter
`from`/`to` wajib untuk query telemetry dan audit lintas dokumen; rentang maksimum bawaan 31 hari per query.

## 8. Katalog audit sistem (Fase 5)

| Kelompok | Kejadian | Titik catat |
| --- | --- | --- |
| `System.Auth` | `signin.success`, `signout`, `signout.all`, `session.revoked` | Di dalam `TokenServices` (`IssueAsync`, `RevokeAsync`, `RevokeAllAsync`), dengan sebab dari pemanggil |
| `System.Auth` | `signin.failed` (`reason`: account/state/password; `accountTried`), `token.rejected`, `session.expired` | `CredentialServices.PostGetMeta_SignIn` sebelum 401; gate token di `EmApp`; refresh ditolak. `WriteNowAsync` |
| `System.Auth` | `debug.accepted`, `debug.impersonate` | Gate debug token di `EmApp` (menggantikan komentar "Until an audit system exists...") |
| `System.Password` | `changed.self`, `reset.byAdmin`, `admin.changed`, `admin.reset` | `PostMeta_ChangeMyPassword`, `PostMeta_ResetPassword`, `PostMeta_ChangeAdminPassword`, `PostMeta_ResetAdminPassword` |
| `System.User` | `created`, `updated`, `deleted`, `state.changed`, `admin.toggled`, `role.granted`, `role.revoked`, `claim.granted`, `claim.revoked` | `PostTa_User_*`, `PostTa_UserRole_*`, `PostTa_UserClaim_*` (diff manual lewat `AuditDiff`) |
| `System.Role` | `created`, `updated`, `deleted`, `claim.added`, `claim.removed` | `PostTa_Role_*`, `PostTa_RoleClaim_*` |
| `System.Robot` | `created`, `updated`, `deleted`, `regenerated`, `access.set` | `RobotServices` |
| `System.Settings` | `smtp.changed`, `storage.changed`, `logstore.changed`, `logstore.prepared`, `retention.changed`, `retention.run` | Pengaturan SMTP/storage, Log Manager, job retensi |
| `System.Registry` | `tag.deleted`, `manifest.deleted`, `gc.run`, `deploy.target.deleted`, `deploy.run` | `CtnManifestDeletion`, `CtnGarbageCollector`, deploy |
| `System.NuPak` | `feed.deleted`, `prefix.deleted`, `version.purged` | `NuPakServices` (audit lama tetap ditulis) |
| `System.ObjectStorage` | `bucket.deleted`, `object.deleted`, `version.purged`, `policy.deleted`, `volume.deleted` | `ObjectStorageServices` |

Pelaku diisi engine dari identitas request (`user`, `robot`, `debug` + `actingAs`, `system` untuk job, `anonymous`).
Bila `accountTried` cocok dengan user yang ada, `TargetKey` diisi user itu agar tampil di Activity-nya. Nama
kejadian final mengikuti action yang benar-benar ada saat eksekusi; tambahan/pengurangan dicatat di laporan.

Konsumen pertama di UI: tab **Activity** User Editor yang sekarang mockup tersembunyi
(`doc/ideas/user-editor-activity.md`) diisi dari `GetMeta_AuditActivity_ByDoc("System.User", cUserId, ...)`
digabung dengan kelompok `System.Auth`/`System.Password` untuk target user yang sama (satu query dengan filter
target, bukan per kelompok).

## 9. Store log dan Log Manager > Pengaturan > Database

### 9.1 Model

Daftar store di `Em.Log.Stores`: id, nama, `Internal` atau koneksi (provider, server, port, database, auth,
user, password runtime terenkripsi, opsi TLS), peran (`Write`/`Read`/`Detached`), waktu event terlama/terbaru
(diperbarui job retensi). Tahap ini tepat satu `Write`. Bawaan saat belum ada pengaturan: satu store `Internal`.
Env override `EM_LOG_DB_CONNECTION` + `EM_LOG_DB_PROVIDER` menggantikan store tulis; layar menampilkan nilai aktif
tanpa tombol Simpan.

### 9.2 Prepare (dialog terpisah, credential pemilik tidak disimpan)

1. Koneksi dengan credential pemilik.
2. Buat database bila dicentang dan belum ada.
3. Jalankan skrip store (§4.2) sesuai provider; tidak pernah `DROP`.
4. Upgrade berurutan bila `ta_LogSchema` lebih lama; tolak bila lebih baru dari engine.
5. Beri hak runtime (opsional, bila user runtime diisi): SELECT/INSERT/UPDATE pada `ta_LogError`, SELECT/INSERT/
   DELETE pada tabel lain.
6. Verifikasi: tulis, baca, hapus satu event uji.

Setiap langkah dilaporkan (status + pesan). Hasil dicatat `System.Settings/logstore.prepared`.

### 9.3 Simpan

Ditolak bila skema store tujuan belum siap. Berlaku setelah restart; layar menampilkan "Active now" vs "Saved
(restart required)". Pindah store tidak memindahkan data; outbox audit yang belum terkirim masuk ke store baru.

## 10. Retensi (Log Manager > Pengaturan > Retensi)

- Aturan per cakupan: telemetry per level (hari + batas GB), akses, error (hari sejak terakhir muncul), audit
  sistem per kelompok (bulan, batas bawah 12), audit modul per kelompok (bulan atau "never", batas bawah dari
  `MinRetention`), `ta_LogIngestBatch` (tetap 7 hari).
- Disimpan di `Em.Log.Retention`, berlaku tanpa restart; perubahan dicatat `retention.changed` (lama/baru).
- Job harian (jam bawaan 02:00 waktu server), hapus per batch 5.000 baris dengan jeda; batas GB telemetry
  diterapkan dengan menghapus event terlama sampai di bawah batas (perkiraan ukuran dari jumlah baris × rata-rata).
- Tombol **Preview** menghitung baris yang akan dihapus untuk aturan di layar sebelum disimpan.
- Job berjalan di store tulis dan semua store baca (Fase 8).

## 11. Layar WPF — Log Manager

Navigasi `admin.logmanager` (Administrative Tools, card home), `EmTabStrip`, gaya sama dengan Object Storage
Manager. Pemilih rentang waktu di atas berlaku untuk semua tab.

| Tab | Isi |
| --- | --- |
| Summary | Error 24 jam, error baru, volume audit, event dibuang, antrean outbox audit yang belum terpindah, status store aktif. Klik angka → tab terkait dengan filter |
| Audit | Filter kelompok, kejadian, kunci/`DocNo`, user, sesi. Tabel entry; detail: ringkasan, alasan, diff (lama → baru dengan teks tampilan), snapshot (JSON terformat), tautan "View request" (tab Log difilter `RequestId`) |
| Security | `System.Auth` + `System.Password`; sign-in gagal dikelompokkan per akun/alamat per 5 menit |
| Errors | Kelompok fingerprint: jumlah, pertama/terakhir, app/versi, user terdampak, status. Detail: stack trace, contoh kejadian, ubah status |
| Log | Penjelajah event: kind, level, kategori, teks, request id, user; kolom pesan sama seperti console |
| Settings | Sub-tab Database (§9) dan Retention (§10) |

Tab yang tidak diizinkan claim user disembunyikan. Tail live, Trace, ekspor, alert: di luar cakupan.

Contoh kontrol activity untuk modul: tidak dibuat control engine baru (keputusan di catatan ide). Panduan
`engine-log.md` memuat contoh VM `LoadFirstAsync`/`LoadMoreAsync` dengan cursor.

## 12. Fase eksekusi

Sesuai aturan `claude.md`: tulis kode semua fase dulu, build per fase bila murah; pengujian, review, dan analisa
sekali di akhir (Fase 10).

1. **Fondasi kontrak dan konfigurasi:** DTO/kontrak di `Em.Libs`, `EmApiConfig` (`storage.logPath`, bagian `log`),
   `AddLogServer()` + `"modules": { "logServer": ... }` / `EM_MODULE_LOGSERVER` di host contoh `Em.Api`
   (bawaan menyala), claim.
2. **Skema MSSQL dan store:** `070-log-audit.sql`, `071-log-store.sql`, `LogStoreDbContext`, `LogStoreSettings`
   (internal saja dulu), cek skema saat start (gagal jelas bila tabel tidak ada, seperti object storage).
3. **Telemetry server:** `ILogIngest`, `EmLogProvider`, writer background + segmen cadangan, fingerprint,
   penyesuaian `RequestTrace` (§5.1), bootstrap logger.
4. **Audit inti:** katalog, `AddAudit`, `IAuditWriter` (`AddEntry`, `Use`, `WriteNowAsync`), interceptor,
   `AuditDiff`, guard akhir request, `AuditReader` (store + outbox), `IAuditServices`, `PostMeta_AuditEntry`;
   modul `Em.Test` mendapat contoh pemakaian (`TestAudit`) sebagai acuan dan bahan uji.
5. **Audit sistem:** katalog §8 dan titik catatnya; tab Activity User Editor diaktifkan.
6. **Client:** `ClientLogProvider`, `ClientLogShipper`, handler exception WPF/MAUI, `PostMeta_LogIngest` dengan
   idempotensi batch, menu "Open log folder".
7. **Store database lain + shipper + Log Manager:** Test/Prepare/Save koneksi MSSQL, dua credential, env override,
   `AuditShipper`; layar Log Manager (§11) dan query `LogQuery`.
8. **Retensi dan multi store:** pengaturan + job + pratinjau; store baca, pembaca gabungan lintas store, cursor per
   store, `Warnings`.
9. **MySQL dan PostgreSQL:** skrip `doc/sqlscript/mysql/` dan `doc/sqlscript/postgresql/` untuk §4.1 dan §4.2
   (embedded juga), Prepare per provider, uji translasi query EF (`ToQueryString`) untuk ketiga provider; buka
   pilihan provider di layar.
10. **Uji, review, dokumentasi:** §13; `doc/engine/engine-log.md` (+ cara menjalankan skrip, claim, contoh modul);
    pembaruan `claude.md`; status catatan ide; laporan eksekusi di plan ini, lalu pindahkan ke `plan/executed/`.

## 13. Pengujian

- **Unit (`Em.Api.Core.Tests`):** provider (template → properti, scope, antrean penuh membuang level rendah,
  tidak melempar), fingerprint stabil, katalog (kejadian tak terdaftar, alasan wajib, prefix `System.` ditolak
  untuk modul, `fromClient`), `AuditDiff` (secret, ignore, label `[Display]`), aturan `Use(ctx)` (dua context →
  exception), cursor gabungan (tanpa duplikat, urutan stabil, sumber mati → `Warnings`), validasi retensi (batas
  bawah, `MinRetention`), redaksi data rahasia.
- **Integrasi SQL Server (`Em.Api.Core.IntegrationTests`):** entry audit ikut commit/rollback, `WriteNowAsync`,
  shipper pindah-lalu-hapus dan idempoten, ingest batch ganda, Prepare idempoten di database kosong dan database
  yang sudah ada, job retensi per batch.
- **Client (`Em.Ui.Core.Tests`):** segmen lokal, batas folder, kirim berhasil menghapus, gagal mempertahankan.
- **WPF (`Em.Ui.Wpf.Core.Tests`):** VM Log Manager dan render XAML terang/gelap (harness di
  `..\.artefacts\em-system\scripts\log-manager-render`).
- **Smoke HTTP** terhadap `Em.Api` lokal (harness `..\.artefacts\em-system\scripts\log-smoke`): sign-in
  berhasil/gagal tercatat, ingest client, query Log Manager per claim.
- Lingkungan tanpa SQL Server/Windows: tulis dan kompilasi test, jalankan yang bisa, catat yang belum dijalankan
  dan siapkan langkah manual `.ps1` di `plan/penyimpanan-log-manual/` (aturan tindakan manual di `claude.md`).

## 14. Di luar cakupan

OTLP (wrapper kelak lewat `ILogIngest`), log server pusat per pelanggan/vendor (topologi B/C/D) dan pengiriman audit
antar-server, trace/span dan propagasi `traceparent`, metrik, aktivitas pengguna, log proses/job dan komunikasi
keluar sebagai jenis tersendiri, diff otomatis `AddEntityAudit`, feed perubahan dengan cursor per konsumen,
tail live (menunggu stream dua arah), alert SMTP, ekspor, arsip ke object storage, legal hold, store tulis per jenis,
alat pindah data antar store, ubah level log tanpa restart, migrasi `ta_NuPakAudit`, log akses endpoint mentah,
hash berantai, layar Log Manager di MAUI.

## 15. Risiko

- **Beban tulis audit di transaksi bisnis:** satu INSERT tambahan per entry; indeks dijaga minimal (§4.1).
- **Volume telemetry di database inti** (store internal): mitigasi dengan baris akses hanya DONE/ABORT/REJECT,
  Debug tidak disimpan, retensi + batas GB, dan pilihan pindah ke database lain.
- **Interceptor di semua context:** salah urutan dengan `ApprovalDatabaseSwitch` atau context yang tidak punya tabel
  audit. Mitigasi: interceptor no-op tanpa entry tertunda; cek skema hanya untuk koneksi yang benar-benar menulis
  audit, dengan pesan error yang menyebut skrip.
- **Rekursi log:** log dari ingest/writer/shipper masuk ke dirinya. Mitigasi guard `AsyncLocal` + level Debug untuk
  route `Log`.
- **Satu instance per store:** shipper dan job retensi mengasumsikan satu instance API (sama dengan object
  storage); multi-replika butuh lease dan dicatat sebagai batasan di panduan.
- **Perbedaan provider** (JSON, paging, identity): query dibatasi ke operasi bersama dan diuji per provider di Fase 9.

## 16. Laporan eksekusi

(Diisi saat plan dieksekusi.)
