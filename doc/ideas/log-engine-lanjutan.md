# Log engine: lanjutan dan temuan

- **Tanggal:** 2026-10-12
- **Status:** diskusi
- **Plan turunan:** belum ada
- **Catatan terkait:** [penyimpanan-log.md](penyimpanan-log.md),
  [plan/executed/penyimpanan-log.md](../../plan/executed/penyimpanan-log.md),
  [doc/engine/engine-log.md](../engine/engine-log.md)

Catatan ini berisi gagasan dan temuan, bukan perintah kerja. Agent tidak boleh mengeksekusinya sebelum catatan
ini dijadikan plan di `plan/unexecuted/`.

## Temuan saat eksekusi

- **Peringatan EF lama di NuPak:** startup mencetak peringatan EF "FirstOrDefault tanpa OrderBy" yang berasal dari
  `NuPakStartup.cs` (`db.Feeds.FirstOrDefaultAsync`), bukan dari log engine. Kini peringatan itu ikut tersimpan
  sebagai telemetry Warning setiap start. Usulan: tambahkan `OrderBy` pada query tersebut.
- **`.gitignore` mengabaikan folder `Log/`:** pola Visual Studio `[Ll]og/` membuat folder sumber
  `Api/Log/` dan `tests/.../Log/` tidak ter-commit; sudah diberi pengecualian. Folder sumber baru bernama `Log`
  atau `Logs` di tempat lain perlu pengecualian yang sama.

## Gagasan lanjutan

- **Banyak instance API per store:** shipper dan job retensi berasumsi satu instance. Butuh lease di `ta_Meta`
  (seperti `ObjectStore.Lease`) sebelum API dijalankan multi-replika.
- **Log Manager di MAUI:** saat ini MAUI hanya mengirim log klien; layar Log Manager dan tab Activity hanya WPF.
- **Ekspor OTLP:** model `LogEvent` sudah mengikuti model log OpenTelemetry; ekspor ke kolektor OTLP bisa
  ditambahkan tanpa mengubah penyimpanan.
- **Tail log live:** Log Manager memuat ulang per halaman; tail live bisa memakai jalur
  [stream-dua-arah.md](stream-dua-arah.md).
- **Audit untuk modul bisnis:** baru `Em.Test` (`TestAudit`) yang memakai `AddAudit`; modul lain bisa menyusul
  dengan pola yang sama.
