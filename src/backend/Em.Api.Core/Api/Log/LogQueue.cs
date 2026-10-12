using System.Collections.Concurrent;
using System.Threading.Channels;
using Em.Api.Core.Models;

namespace Em.Api.Core.Log;

/// <summary>
/// Bounded in-memory queue between the logger and the background writer. Never blocks: when it is full,
/// Debug/Information events are dropped (and counted) while Warning and above go to a small priority queue.
/// </summary>
internal sealed class LogQueue
{
   private const int PriorityCapacity = 5000;
   private readonly Channel<LogEvent> _channel;
   private readonly ConcurrentQueue<LogEvent> _priority = new();
   private readonly LogRuntime _runtime;
   private int _priorityCount;

   public LogQueue(int capacity, LogRuntime runtime) {
      _runtime = runtime;
      _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(capacity) {
         SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
      });
   }

   /// <summary>Queues an event; returns false when it was dropped.</summary>
   public bool Enqueue(LogEvent e) {
      if (_channel.Writer.TryWrite(e)) return true;
      if (e.Level >= LogEventLevel.Warning && Interlocked.Increment(ref _priorityCount) <= PriorityCapacity) {
         _priority.Enqueue(e);
         return true;
      }
      if (e.Level >= LogEventLevel.Warning) Interlocked.Decrement(ref _priorityCount);
      _runtime.AddDropped(1);
      return false;
   }

   /// <summary>Waits until at least one event is queued or <paramref name="timeout"/> passes.</summary>
   public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) {
      if (!_priority.IsEmpty) return;
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(timeout);
      try { await _channel.Reader.WaitToReadAsync(cts.Token); }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
   }

   /// <summary>Takes up to <paramref name="max"/> events, priority ones first.</summary>
   public List<LogEvent> Drain(int max) {
      var list = new List<LogEvent>();
      while (list.Count < max && _priority.TryDequeue(out var p)) {
         Interlocked.Decrement(ref _priorityCount);
         list.Add(p);
      }
      while (list.Count < max && _channel.Reader.TryRead(out var e)) list.Add(e);
      return list;
   }

   public int Count => _channel.Reader.Count + _priorityCount;
}
