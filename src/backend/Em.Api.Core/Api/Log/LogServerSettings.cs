namespace Em.Api.Shared;

/// <summary>
/// Settings of the log engine, filled from <see cref="EmApiConfig.Log"/> by <see cref="EmAppBuilder.ApplyConfig"/>
/// or set in the host before <see cref="EmAppBuilder.AddLogServer"/>.
/// </summary>
public sealed class LogServerSettings
{
   /// <summary>Folder of the disk segments kept while the store cannot be written; relative to the content root.</summary>
   public string LogPath { get; set; } = "./data/logs";

   /// <summary>Also store the IN and START request trace lines (default: only DONE, ABORT and REJECT).</summary>
   public bool StoreRequestStartLines { get; set; }

   /// <summary>Capacity of the in-memory telemetry queue, in events.</summary>
   public int QueueCapacity { get; set; } = 20_000;

   /// <summary>Total size limit of the disk segments, in bytes.</summary>
   public long OutboxMaxBytes { get; set; } = 500L * 1024 * 1024;

   /// <summary>Application name written on every event; <c>null</c> uses the entry assembly name.</summary>
   public string? AppName { get; set; }
}
