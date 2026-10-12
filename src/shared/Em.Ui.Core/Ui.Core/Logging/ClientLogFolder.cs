using System.Text;
using System.Text.Json;
using Em.Api.Core.Models;

namespace Em.Ui.Core.Logging;

/// <summary>
/// The local outbox: JSONL segments written always (also before sign-in and offline). A segment is "open" while
/// events are appended to it and "closed" once it is full or the shipper takes it; only closed segments are
/// sent. Body and form contents are never written by the engine; what goes in is what <c>ILogger</c> was given.
/// </summary>
public sealed class ClientLogFolder
{
   private const string DroppedFile = "dropped.count";
   private static readonly JsonSerializerOptions Json = new();
   private readonly ClientLogOptions _options;
   private readonly object _lock = new();
   private string? _open;
   private long _openBytes;

   /// <summary>Creates the folder object; the directory is created on first write.</summary>
   public ClientLogFolder(ClientLogOptions options) => _options = options;

   /// <summary>Folder path.</summary>
   public string Path => _options.Folder;

   /// <summary>Appends one event to the open segment, closing it when it is full.</summary>
   public void Append(LogEvent e) {
      var line = JsonSerializer.Serialize(e, Json) + "\n";
      var bytes = Encoding.UTF8.GetByteCount(line);
      lock (_lock) {
         Directory.CreateDirectory(_options.Folder);
         if (_open is null || _openBytes + bytes > _options.SegmentBytes) Rotate();
         File.AppendAllText(_open!, line, Encoding.UTF8);
         _openBytes += bytes;
      }
   }

   /// <summary>Closes the open segment so it can be sent; returns the closed segments, oldest first.</summary>
   public IReadOnlyList<string> CloseAndList() {
      lock (_lock) {
         if (_open is not null) Close();
         if (!Directory.Exists(_options.Folder)) return [];
         return Directory.GetFiles(_options.Folder, "seg-*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList();
      }
   }

   /// <summary>Reads a closed segment; unreadable lines are skipped.</summary>
   public List<LogEvent> Read(string file) {
      var list = new List<LogEvent>();
      foreach (var line in File.ReadLines(file)) {
         if (string.IsNullOrWhiteSpace(line)) continue;
         try { if (JsonSerializer.Deserialize<LogEvent>(line, Json) is { } e) list.Add(e); }
         catch (JsonException) { }
      }
      return list;
   }

   /// <summary>Deletes a segment that was sent.</summary>
   public void Delete(string file) {
      lock (_lock) { try { File.Delete(file); } catch (IOException) { } }
   }

   /// <summary>Events dropped because the folder was full, not reported yet.</summary>
   public int Dropped {
      get {
         lock (_lock) {
            var file = System.IO.Path.Combine(_options.Folder, DroppedFile);
            return File.Exists(file) && int.TryParse(File.ReadAllText(file), out var n) ? n : 0;
         }
      }
   }

   /// <summary>Clears the dropped counter after it was reported.</summary>
   public void ResetDropped() {
      lock (_lock) {
         var file = System.IO.Path.Combine(_options.Folder, DroppedFile);
         try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
      }
   }

   private void Rotate() {
      if (_open is not null) Close();
      // The name orders by time and is unique; the shipper uses it as the batch id.
      _open = System.IO.Path.Combine(_options.Folder, $"open-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jsonl");
      _openBytes = 0;
   }

   private void Close() {
      var open = _open!;
      _open = null;
      _openBytes = 0;
      if (!File.Exists(open)) return;
      var closed = System.IO.Path.Combine(_options.Folder, "seg-" + System.IO.Path.GetFileName(open)["open-".Length..]);
      try { File.Move(open, closed); } catch (IOException) { return; }
      Trim();
   }

   // Deletes the oldest closed segments while the folder is over its limit and counts their events as dropped.
   private void Trim() {
      var files = Directory.GetFiles(_options.Folder, "seg-*.jsonl").Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
      var total = files.Sum(f => f.Length);
      var dropped = 0;
      foreach (var file in files) {
         if (total <= _options.MaxFolderBytes || files.Count <= 1) break;
         try {
            dropped += File.ReadLines(file.FullName).Count();
            total -= file.Length;
            file.Delete();
         }
         catch (IOException) { }
      }
      if (dropped > 0) {
         var counter = System.IO.Path.Combine(_options.Folder, DroppedFile);
         var previous = File.Exists(counter) && int.TryParse(File.ReadAllText(counter), out var n) ? n : 0;
         File.WriteAllText(counter, (previous + dropped).ToString());
      }
   }

   /// <summary>
   /// Turns segments left open by a previous run (the application closed or crashed) into closed segments so
   /// they are sent too. Called once at startup.
   /// </summary>
   public void RecoverOpenSegments() {
      lock (_lock) {
         if (!Directory.Exists(_options.Folder)) return;
         foreach (var open in Directory.GetFiles(_options.Folder, "open-*.jsonl")) {
            var closed = System.IO.Path.Combine(_options.Folder, "seg-" + System.IO.Path.GetFileName(open)["open-".Length..]);
            try { File.Move(open, closed); } catch (IOException) { }
         }
      }
   }
}
