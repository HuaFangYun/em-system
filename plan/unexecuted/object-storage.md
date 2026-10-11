# Plan — Object storage engine (tahap internal Em)

Status: **belum dieksekusi — plan matang, menunggu permintaan eksekusi**
Dibuat: 2026-10-11 — dipromosikan dari [doc/ideas/object-storage.md](../../doc/ideas/object-storage.md)
setelah pengguna menjawab pertanyaan pemblokir (lihat §1).

---

## 0. Ringkasan

Membangun object storage generik di engine em-system: bucket → object → versi → blob (hash isi) →
salinan fisik di volume, dengan policy per bucket yang mengatur placement/replika, retensi versi, kuota
dan hak akses. Tahap ini hanya **API/service internal Em** dan layar WPF. Protokol S3 **tidak**
diimplementasikan; desainnya hanya dijaga agar adapter S3 bisa ditambahkan kelak (§10).

Integrasi mengikuti aturan "library bersama wajib di em-system": backend di `Em.Api.Core`, kontrak di
`Em.Libs`, layar di `Em.Ui.Wpf.Core`, SQL di `doc/sqlscript/`, dinyalakan host lewat
`builder.AddObjectStorage()`. Pola acuan terdekat: NuPak (`Api/NuPak/*`, `040-nupak.sql`).

## 1. Keputusan pengguna (2026-10-11)

| # | Pertanyaan di catatan ide | Keputusan |
| --- | --- | --- |
| 1 | Cakupan policy | **Keempatnya**: placement + replika, retensi versi, kuota bucket, hak akses |
| 2 | Jenis volume | **Folder lokal/NAS** di filesystem server API. Provider remote menyusul lewat adapter |
| 5 | Deduplikasi | **Global lintas bucket** (sesuai ERD: blob tanpa bucket) |
| 7 | Deployment | **Satu instance API per storage** (sama dengan registry GC) |
| – | Konsumen pertama | **Konsumen baru**. CDN, registry, NuPak dan approval tidak dimigrasikan. Adapter `IBinaryStorage` opsional (Fase 7) |

Pertanyaan 3 (operasi S3) sudah dijawab 2026-10-09: ditunda. Pertanyaan 4 dan 6 dijawab dengan usulan
bawaan di §2. Usulan itu tidak memblokir; pengguna bisa mengubahnya sebelum eksekusi.

## 2. Usulan bawaan untuk keputusan yang tersisa

Dipakai bila pengguna tidak mengubahnya sebelum eksekusi.

- **Hash blob:** SHA-256 (32 byte) sebagai identitas blob. CRC32 hanya checksum tambahan untuk verifikasi
  cepat. Hash internal tidak dipakai sebagai ETag eksternal.
- **Key object:** string UTF-8, case-sensitive, 1–1024 byte, tanpa karakter kontrol, tanpa segmen `.`/`..`.
  Separator `/` hanya konvensi untuk listing berprefiks; tidak ada folder sungguhan. Key **tidak pernah**
  dipakai sebagai path fisik.
- **Nama bucket:** aturan penamaan S3 (3–63 karakter, huruf kecil, angka, `-`, `.`, diawali dan diakhiri
  huruf/angka) agar kelak bisa langsung dipetakan ke S3.
- **Versioning:** selalu aktif (tidak ada mode unversioned di v1). Upload ke key yang ada membuat versi baru
  dan menjadikannya aktif.
- **Delete biasa:** membuat *delete marker* (versi tanpa blob) yang menjadi versi aktif; object tampak hilang
  tetapi riwayat tetap ada. **Delete permanen** satu versi adalah operasi terpisah. Bila versi aktif yang
  dihapus permanen, versi non-marker terbaru berikutnya menjadi aktif.
- **Restore:** membuat versi baru yang menunjuk blob versi lama (byte tidak digandakan). Pointer aktif tidak
  diputar mundur diam-diam.
- **Nomor versi:** integer lokal per object, dialokasikan dari counter monotonik `cStoreObjectVersionNext`,
  terpisah dari pointer aktif. Ini menutup masalah bentrok nomor di catatan ide (klarifikasi #3).
- **Replika:** policy punya `ReplicaMin` (bawaan 1) dan `ReplicaTarget` (bawaan 1). Upload sukses setelah
  `ReplicaMin` salinan siap. Salinan sampai `ReplicaTarget` dibuat oleh task latar belakang.
- **Kuota:** dihitung dari **byte logis** semua versi yang masih disimpan (termasuk non-aktif), bukan byte
  fisik setelah dedup. Delete marker 0 byte, tetapi tetap dihitung sebagai object.
- **Hak akses:** policy `Private` atau `PublicRead`. Manajemen selalu lewat claim engine. Hak per bucket per
  user/role ditunda (§11).
- **Masa tenggang GC:** 24 jam (1–720), sama dengan registry GC.

## 3. Skema database — `doc/sqlscript/mssql/tables/060-objectstore.sql`

Satu skrip idempoten (`IF OBJECT_ID(...) IS NULL`), satu transaksi, mengikuti Dahlia Convention: PK
`char(26)` ULID, `ustamp`/`datestamp`/`json_object`, `State` berupa `int` yang nilainya didefinisikan
aplikasi. Perubahan terhadap ERD asli ditandai ★.

### 3.1 Tabel

**`ta_StoreVolume`**: lokasi fisik.
`cStoreVolumeId` PK, `cStoreVolumeName varchar(100)` unik, `cStoreVolumeState int` (1 Active, 2 ReadOnly,
3 Offline), `cStoreVolumeType int` (1 LocalFolder), ★`cStoreVolumePath nvarchar(400)` (relatif ke
`ContentRootPath` atau absolut, tanpa rahasia), `cStoreVolumeNote`, stamps, `json_object` (cadangan untuk
konfigurasi provider remote kelak).

**`ta_StorePolicy`**: aturan bucket. Aturan yang ditegakkan disimpan sebagai **kolom eksplisit**, bukan
JSON, karena dibaca dan divalidasi di setiap upload.
`cStorePolicyId` PK, `cStorePolicyName` unik, `cStorePolicyState`, `cStorePolicyNote`,
★`cStorePolicyReplicaMin int`, ★`cStorePolicyReplicaTarget int` (CHECK `1 <= Min <= Target`),
★`cStorePolicyKeepVersions int NULL` (jumlah versi non-aktif yang disimpan; NULL = tak terbatas),
★`cStorePolicyKeepDays int NULL` (umur maksimum versi non-aktif),
★`cStorePolicyQuotaBytes bigint NULL`, ★`cStorePolicyQuotaObjects bigint NULL`,
★`cStorePolicyMaxObjectBytes bigint NULL`, ★`cStorePolicyAccess int` (1 Private, 2 PublicRead),
stamps, `json_object`.

**★`ta_StorePolicyVolume`**: volume yang boleh dipakai policy beserta urutan prioritasnya. Ini menjawab
"policy → volume lewat JSON atau FK" di catatan ide: memakai **tabel relasi dengan FK**.
PK (`cStorePolicyId`, `cStoreVolumeId`), `cStorePolicyVolumeOrder int`, FK ke kedua tabel.

**`ta_StoreBucket`**
`cStoreBucketId` PK, `cStoreBucketName varchar(63)` unik (collation case-sensitive), `cStorePolicyId` FK,
`cStoreBucketState`, `cStoreBucketNote`, ★`cStoreBucketBytes bigint`, ★`cStoreBucketObjects bigint`
(counter kuota, diperbarui dalam transaksi yang sama dengan versi), stamps, `json_object`.

**`ta_StoreObject`**
`cStoreObjectId` PK (★PK hanya ID, bukan gabungan dengan versi aktif), `cStoreBucketId` FK,
`cStoreObjectKey nvarchar(1024)` (case-sensitive), ★`cStoreObjectKeyHash binary(32)` (SHA-256 key UTF-8),
`cStoreObjectVersionCurrent int NULL` (NULL hanya sesaat sebelum versi pertama tercatat dalam transaksi
yang sama), ★`cStoreObjectVersionNext int`, `cStoreObjectState`, stamps, `json_object`.
UNIQUE (`cStoreBucketId`, `cStoreObjectKeyHash`): key sampai 2048 byte melebihi batas index SQL Server
1700 byte, jadi keunikan ditegakkan lewat hash. Index (`cStoreBucketId`, `cStoreObjectKey`) memakai
INCLUDE atau kolom terhitung prefiks untuk listing berprefiks. Bentuk akhirnya diputuskan saat eksekusi
setelah mengukur query listing.
Tanpa FK balik ke Version, sesuai arah diskusi. Validitas pointer dijaga service dalam transaksi.

**`ta_StoreObjectVersion`**
PK (`cStoreObjectId`, `cStoreObjectVersionId int`), FK → Object,
`cStoreObjectBlobHash binary(32) NULL` FK → Blob (NULL = delete marker), ★`cStoreObjectVersionSize bigint`,
★`cStoreObjectVersionMime varchar(255)`, ★`cStoreObjectVersionMarker bit`, `datestamp`, `json_object`
(metadata user per versi). Metadata per versi mengikuti telaah awal di catatan ide.

**`ta_StoreObjectBlob`**
`cStoreObjectBlobHash binary(32)` PK, `cStoreObjectBlobCrc32 binary(4)`, `cStoreObjectBlobSize bigint`,
★`cStoreObjectBlobState int` (1 Ready, 2 Orphan), ★`cStoreObjectBlobOrphanAt datetime NULL`, `datestamp`.

**`ta_StoreObjectBlobVol`**
PK (`cStoreObjectBlobHash`, `cStoreVolumeId`), FK ke keduanya, `cStoreObjectBlobVolState int`
(1 Writing, 2 Ready, 3 Missing, 4 Deleting), ★`cStoreObjectBlobVolVerifiedAt datetime NULL`, `datestamp`.

### 3.2 View

`views/vi_StoreBucket.sql` (bucket + policy + counter), `views/vi_StoreObject.sql` (object + versi aktif +
size/MIME), `views/vi_StoreVolume.sql` (volume + jumlah blob/byte). Masing-masing satu berkas, dijalankan
setelah semua `tables/`.

## 4. Tata letak fisik volume

```text
<volume>/blobs/<hex[0..2]>/<hex[2..4]>/<hex-sha256>     isi blob, read-only setelah ditulis
<volume>/.staging/<ulid>                                 upload yang sedang berjalan
```

Nama file hanya dari hash, sehingga key user tidak pernah menyentuh filesystem. `.staging` dibersihkan saat
startup karena hanya ada satu instance.

## 5. Alur inti

**Upload** (`PutAsync(bucket, key, stream, mime, metadata)`):
1. Validasi bucket, key, `MaxObjectBytes`. Lakukan cek kuota awal terhadap counter bucket.
2. Stream ke `.staging` pada volume prioritas pertama yang Active, sambil menghitung SHA-256, CRC32 dan
   ukuran. Batasi ukuran selama streaming, bukan setelahnya.
3. Ambil kunci per hash di dalam proses (pola `CtnBlobGate`). Bila blob sudah Ready dengan salinan Ready
   ≥ `ReplicaMin` pada volume policy: buang staging (dedup). Bila belum: rename atomik ke `blobs/...` di
   volume yang sama, salin ke volume berikutnya sampai `ReplicaMin`, verifikasi ukuran dan CRC32, lalu catat
   Blob dan BlobVol (Writing → Ready).
4. Transaksi SQL serializable: kunci baris object (`UPDLOCK`) atau buat object baru; alokasikan
   `VersionNext`; insert versi; set `VersionCurrent`; perbarui counter bucket; tegakkan kuota secara final
   (rollback → 409 bila terlampaui). Bila blob berstatus Orphan, kembalikan ke Ready.
5. Antrikan replikasi sampai `ReplicaTarget`.

Bila proses mati di tengah jalan: staging dibuang saat startup. Blob tanpa versi akan menjadi Orphan dan
diambil GC setelah masa tenggang. Tidak ada versi yang menunjuk blob yang belum Ready.

**Read:** object → versi aktif (atau versi tertentu) → BlobVol Ready pada volume Active/ReadOnly, diurutkan
menurut prioritas policy → buka stream. Dukung Range. Bila file hilang atau ukurannya tidak cocok: tandai
Missing, coba volume berikutnya, antrikan repair.

**Delete / delete permanen / restore:** mengikuti §2. Semuanya transaksional dan memperbarui counter.

**Retensi** (task latar belakang berkala + dapat dipicu manual): per bucket, hapus versi non-aktif di luar
`KeepVersions`/`KeepDays`. Delete marker yang menjadi satu-satunya versi tersisa menghapus object.

**GC blob** (pola registry GC: review dry run lalu run): blob tanpa versi → Orphan + `OrphanAt`. Setelah
masa tenggang dan di bawah kunci hash: hapus file di setiap volume (BlobVol Deleting → hapus baris), lalu
hapus Blob.

**Replikasi/repair** (task latar belakang): lengkapi salinan sampai `ReplicaTarget` pada volume policy,
ganti salinan Missing. Volume Offline dilewati. Perubahan daftar volume policy tidak memindahkan data
secara otomatis; data baru mengikuti policy, data lama dirapikan lewat repair manual.

## 6. Lapisan kode

| Lapisan | Lokasi | Isi |
| --- | --- | --- |
| Kontrak | `Em.Libs/Api.Core.Models/IObjectStorageServices.cs`, `ObjectStorageEntities.cs` | Action manajemen + DTO; konstanta claim |
| Core service | `Em.Api.Core/Api/ObjectStorage/ObjectStore.cs` (`IObjectStore`) | Upload, read, delete, restore, list. **Satu-satunya** jalur logika versi/policy/kuota. Dipakai modul server-side, action manager, endpoint publik, dan adapter S3 kelak |
| Volume | `.../ObjectStorage/Volumes/IStoreVolumeProvider.cs`, `LocalFolderVolumeProvider.cs` | Abstraksi provider; v1 hanya folder lokal/NAS |
| Data | `ObjectStorageDbContext.cs` | EF model untuk tabel §3 (pola `NuPakDbContext`) |
| Background | `ObjectStorageMaintenance.cs` | Replikasi/repair, retensi, GC, pembersihan staging saat startup |
| Action | `ObjectStorageServices*.cs` (partial: Buckets, Objects, Volumes, Policies, Maintenance) | Implementasi `IObjectStorageServices` |
| Endpoint publik | `ObjectStorageEndpoint.cs` | `GET/HEAD /store/<bucket>/<key>` hanya untuk bucket `PublicRead`. Di luar dispatcher seperti `/cdn`, dengan Range/ETag/`If-None-Match`. Selain itu 404 polos |
| Registrasi | `Api/Shared/EmAppBuilder.ObjectStorage.cs` | `builder.AddObjectStorage()` |
| Proxy klien | `Em.Ui.Wpf.Core/Api.Core/ObjectStorageService.cs` | Proxy action |
| Layar | `Em.Ui.Wpf.Core/Navigations/ObjectStorageManager*.cs/.xaml` | Lihat §8 |

Volume dan policy tersimpan di database, sehingga tidak memakai `ManagedStorageSettings` dan perubahannya
berlaku tanpa restart (cache di proses diperbarui setelah save; aman karena satu instance).

## 7. Action dan claim

Claim di modul `Administrative Tools`:
- `Object Storage Manager Access`: lihat bucket/object/versi, upload, download, delete, restore.
- `Object Storage Settings Manage`: volume, policy, buat/hapus bucket, delete permanen, retensi, GC, repair.

Daftar action awal (nama final mengikuti `action-naming-cleanup` bila plan itu sudah dieksekusi):

| Area | Action |
| --- | --- |
| Status | `GetMeta_StoreStatus` (counter, volume bermasalah, task berjalan) |
| Volume | `GetMeta_StoreVolumes`, `PostGetMeta_StoreVolumeValidate`, `PostGetMeta_StoreVolumeSave`, `PostMeta_StoreVolumeDelete` (hanya bila kosong) |
| Policy | `GetMeta_StorePolicies`, `PostGetMeta_StorePolicySave`, `PostMeta_StorePolicyDelete` (hanya bila tidak dipakai) |
| Bucket | `GetMeta_StoreBuckets`, `PostGetMeta_StoreBucketSave`, `PostMeta_StoreBucketDelete` (hanya bila kosong) |
| Object | `GetMeta_StoreObjects(bucketId, prefix, delimiter, continuation, limit)`, `GetMeta_StoreObject`, `PostGetMeta_StoreObjectUpload(request, content)` (streaming, pola `PostGetMeta_CdnUpload`), unduh streaming (pola streaming yang ada; dicek saat eksekusi), `PostMeta_StoreObjectDelete` |
| Versi | `GetMeta_StoreObjectVersions`, `PostGetMeta_StoreObjectRestore`, `PostMeta_StoreObjectVersionPurge` |
| Pemeliharaan | `GetMeta_StoreGcReview`, `PostGetMeta_StoreGcRun`, `PostGetMeta_StoreRetentionRun`, `PostGetMeta_StoreRepairRun`, `GetMeta_StoreTasks` |

Listing memakai continuation token opak, bukan offset, agar kelak setara dengan `ListObjectsV2`.

## 8. Layar WPF — Object Storage Manager

Mengikuti gaya manager yang ada (NuPak/SMTP), memakai `EmTabStrip` untuk tab panel:
- **Buckets**: tabel bucket (policy, jumlah object, byte vs kuota). Double-click membuka browser object:
  breadcrumb prefiks, folder semu dari delimiter `/`, upload (tombol + drag-drop), download, delete, panel
  versi (restore, purge) di side sheet.
- **Volumes**: tabel volume + editor (validate path seperti storage settings), status salinan Missing.
- **Policies**: editor replika/retensi/kuota/akses + daftar volume berurutan.
- **Maintenance**: GC review/run, retensi, repair, daftar task.
- Card home screen: total byte logis vs fisik, volume bermasalah.

Navigasi `admin.objectstorage` otomatis dari core seperti NuPak. MAUI tidak mendapat layar.

## 9. Fase eksekusi

Sesuai aturan di `claude.md`: **tulis kode semua fase lebih dulu**, lalu build penuh, pengujian,
review dan analisa sekali di akhir (Fase 8).

1. **SQL**: `060-objectstore.sql` + tiga view; urutan instalasi di README/`doc/sqlscript`.
2. **Kontrak** di `Em.Libs`: interface, DTO, enum State/Type/Access, konstanta claim, XML comment bahasa
   Inggris.
3. **Core**: DbContext, `IObjectStore`, volume provider lokal, kunci hash, validasi key/bucket, alur §5.
4. **Background**: staging cleanup, replikasi/repair, retensi, GC.
5. **Action + endpoint publik + `AddObjectStorage()`**; nyalakan di host `Em.Api` (dan opsional di
   `Em.Test` untuk contoh pemakaian `IObjectStore` dari modul).
6. **WPF**: proxy, Object Storage Manager, card home.
7. **Opsional — adapter `ObjectStoreBinaryStorage : IBinaryStorage`**: memetakan key `IBinaryStorage` ke
   satu bucket. `PutAsync` menolak key yang sudah punya versi agar sifat no-overwrite tetap ada. Tidak
   mengganti registrasi approval yang ada.
8. **Verifikasi dan dokumentasi**:
   - Unit test (`tests/Em.Api.Core.Tests`): validasi key/nama bucket, alokasi versi, aturan retensi,
     hitung kuota, path blob dari hash.
   - Integration test (`tests/Em.Api.Core.IntegrationTests`, SQL Server lokal): upload/dedup, versi dan
     restore, delete marker, purge versi aktif, kuota 409, replika 2 volume dan repair setelah file dihapus,
     GC dengan masa tenggang, upload paralel ke key yang sama, crash simulasi (staging tertinggal).
   - Uji UI WPF dengan unit test view-model; render/GUI hanya dengan konfirmasi pengguna (harness di
     `..\.artefacts\em-system\scripts\objectstorage-*`).
   - `doc/engine/engine-object-storage.md` (bahasa Inggris), tautan di `doc/engine/README.md` dan README
     root, pembaruan `claude.md` (ringkasan + status uji), status catatan ide.
   - Laporan eksekusi di akhir plan ini, lalu pindahkan ke `plan/executed/`.

## 10. Kesiapan S3 (tanpa implementasi)

Yang dijaga di v1 agar adapter S3 kelak tidak memaksa perubahan skema inti:
- Semua logika lewat `IObjectStore`; adapter S3 hanya transport (SigV4, XML, error code).
- Nama bucket dan batas key sudah mengikuti aturan S3; key case-sensitive.
- Versi punya metadata dan MIME sendiri; ada delete marker; restore = versi baru.
- Version ID eksternal kelak = encoding stabil dari (object ID, nomor versi), bukan integer mentah.
- ETag kelak dihitung terpisah (MD5 untuk upload tunggal, aturan multipart). Kolomnya ditambahkan saat
  adapter dibangun. Hash SHA-256 tidak dipakai sebagai ETag.
- Listing memakai prefix/delimiter/continuation token.
- Multipart kelak memakai tabel upload sementara terpisah. Tidak ada scaffolding di v1.

## 11. Di luar cakupan v1

Protokol S3 dan presigned URL; provider volume remote; hak per bucket per user/role; volume Draining dan
rebalancing otomatis; multi-instance (lock terdistribusi); enkripsi at-rest; object lock/legal hold;
lifecycle selain retensi versi; migrasi CDN/registry/NuPak/approval ke object storage; layar MAUI.
Usulan lanjutan yang muncul saat eksekusi dicatat di `doc/ideas/`.

## 12. Risiko

- **Konsistensi file vs SQL**: tidak atomik bersama. Diatasi dengan urutan "file Ready dulu, lalu metadata"
  dan GC berbasis Orphan + masa tenggang.
- **Satu instance**: dua instance pada volume/database yang sama dapat merusak kunci hash dan GC. Ini
  didokumentasikan dan dicek saat startup lewat lease di `ta_Meta` (peringatan, bukan jaminan).
- **Listing key panjang**: index pada `nvarchar(1024)` terbatas; ukur query listing dengan data besar.
- **Counter kuota**: harus selalu diperbarui di transaksi yang sama. Integration test membandingkan counter
  dengan `SUM` riwayat versi.
