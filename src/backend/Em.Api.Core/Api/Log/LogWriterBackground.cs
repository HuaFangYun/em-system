using System.Text;
using System.Text.Json;
using Em.Api.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Em.Api.Core.Log;

/// <summary>
/// Drains the telemetry queue every second (or every 500 events) into <see cref="ILogIngest"/>. When the store
/// cannot be written the batch goes to a JSONL segment under <c>&lt;logPath&gt;/outbox</c>; segments are retried
/// every 30 seconds and the oldest are deleted (and counted) when the folder exceeds its size limit.
/// </summary>
internal sealed class LogWriterBackground(LogQueue queue, ILogIngest ingest, LogRuntime runtime, LogSegmentFolder segments,
   ILogger<LogWriterBackground> logger) : BackgroundService
{
   private const int BatchSize = 500;
   private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
   private long _reportedDropped;

   protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      var nextRetry = DateTime.UtcNow;
      while (!stoppingToken.IsCancellationRequested) {
         try {
            await queue.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
            ReportDropped();
            while (queue.Drain(BatchSize) is { Count: > 0 } batch) {
               await WriteOrSpillAsync(batch, stoppingToken);
               if (batch.Count < BatchSize) break;
            }

            if (DateTime.UtcNow >= nextRetry) {
               nextRetry = DateTime.UtcNow + RetryInterval;
               await RetrySegmentsAsync(stoppingToken);
            }
         }
         catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            break;
         }
         catch (Exception ex) {
            using (LogGuard.Suppress()) logger.LogError(ex, "Log writer loop failed.");
         }
      }

      // Last drain on shutdown: whatever cannot reach the store is kept on disk.
      while (queue.Drain(BatchSize) is { Count: > 0 } rest) {
         await WriteOrSpillAsync(rest, CancellationToken.None);
      }
   }

   private void ReportDropped() {
      var dropped = runtime.Dropped;
      if (dropped <= _reportedDropped) return;
      var count = dropped - _reportedDropped;
      _reportedDropped = dropped;
      queue.Enqueue(new LogEvent {
         Time = DateTime.UtcNow, Kind = LogEventKind.Diagnostic, Level = LogEventLevel.Warning, Category = "Em.Log",
         Template = "{Count} log events were dropped because the log queue was full.",
         Message = $"{count} log events were dropped because the log queue was full.",
         Properties = JsonSerializer.Serialize(new { Count = count }), App = runtime.App, AppVersion = runtime.AppVersion,
         Instance = runtime.Instance, Environment = runtime.Environment
      });
   }

   private async Task WriteOrSpillAsync(List<LogEvent> batch, CancellationToken ct) {
      try {
         await ingest.WriteAsync(batch, LogSender.Process, null, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
         segments.Write(batch);
         runtime.PendingSegments = segments.Count;
         using (LogGuard.Suppress()) logger.LogWarning("Log store write failed; {Count} events kept on disk: {Reason}", batch.Count, ex.Message);
      }
   }

   private async Task RetrySegmentsAsync(CancellationToken ct) {
      foreach (var file in segments.Oldest()) {
         var events = segments.Read(file);
         try {
            if (events.Count > 0) await ingest.WriteAsync(events, LogSender.Process, null, ct);
            segments.Delete(file);
         }
         catch (Exception ex) when (ex is not OperationCanceledException) {
            break; // store still unavailable; keep the segment and try again later
         }
      }
      runtime.PendingSegments = segments.Count;
   }
}

/// <summary>The folder of JSONL segments that hold events while the store cannot be written.</summary>
internal sealed class LogSegmentFolder
{
   private static readonly JsonSerializerOptions Json = new();
   private readonly string _folder;
   private readonly long _maxBytes;
   private readonly LogRuntime _runtime;
   private readonly object _lock = new();

   public LogSegmentFolder(string folder, long maxBytes, LogRuntime runtime) {
      _folder = folder;
      _maxBytes = maxBytes;
      _runtime = runtime;
   }

   public string Folder => _folder;

   public int Count {
      get { lock (_lock) return Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.jsonl").Length : 0; }
   }

   public void Write(IReadOnlyList<LogEvent> events) {
      lock (_lock) {
         Directory.CreateDirectory(_folder);
         var name = Path.Combine(_folder, $"seg-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jsonl");
         var sb = new StringBuilder();
         foreach (var e in events) sb.AppendLine(JsonSerializer.Serialize(e, Json));
         File.WriteAllText(name, sb.ToString(), Encoding.UTF8);
         Trim();
      }
   }

   public IEnumerable<string> Oldest() {
      lock (_lock) {
         return Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList() : [];
      }
   }

   public List<LogEvent> Read(string file) {
      var list = new List<LogEvent>();
      foreach (var line in File.ReadLines(file)) {
         if (string.IsNullOrWhiteSpace(line)) continue;
         try { if (JsonSerializer.Deserialize<LogEvent>(line, Json) is { } e) list.Add(e); }
         catch (JsonException) { }
      }
      return list;
   }

   public void Delete(string file) {
      lock (_lock) { try { File.Delete(file); } catch (IOException) { } }
   }

   // Deletes the oldest segments while the folder is over its limit; their events are counted as dropped.
   private void Trim() {
      var files = Directory.GetFiles(_folder, "*.jsonl").Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
      var total = files.Sum(f => f.Length);
      foreach (var file in files) {
         if (total <= _maxBytes || files.Count <= 1) break;
         var lines = File.ReadLines(file.FullName).Count();
         total -= file.Length;
         try { file.Delete(); _runtime.AddDropped(lines); } catch (IOException) { }
      }
   }
}
