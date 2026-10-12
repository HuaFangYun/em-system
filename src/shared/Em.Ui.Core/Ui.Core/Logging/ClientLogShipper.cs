using Em.Api.Core.Models;

namespace Em.Ui.Core.Logging;

/// <summary>
/// Sends the local outbox to the server through <c>PostMeta_LogIngest</c>: after sign-in, every
/// <see cref="ClientLogOptions.SendInterval"/> while signed in, and before sign-out. A segment is deleted only after
/// the server accepted it; on failure it stays and is tried again next time. The segment name is the batch id,
/// so a resend after a lost answer is ignored by the server.
/// </summary>
public sealed class ClientLogShipper : IDisposable
{
   private const int MaxEventsPerBatch = 1000;
   private readonly ClientLogFolder _folder;
   private readonly ClientLogOptions _options;
   private readonly Func<ILogServices?> _service;
   private readonly SemaphoreSlim _gate = new(1, 1);
   private CancellationTokenSource? _loop;

   /// <summary>Creates the shipper.</summary>
   /// <param name="folder">Local outbox.</param>
   /// <param name="options">Settings.</param>
   /// <param name="service">Returns the log service of the signed-in connection, or <c>null</c> when signed out.</param>
   public ClientLogShipper(ClientLogFolder folder, ClientLogOptions options, Func<ILogServices?> service) {
      _folder = folder;
      _options = options;
      _service = service;
   }

   /// <summary>Last send error, or <c>null</c> after a successful send.</summary>
   public string? LastError { get; private set; }

   /// <summary>Starts sending in the background (after sign-in): once now, then every interval.</summary>
   public void Start() {
      Stop();
      var cts = new CancellationTokenSource();
      _loop = cts;
      _ = Task.Run(async () => {
         while (!cts.IsCancellationRequested) {
            await SendAsync(cts.Token);
            try { await Task.Delay(_options.SendInterval, cts.Token); }
            catch (OperationCanceledException) { return; }
         }
      });
   }

   /// <summary>Stops the background loop (signed out).</summary>
   public void Stop() {
      var loop = Interlocked.Exchange(ref _loop, null);
      if (loop is null) return;
      loop.Cancel();
      loop.Dispose();
   }

   /// <summary>Sends every closed segment now; returns the number of segments sent. Never throws.</summary>
   public async Task<int> SendAsync(CancellationToken ct = default) {
      if (_service() is not { } service) return 0;
      if (!await _gate.WaitAsync(0, ct)) return 0;
      var sent = 0;
      try {
         var dropped = _folder.Dropped;
         foreach (var file in _folder.CloseAndList()) {
            ct.ThrowIfCancellationRequested();
            var events = _folder.Read(file);
            var name = Path.GetFileNameWithoutExtension(file);
            for (var i = 0; i < Math.Max(1, (events.Count + MaxEventsPerBatch - 1) / MaxEventsPerBatch); i++) {
               var chunk = events.Skip(i * MaxEventsPerBatch).Take(MaxEventsPerBatch).ToArray();
               var id = i == 0 ? name : $"{name}:{i}";
               await service.PostMeta_LogIngest(new LogIngestBatch {
                  BatchId = id.Length <= 64 ? id : id[^64..], Events = chunk, DroppedEvents = dropped
               });
               if (dropped > 0) {
                  _folder.ResetDropped();
                  dropped = 0;
               }
            }
            _folder.Delete(file);
            sent++;
         }
         LastError = null;
      }
      catch (OperationCanceledException) { }
      catch (Exception ex) {
         LastError = ex.Message;
      }
      finally {
         _gate.Release();
      }
      return sent;
   }

   /// <inheritdoc />
   public void Dispose() {
      Stop();
      _gate.Dispose();
   }
}
