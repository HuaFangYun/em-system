using Em.Api.Core.Models;

namespace Em.Ui.Core.Logging;

/// <summary>Settings of the client log engine (WPF and MAUI hosts).</summary>
public sealed class ClientLogOptions
{
   /// <summary>Folder of the local segments, e.g. <c>%LocalAppData%\&lt;ApplicationName&gt;\Logs\outbox</c>.</summary>
   public required string Folder { get; init; }

   /// <summary>Application name written on every event.</summary>
   public required string App { get; init; }

   /// <summary>Application version written on every event.</summary>
   public string? AppVersion { get; init; }

   /// <summary>Lowest level that is kept; default Information.</summary>
   public LogEventLevel MinLevel { get; init; } = LogEventLevel.Information;

   /// <summary>Total size limit of the folder; the oldest segments are deleted above it. Default 200 MB.</summary>
   public long MaxFolderBytes { get; init; } = 200L * 1024 * 1024;

   /// <summary>A segment is closed once it reaches this size. Default 1 MB.</summary>
   public long SegmentBytes { get; init; } = 1024 * 1024;

   /// <summary>How often the shipper sends while signed in. Default 60 seconds.</summary>
   public TimeSpan SendInterval { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Who is signed in at the moment an event is written; <c>null</c> fields before sign-in.</summary>
/// <param name="UserId">User id.</param>
/// <param name="Account">Account name.</param>
/// <param name="SessionId">Session id, when the client knows it.</param>
public readonly record struct ClientLogUser(string? UserId, string? Account, string? SessionId);
