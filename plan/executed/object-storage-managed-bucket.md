# Object storage — managed bucket (bucket milik aplikasi)

- **Tanggal:** 2026-10-15
- **Status:** dieksekusi 2026-10-15 (lihat [Laporan eksekusi](#laporan-eksekusi)); semua keputusan sudah dijawab pengguna (lihat [Keputusan final](#keputusan-final))
- **Eksekutor:** Claude Code
- **Dasar:** engine object storage v1 ([plan/executed/object-storage.md](../executed/object-storage.md)),
  panduan [doc/engine/engine-object-storage.md](../../doc/engine/engine-object-storage.md)
- **Plan pasangan di repo turunan:** repo produk privat yang memakai engine ini menulis plan penyesuaiannya sendiri
  (deklarasi bucket di modulnya, penghapusan pemeriksaan bucket manual). Plan ini tetap generik dan tidak menyebut
  produk, modul bisnis, server, atau database turunan mana pun (repo publik).

## Latar belakang

Pada v1 semua bucket dibuat administrator lewat Object Storage Manager. Modul yang menyimpan file harus meminta admin
membuat bucket bernama persis sesuai harapan modul, lalu memeriksa sendiri saat runtime bahwa bucket itu ada dan
policy-nya Private, karena admin tetap bisa menghapus bucket, memindahkannya ke policy lain, atau mengubah akses policy
menjadi PublicRead. Sistem file lama yang ingin digantikan engine ini berjalan *zero-setup*: aplikasi cukup menyimpan
file, dan tempat penyimpanannya hanya diatur di konfigurasi host.

Konsep baru: **managed bucket** adalah wadah yang **dikelola oleh layer aplikasi** (modul). Modul mendeklarasikan bucket
beserta nilai awal policy-nya di builder. Engine membuatnya saat startup dan menguncinya. Admin hanya mengurus sisi
infrastruktur (volume, replika, retensi, kuota, ukuran maksimum) dan boleh melihat serta mengunduh isinya, tetapi tidak
boleh mengubah isinya.

## Keputusan final

Semua dijawab pengguna 2026-10-15. Selama eksekusi tidak ada pertanyaan lagi; hal yang tidak tercakup diputuskan dengan
pilihan yang paling konsisten dengan daftar ini lalu dicatat di laporan eksekusi.

| # | Topik | Keputusan |
| --- | --- | --- |
| 1 | Istilah | **Managed bucket**. API `builder.AddManagedBucket(...)`, label UI "Managed by &lt;owner&gt;". |
| 2 | Policy | **Satu policy khusus per managed bucket**, dibuat engine dari nilai default deklarasi (akses, max object size, retensi, kuota). Policy terikat 1:1: tidak bisa dipakai bucket lain, dan bucket tidak bisa dipindah ke policy lain. |
| 3 | Volume | **Opsi builder + fallback**: `AddObjectStorage(o => o.DefaultVolumePath = "...")`. Bila tidak diisi, dipakai folder relatif `data/objectstore` (relatif ke content root). Engine membuat volume default sekali saja, hanya saat ada policy managed baru yang perlu dibuat. |
| 4 | Sinkronisasi | Default deklarasi **hanya dipakai saat policy dibuat**. Perubahan admin tidak pernah ditimpa. Yang dipaksa setiap startup hanya **akses** (atribut terkunci). |
| 5 | Deklarasi hilang | Bucket **tetap terkunci** dan diberi tanda "not declared" di manager. Admin bisa melepasnya lewat aksi **Release**, setelah itu menjadi bucket biasa. |
| 6 | Isi bucket | Admin hanya bisa **lihat dan unduh**: browse, info, daftar versi, download. Upload, delete, restore, dan purge versi lewat action manajemen ditolak 409. Jalur modul (`IObjectStore`), retensi, GC, replikasi, dan repair tetap berjalan. |
| 7 | Pengaturan | **Infra saja**: admin boleh mengubah volume, replika, retensi, kuota, max object size, state, dan note. Nama, akses, pemindahan policy, serta penghapusan bucket atau policy-nya dikunci. |
| 8 | Bucket bernama sama sudah ada | **Diadopsi otomatis**: bucket ditandai managed dan isinya tetap. Bila policy lamanya juga dipakai bucket lain, engine menyalin policy itu menjadi policy khusus lalu memindahkan bucket ke sana. Akses dipaksa sesuai deklarasi; perubahan akses dicatat di log (warning) dan audit. |
| 9 | Uji visual | **Termasuk render**: harness render dibangun dan **dijalankan** (persetujuan pengguna 2026-10-15 berlaku untuk eksekusi plan ini). |
| 10 | Commit | Commit di akhir, **hanya bila branch aktif `work-bench`**. Kalau bukan, jangan commit dan laporkan. Tanpa push. |
| 11 | Deskripsi | Deklarasi managed bucket **wajib membawa deskripsi yang jelas** (untuk apa bucket itu dan siapa yang mengisinya). Deskripsi milik kode: tampil di Object Storage Manager, tidak bisa diedit admin, dan disamakan dengan deklarasi setiap startup (keputusan pengguna 2026-10-15). |

### Keputusan turunan (diambil saat menulis plan, konsisten dengan keputusan di atas)

- **Deklarasi bebas urutan.** `AddManagedBucket` hanya mengumpulkan deklarasi di builder. Penerapannya dilakukan saat
  startup oleh `ObjectStorageBackground.StartAsync`, setelah pemeriksaan skema. Jadi urutan antara `AddObjectStorage()`
  dan deklarasi modul di `Program.cs` tidak berpengaruh.
- **Object storage dimatikan host.** Kalau `AddObjectStorage()` tidak dipanggil, deklarasi diabaikan tanpa error.
  Modul tetap menangani `IObjectStore` yang tidak tersedia seperti sekarang.
- **Deskripsi.** Parameter wajib `description`, setelah di-trim 10–500 karakter. Disimpan di kolom baru
  `ta_StoreBucket.cStoreBucketDescription` (`nvarchar(500) NULL`; `NULL` untuk bucket biasa), terpisah dari
  `cStoreBucketNote` yang tetap milik admin. Deskripsi diperbarui setiap startup bila berbeda dari deklarasi (berbeda
  dengan nilai policy pada keputusan 4, karena deskripsi bukan pengaturan admin). Setelah Release, deskripsi tetap
  tersimpan sebagai informasi dan tidak lagi disinkronkan. XML comment `AddManagedBucket` menjelaskan bahwa deskripsi
  ditulis untuk administrator (isi bucket, modul pengisi, dampak bila volume/kuota diubah).
- **Validasi deklarasi di builder.** Nama bucket mengikuti `StoreNames.Bucket`. Owner wajib diisi (1–100 karakter).
  Nilai default memakai aturan `ObjectStorageServices.ValidatePolicy` (replika 1/1, satu volume). Nama yang dideklarasikan
  dua kali melempar `InvalidOperationException` di builder.
- **Owner berubah.** Kalau bucket managed sudah ada dengan owner berbeda, owner diperbarui ke nilai deklarasi dan
  dicatat di log (information). Startup tidak gagal.
- **Penanda tersimpan.** Kolom baru `ta_StoreBucket.cStoreBucketOwner` (`nvarchar(100) NULL`; `NULL` = bucket biasa).
  Bucket dihitung "declared" dari daftar deklarasi di memori (satu instance API per database, sesuai asumsi v1),
  sehingga tidak perlu kolom status. Policy khusus tidak diberi kolom: policy dianggap managed bila dipakai bucket yang
  `cStoreBucketOwner`-nya tidak `NULL`. Ini menghindari FK melingkar bucket ↔ policy. Sebelum menamai kolom, cek ulang
  [dahlia-convention.md](../../doc/convention/dahlia-convention.md) bagian *Additional columns*; bila konvensi
  menuntut nama lain, pakai nama konvensi dan catat di laporan.
- **Nama policy dan volume otomatis.** Policy: `<bucket> (managed)`. Volume default: `managed-default`. Id volume
  disimpan di `ta_Meta` dengan key `ObjectStore.DefaultVolume`. Kalau id itu sudah tidak ada (dihapus admin), volume
  dibuat ulang saat dibutuhkan. Kalau nama sudah dipakai objek lain, tambahkan akhiran `-2`, `-3`, dan seterusnya.
  Bila folder volume default tidak bisa dipakai (validasi provider gagal), startup gagal dengan pesan jelas yang
  menyebut `DefaultVolumePath`. Ini hanya terjadi bila ada policy managed yang perlu dibuat.
- **Release.** Action baru `PostMeta_StoreBucketRelease(string bucketId)` dengan claim `Object Storage Settings Manage`.
  Hanya berlaku untuk bucket managed yang **tidak** sedang dideklarasikan (selain itu 409). Mengosongkan
  `cStoreBucketOwner`; policy-nya menjadi policy biasa. Dicatat di audit `System.ObjectStorage` (`bucket.released`).
- **Kontrak DTO.** `StoreBucketInfo` ditambah `string? Owner`, `string? Description`, dan `bool IsDeclared` (`IsManaged => Owner is not null`).
  `StorePolicyInfo` ditambah `string? ManagedBucket` (nama bucket pemilik bila policy managed). Ini perubahan kontrak
  publik `Em.Libs`; catat di bagian *Upgrade notes* panduan. Release note NuGet **tidak** ditulis di plan ini (rilis
  adalah keputusan terpisah).
- **Modul uji `Em.Test`** mendeklarasikan satu managed bucket (`emtest-files`, owner `Em Test`, dengan deskripsi lengkap sebagai contoh), supaya fitur ini
  terlihat di host contoh dan bisa dipakai harness render. Deklarasi ada di `AddTestModule`, jadi ikut mati bila modul
  uji dimatikan.

## Cakupan

### A. Skema database

1. `doc/sqlscript/mssql/tables/060-objectstore.sql`
   - `ta_StoreBucket` mendapat kolom `cStoreBucketOwner nvarchar(100) NULL` dan `cStoreBucketDescription nvarchar(500) NULL`
     di `CREATE TABLE`, plus blok idempotent `IF COL_LENGTH(...) IS NULL ALTER TABLE ... ADD ...` per kolom untuk
     instalasi lama.
   - Baris `ObjectStoreSchemaVersion` menjadi `'2'`: insert `'2'` bila belum ada, dan update `'1'` → `'2'` setelah kolom ada.
   - Perbarui komentar header (arti `cStoreBucketOwner`).
2. `doc/sqlscript/mssql/updates/20261015-StoreManagedBucket.sql`: migrasi database lama (kolom + versi skema 2),
   idempotent.
3. `doc/sqlscript/mssql/views/vi_StoreBucket.sql`: tambah `b.[cStoreBucketOwner]`, `b.[cStoreBucketDescription]`.
4. `ObjectStorageBackground.StartAsync`: versi skema yang diterima `"2"`. Pesan error menyebut skrip update.
   `ObjectStorageDbContext` dan `ta_StoreBucket` (`Em.Libs`) mendapat properti `cStoreBucketOwner` dan
   `cStoreBucketDescription` dengan XML comment bahasa Inggris.

### B. Deklarasi dan opsi (Em.Api.Core / Em.Libs)

1. `ObjectStorageOptions` (public, `Em.Api.Core.ObjectStorage`): `DefaultVolumePath` (default `data/objectstore`).
   Overload `AddObjectStorage(Action<ObjectStorageOptions>? configure = null)`. Signature tanpa argumen tetap ada,
   sehingga host lama tidak rusak.
2. `ManagedBucketOptions` (public): `Access` (default `Private`), `MaxObjectBytes`, `KeepVersions`, `KeepDays`,
   `QuotaBytes`, `QuotaObjects`, `Note`. Semua opsional kecuali akses yang punya default.
3. `EmAppBuilder.AddManagedBucket(string name, string owner, string description, Action<ManagedBucketOptions>? configure = null)` di
   `EmAppBuilder.ObjectStorage.cs`: validasi seperti di keputusan turunan, lalu simpan ke daftar deklarasi singleton
   (`ManagedBucketRegistry`, internal, juga dipakai service untuk `IsDeclared`).
4. XML comment bahasa Inggris untuk semua member public baru (CS1591 aktif).

### C. Penerapan saat startup

Kelas internal baru `ManagedBucketProvisioner` (`Api/ObjectStorage/`), dipanggil dari `ObjectStorageBackground.StartAsync`
setelah pemeriksaan skema dan lease, sebelum loop. Untuk setiap deklarasi, dalam transaksi serializable seperti action
settings:

1. Bucket belum ada → pastikan volume default ada (buat dan simpan id di `ta_Meta` bila perlu, validasi lewat provider),
   buat policy `<bucket> (managed)` dari default deklarasi + volume default, lalu buat bucket dengan owner.
2. Bucket ada, `cStoreBucketOwner` `NULL` → adopsi: set owner; bila policy-nya dipakai bucket lain, salin policy
   (semua nilai + daftar volume berurutan) menjadi `<bucket> (managed)` lalu pindahkan bucket ke salinan itu.
   Audit `bucket.adopted`.
3. Bucket ada dan sudah managed → owner dan deskripsi disamakan bila berbeda. (Pada kasus 1 dan 2 deskripsi juga ditulis.)
4. Semua kasus → bila akses policy berbeda dari deklarasi, paksa ke nilai deklarasi, catat warning di log dan audit
   `bucket.access.enforced`. Nilai policy lain tidak disentuh (keputusan 4).
5. Setelah semua deklarasi diterapkan, sinyalkan replikasi bila ada policy baru.

Kegagalan provisioning menggagalkan startup dengan pesan yang menyebut bucket dan penyebabnya, sama seperti
pemeriksaan skema.

### D. Penguncian di action manajemen (`ObjectStorageServices`)

Semua penolakan berupa `ActionException` 409 dengan pesan bahasa Inggris yang menjelaskan bahwa bucket dikelola
aplikasi `<owner>`.

| Action | Aturan baru |
| --- | --- |
| `PostGetMeta_StoreBucketSave` | Bucket managed: `PolicyId` tidak boleh berubah (state dan note boleh). Bucket baru atau bucket biasa tidak boleh memilih policy managed. |
| `PostMeta_StoreBucketDelete` | Bucket managed ditolak; minta Release dulu (Release sendiri hanya untuk yang tidak dideklarasikan). |
| `PostGetMeta_StorePolicySave` | Policy managed: `Access` tidak boleh berubah; field lain boleh. |
| `PostMeta_StorePolicyDelete` | Sudah ditolak karena policy dipakai bucket; tidak berubah. |
| `PostGetMeta_StoreObjectUpload`, `PostMeta_StoreObjectDelete`, `PostGetMeta_StoreObjectRestore`, `PostMeta_StoreObjectVersionPurge` | Ditolak untuk bucket managed. Upload ditolak **sebelum** body dibaca (aturan streamed action). |
| `GetMeta_StoreObjects`, `GetMeta_StoreObject`, `GetMeta_StoreObjectVersions`, `GetMeta_StoreObjectDownload` | Tetap diizinkan. |
| `GetMeta_StoreBuckets`, `GetMeta_StorePolicies` | Mengisi `Owner`, `IsDeclared`, `ManagedBucket`. |
| `PostMeta_StoreBucketRelease` (baru) | Lihat keputusan turunan. Tambahkan ke `IObjectStorageServices` dan proxy client `ObjectStorageService` (WPF). |

`IObjectStore` (jalur modul), retensi, GC, replikasi, repair, dan endpoint publik `/store` **tidak** diubah.

### E. Object Storage Manager (WPF, `Em.Ui.Wpf.Core`)

Ikuti `MaterialDesign.xaml`, aturan UserManager, aturan help hover, dan aturan anti-kilatan putih di `CLAUDE.md`.
Command guard memakai gaya file yang sudah ada.

1. **Tab Buckets dan bucket editor:** chip `MANAGED` dengan tooltip "Managed by &lt;owner&gt;" beserta deskripsinya, dan
   chip tambahan `NOT DECLARED` bila `IsDeclared == false`. Bucket editor menampilkan deskripsi sebagai teks read-only
   di atas field Note (Note tetap bisa diedit admin). Kombo policy di editor bucket dinonaktifkan untuk bucket managed
   (`ToolTipService.ShowOnDisabled`, help menjelaskan alasannya). Delete bucket dinonaktifkan untuk bucket managed.
   Aksi **Release** di menu More hanya aktif untuk bucket managed yang tidak dideklarasikan, dengan konfirmasi.
2. **Object browser dan side sheet versi** (manager maupun `StoreBucketEditor`): Upload (toolbar dan drag-drop),
   Delete object, Restore, dan Purge version dinonaktifkan untuk bucket managed. Download dan Versions tetap aktif.
   Tampilkan baris info singkat "Contents are managed by &lt;owner&gt;; read-only here."
3. **Tab Policies:** kolom atau chip yang menunjukkan bucket pemilik policy managed. Di editor policy, field Access
   dinonaktifkan untuk policy managed (dengan help hover). Field lain tetap bisa diedit.
4. **Pilihan policy untuk bucket baru atau biasa** tidak menampilkan policy managed.

### F. Pengujian

Tulis seluruh kode A–E dulu, lalu jalankan semua pengujian sekali di akhir (aturan urutan eksekusi).

- **Unit** (`tests/Em.Api.Core.Tests/ObjectStorage/`): validasi `AddManagedBucket` (nama invalid, owner kosong,
  deskripsi kosong/terlalu pendek/terlalu panjang, deklarasi ganda, nilai default invalid); `ObjectStorageOptions` default.
- **Integrasi:** termasuk deskripsi yang berubah di deklarasi ikut tersinkron saat startup, sedangkan Note admin tidak tersentuh.
- **Integrasi SQL Server** (`tests/Em.Api.Core.IntegrationTests/ObjectStoreTests.cs` + fixture): pembuatan pertama
  (volume default, policy, bucket); startup kedua idempoten dan tidak menimpa perubahan admin; akses dipaksa;
  adopsi bucket biasa (policy tunggal dan policy bersama → disalin); owner berubah; semua penolakan 409 di tabel D
  (termasuk upload yang ditolak tanpa membaca body); Release hanya untuk yang tidak dideklarasikan; jalur `IObjectStore`
  tetap bisa menulis ke bucket managed; migrasi skema v1 → v2 lewat skrip update.
- **View model** (`tests/Em.Ui.Wpf.Core.Tests/ObjectStorageManagerTests.cs`): guard command untuk bucket managed,
  declared, dan undeclared; policy managed tersembunyi dari pilihan; field Access terkunci.
- **Build:** `dotnet build src/backend/Em.Api.slnx`, `dotnet build src/frontend/Em.Ui.Wpf.slnx`,
  `dotnet build src/frontend/Em.Ui.Maui.slnx` (pastikan perubahan kontrak tidak memecah MAUI).
  Lalu `dotnet test` untuk backend dan WPF.
- **Smoke HTTP** terhadap `Em.Api` lokal dengan modul uji menyala: harness
  `..\.artefacts\em-system\scripts\managed-bucket-smoke\` (startup membuat `emtest-files`; action manajemen ditolak;
  download diizinkan). Kredensial dari konfigurasi lokal artefak.
- **Render:** harness `..\.artefacts\em-system\scripts\managed-bucket-render\`, tema terang dan gelap: tab Buckets
  dengan chip, bucket editor managed (tombol nonaktif, info read-only), side sheet versi, dan editor policy managed
  (Access nonaktif, tooltip saat disabled). Termasuk kondisi busy/disabled sesuai aturan anti-kilatan putih. Disetujui
  untuk dijalankan (keputusan 9). Output di folder `out` harness, tidak ada berkas di repo.
- **Database:** test integrasi membuat database sementaranya sendiri. Skrip update juga diterapkan ke database lokal
  pengembangan `Em.Api` (target lokal di `..\.artefacts\em-system\config\`). Target remote **tidak** dimigrasikan di
  plan ini; catat di laporan sebagai tindakan lanjutan.

### G. Dokumentasi

- `doc/engine/engine-object-storage.md` dan `.id.md`: bagian baru **Managed buckets** (konsep, `AddManagedBucket`,
  `ObjectStorageOptions.DefaultVolumePath`, apa yang dikunci dan siapa yang boleh apa, adopsi, Release, *Upgrade notes*
  untuk skema v2 dan DTO), serta perbarui *Using it from a module* ("buckets must already exist" → kecuali managed)
  dan *Limits of version 1*.
- `CLAUDE.md` em-system: catatan pembaruan 2026-10-xx (ringkas, dengan status uji sebenarnya).
- `doc/ideas/object-storage-lanjutan.md`: tidak diubah kecuali muncul usulan baru selama eksekusi (dicatat di sana).

## Urutan eksekusi

1. Baca ulang `CLAUDE.md` em-system, `doc/convention/dahlia-convention.md`, dan panduan object storage.
2. Tulis kode A → E secara berurutan, tanpa build atau test di antaranya.
3. Bangun dan jalankan seluruh pengujian F. Perbaiki temuan, lalu ulangi test yang terdampak.
4. Tulis dokumentasi G.
5. Review akhir: periksa semua action bernama `Get...`/`Post...` punya atribut, pastikan tidak ada nama produk atau
   path mesin di berkas repo, dan pastikan XML comment bahasa Inggris.
6. Laporan eksekusi di bagian bawah plan ini, lalu pindahkan ke `plan/executed/`.
7. Commit sesuai keputusan 10 (pesan commit bahasa Indonesia).

## Penanganan kegagalan yang sudah diputuskan

- SQL Server lokal tidak terjangkau: test integrasi akan ter-skip. Catat sebagai **belum dijalankan**, jangan
  dinyatakan lulus. Smoke HTTP dicoba dengan database yang tersedia; kalau tidak ada, catat sebagai tertunda.
- Tindakan diblokir policy: ikuti aturan skrip PowerShell manual di `CLAUDE.md` (`plan/object-storage-managed-bucket-manual/`).
- Render gagal karena lingkungan (bukan bug): catat sebagai tertunda beserta perintah untuk menjalankannya ulang.

## Laporan eksekusi

Dieksekusi 2026-10-15 oleh Claude Code di branch `work-bench`. Semua seksi A–G selesai; tidak ada tindakan yang
diblokir policy.

### Penyimpangan dari plan (diputuskan saat eksekusi, konsisten dengan plan)

1. **Kolom baru diganti `json_object` (Dahlia Convention).** Plan meminta cek ulang
   [dahlia-convention.md](../../doc/convention/dahlia-convention.md) bagian *Additional columns* sebelum menamai
   kolom. Bagian itu menyatakan kolom yang dibutuhkan setelah tabel dipakai **tidak** ditambahkan sebagai kolom
   baru, tetapi disimpan di `json_object`. Karena itu owner dan deskripsi disimpan di
   `ta_StoreBucket.json_object` (`{"owner": ..., "description": ...}`, key lain dipertahankan; helper
   `ManagedBucketJson`). Konsekuensi:
   - Tidak ada kolom `cStoreBucketOwner`/`cStoreBucketDescription`, tidak ada versi skema 2, tidak ada skrip
     `updates/20261015-StoreManagedBucket.sql`; `ObjectStorageBackground` tetap menerima versi `1`.
   - `060-objectstore.sql` hanya mendapat komentar header tentang key JSON tersebut.
   - `vi_StoreBucket.sql` mengekspand JSON sebagai `cvStoreBucketOwner` dan `cvStoreBucketDescription`
     (prefix `cv` sesuai konvensi, dijaga `ISJSON`).
   - Test integrasi "migrasi skema v1 → v2 lewat skrip update" tidak berlaku; diganti test bahwa view
     mengekspand kedua key.
   - "Bucket managed" = `json_object` punya `owner`; policy managed = dipakai bucket managed (sama seperti plan).
2. **Nama policy managed tetap boleh diubah.** Keputusan 7 menyebut "Nama ... dikunci" (dibaca sebagai nama
   bucket), tabel D menyebut "Policy managed: Access tidak boleh berubah; field lain boleh". Diikuti tabel D yang
   lebih spesifik: nama policy boleh diubah karena provisioner tidak bergantung pada nama policy.
3. **Entri audit tambahan `bucket.provisioned`** untuk bucket yang dibuat saat startup, selain `bucket.adopted`,
   `bucket.access.enforced`, `bucket.released`. Audit startup ditulis lewat `IAuditWriter.WriteNowAsync` dengan
   actor `system`; kegagalan menulis audit hanya dicatat sebagai warning, tidak menghentikan startup.
4. **`ManagedBucketOptions.Note`** dipakai sebagai catatan awal policy managed (bawaan: "Created for managed
   bucket '<name>' of <owner>."); note bucket tetap milik admin.
5. **Akses registry deklarasi.** `ManagedBucketRegistry` tetap internal; disimpan di `EmAppBuilder.ManagedBuckets`
   dan `ObjectStorageRuntime.ManagedBuckets` (bukan disuntik ke constructor service publik, karena tipe internal
   tidak boleh muncul di constructor publik). Opsi juga ada di `ObjectStorageRuntime.Options` selain singleton
   `ObjectStorageOptions`.
6. **Release hanya dari Object Storage Manager** (tab Buckets › More). Bucket editor menampilkan chip NOT DECLARED
   dengan tooltip yang mengarahkan ke sana.
7. **Body action Release** dipisah ke `ReleaseBucketCoreAsync` (internal) agar bisa diuji tanpa `EmApp`; action
   publik memanggilnya lalu menulis audit.

### Hasil pengujian

| Uji | Hasil |
| --- | --- |
| `dotnet build src/backend/Em.Api.slnx` | Lulus, tanpa warning baru |
| `dotnet build src/frontend/Em.Ui.Wpf.slnx` | Lulus; hanya warning lama CS8604 di `Em.Test.Wpf/TestService.cs` |
| `dotnet build src/frontend/Em.Ui.Maui.slnx` | Lulus |
| `dotnet test src/backend/Em.Api.slnx` | 292 total: 284 lulus, 8 skip (test log store PostgreSQL/MySQL, server tidak tersedia; bukan bagian fitur ini) |
| Unit baru `tests/Em.Api.Core.Tests/ObjectStorage/ManagedBucketTests.cs` | 14 kasus lulus (validasi deklarasi, deklarasi ganda, opsi default, urutan deklarasi vs `AddObjectStorage`, draft policy, JSON) |
| Integrasi baru `tests/Em.Api.Core.IntegrationTests/ManagedBucketTests.cs` (SQL Server lokal, tidak di-skip) | 9 lulus: pembuatan pertama + start kedua idempoten dan mempertahankan perubahan admin; akses dipaksa + owner/deskripsi tersinkron, note admin tetap; adopsi policy tunggal dan policy bersama (disalin, urutan volume sama); volume default dibuat ulang dengan nama bebas; folder volume tidak valid → pesan menyebut `DefaultVolumePath`; semua 409 di tabel D termasuk upload tanpa membaca body (stream yang gagal bila dibaca); baca/unduh dan jalur `IObjectStore` tetap jalan; Release hanya untuk yang tidak dideklarasikan; view `cv*` |
| `dotnet test src/frontend/Em.Ui.Wpf.slnx` | 140 lulus (4 test view model baru: managed/declared, undeclared + Release dengan konfirmasi, Access policy terkunci, bucket page managed) |
| Smoke HTTP `..\.artefacts\em-system\scripts\managed-bucket-smoke` | 31 cek lulus. Harness menjalankan `Em.Api` (Debug) sendiri dengan modul uji dan log server menyala, terhadap database SQL Server **lokal** (bukan database `emapi-config.json` yang remote), content root di folder `out` harness: startup membuat `emtest-files`; 10 penolakan 409; browse/info/versions/download lulus; start kedua mengembalikan akses yang diubah lewat SQL, mempertahankan retensi dan note admin, tidak membuat policy baru; warning log dan audit `bucket.provisioned`/`bucket.access.enforced` tertulis. Baris object storage yang dibuat dihapus otomatis (`cleanup.sql`); entri audit tetap di log lokal. |
| Render `..\.artefacts\em-system\scripts\managed-bucket-render` | 24 PNG terang/gelap di `out` harness dan 12 cek properti lulus, diperiksa visual: tabel bucket dengan chip, kondisi disabled dan busy (tanpa kilatan putih), lebar 820, side sheet bucket managed, browser dan side sheet versi read-only, tabel dan editor policy managed (Access nonaktif), tab bucket editor managed/biasa/sempit. Temuan render yang diperbaiki: kolom nama tabel Policies dilebarkan (170 → 270) karena nama terpotong chip. |

### Database

- Tidak ada perubahan tabel. `views/vi_StoreBucket.sql` diterapkan ulang ke dua database lokal pengembangan
  (Windows Authentication) dan terverifikasi; daftar target ada di catatan privat
  `..\.artefacts\em-system\config\db-migration-targets.md`.
- Atas permintaan pengguna setelah eksekusi, `vi_StoreBucket.sql` juga diterapkan ke kedua target remote dan
  terverifikasi (kolom `cv*` ada, view terbaca, `ObjectStoreSchemaVersion` tetap 1). Semua target di catatan
  privat kini sinkron.
- Temuan di luar cakupan: salah satu database lokal punya skema registry lama (kolom robot di `ta_CtnManifest`
  belum ada) sehingga `Em.Api` menolak start di sana; tidak diubah, smoke memakai database lokal yang lengkap.

### Belum diverifikasi

- Interaksi mouse di window sungguhan: membuka menu **More** dan memilih Release, tooltip yang muncul saat hover
  pada field nonaktif (dicek lewat properti `ToolTipService.ShowOnDisabled` dan teks binding, bukan popup yang
  dirender), drag-drop file ke bucket managed yang ditolak.
- Popup (ContextMenu, ToolTip) tidak ikut ter-render oleh `RenderTargetBitmap`.

### Berkas

- Kode: `Api/ObjectStorage/ManagedBuckets.cs`, `ManagedBucketProvisioner.cs`, `ObjectStorageOptions.cs` (baru);
  `ObjectStorageServices.Settings.cs`, `ObjectStorageServices.Objects.cs`, `ObjectStorageBackground.cs`,
  `ObjectStorageRuntime.cs`, `EmAppBuilder.ObjectStorage.cs`, `SystemAudits.cs`; kontrak di
  `Em.Libs/Api.Core.Models/IObjectStorageServices.cs` dan `ObjectStorageEntities.cs`; WPF di
  `ObjectStorageManager*.cs/.xaml`, `StoreBucketEditor.xaml(.cs)`, `Api.Core/ObjectStorageService.cs`; modul uji
  `Em.Test.Api/TestModuleExtensions.cs`, `Em.Test.Models/ITestServices.cs`.
- Panduan: bagian "Managed buckets" di [doc/engine/engine-object-storage.md](../../doc/engine/engine-object-storage.md)
  dan [engine-object-storage.id.md](../../doc/engine/engine-object-storage.id.md) (termasuk *Upgrade notes*).
- Release note NuGet tidak ditulis (sesuai plan).
