using System.Collections.Concurrent;
using Em.Api.Core.Models;
using Em.Api.Core.ObjectStorage.Volumes;
using Em.Shared;

namespace Em.Api.Core.ObjectStorage;

/// <summary>
/// Process-wide state of object storage: volume providers and opened volumes, the per-hash lock, maintenance
/// gates, the replication signal and the result of the last background pass. Registered as a singleton;
/// object storage assumes a single API instance per database and volume set.
/// </summary>
public sealed class ObjectStorageRuntime
{
   private readonly Dictionary<StoreVolumeType, IStoreVolumeProvider> _providers;
   private readonly ConcurrentDictionary<(string Id, int Type, string Path), IStoreVolume> _volumes = new();
   private readonly SemaphoreSlim _signal = new(0, 1);

   /// <summary>Creates the runtime with the available volume providers.</summary>
   public ObjectStorageRuntime(IEnumerable<IStoreVolumeProvider> providers) {
      _providers = providers.ToDictionary(p => p.Type);
   }

   /// <summary>Lock per blob hash.</summary>
   public StoreHashLock HashLock { get; } = new();

   /// <summary>Serializes garbage collection runs.</summary>
   public SemaphoreSlim GcGate { get; } = new(1, 1);

   /// <summary>Serializes retention runs.</summary>
   public SemaphoreSlim RetentionGate { get; } = new(1, 1);

   /// <summary>Serializes replication and repair runs.</summary>
   public SemaphoreSlim RepairGate { get; } = new(1, 1);

   /// <summary>Default grace period of garbage collection, in hours.</summary>
   public const int DefaultGraceHours = 24;

   /// <summary>When background maintenance last finished a pass.</summary>
   public DateTime? LastMaintenanceUtc { get; private set; }

   /// <summary>Summary of the last background pass.</summary>
   public string? LastMaintenanceMessage { get; private set; }

   /// <summary>Records the result of a background pass.</summary>
   public void ReportMaintenance(string message) {
      LastMaintenanceUtc = DateTime.UtcNow;
      LastMaintenanceMessage = message;
   }

   /// <summary>Provider of a volume kind, throwing 400 when that kind is not available.</summary>
   public IStoreVolumeProvider Provider(StoreVolumeType type) =>
      _providers.TryGetValue(type, out var provider) ? provider : throw new ActionException($"Volume type {type} is not available.", 400);

   /// <summary>Opened volume of a row. Instances are cached per ID, type and path, so a saved change takes effect immediately.</summary>
   public IStoreVolume Volume(ta_StoreVolume row) =>
      _volumes.GetOrAdd((row.cStoreVolumeId, row.cStoreVolumeType, row.cStoreVolumePath),
         key => Provider((StoreVolumeType)key.Type).Open(key.Id, key.Path));

   /// <summary>Drops cached volumes after a volume is saved or deleted.</summary>
   public void InvalidateVolumes() => _volumes.Clear();

   /// <summary>Asks background maintenance to replicate soon (after an upload left a blob below its replica target).</summary>
   public void SignalReplication() {
      try { _signal.Release(); }
      catch (SemaphoreFullException) { }
   }

   /// <summary>Waits until <see cref="SignalReplication"/> is called or <paramref name="timeout"/> passes.</summary>
   public async Task<bool> WaitSignalAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
}
