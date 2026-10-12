using Em.Api.Core.Models;
using Em.Ui.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Em.Ui.Core.Tests;

public sealed class ClientLogTests : IDisposable
{
   private readonly string _folder = Path.Combine(Path.GetTempPath(), "em-clientlog-" + Guid.NewGuid().ToString("N"));

   public void Dispose() {
      try { Directory.Delete(_folder, true); } catch (IOException) { }
   }

   private ClientLogOptions Options(long maxFolder = 200L * 1024 * 1024, long segment = 1024 * 1024) =>
      new() { Folder = _folder, App = "TestClient", AppVersion = "1.0.0", MaxFolderBytes = maxFolder, SegmentBytes = segment };

   private sealed class FakeLogService : ILogServices
   {
      public List<LogIngestBatch> Batches { get; } = [];
      public bool Fail { get; set; }
      public Task PostMeta_LogIngest(LogIngestBatch batch) {
         if (Fail) throw new HttpRequestException("offline");
         Batches.Add(batch);
         return Task.CompletedTask;
      }
      public Task<LogSummary> GetMeta_LogSummary(DateTime from, DateTime to) => throw new NotSupportedException();
      public Task<LogEventPage> GetMeta_LogEvents(DateTime from, DateTime to, LogEventKind? kind, LogEventLevel? minLevel, string? category, string? text, string? requestId, string? userId, string? cursor, int take) => throw new NotSupportedException();
      public Task<LogErrorPage> GetMeta_LogErrors(DateTime from, DateTime to, LogErrorState? state, string? cursor, int take) => throw new NotSupportedException();
      public Task<LogErrorDetail> GetMeta_LogErrorDetail(string fingerprint) => throw new NotSupportedException();
      public Task PostMeta_LogErrorState(string fingerprint, LogErrorState state) => throw new NotSupportedException();
      public Task<AuditActivityPage> GetMeta_LogAudit(DateTime from, DateTime to, string? group, string? kind, string? targetKey, string? docNo, string? actorUserId, string? sessionId, string? cursor, int take) => throw new NotSupportedException();
      public Task<LogSignInFailureGroup[]> GetMeta_LogSignInFailures(DateTime from, DateTime to) => throw new NotSupportedException();
      public Task<AuditCatalogGroup[]> GetMeta_AuditCatalog() => throw new NotSupportedException();
      public Task<LogStoreSettingsView> GetMeta_LogStores() => throw new NotSupportedException();
      public Task<LogStoreTestResult> PostGetMeta_LogStoreTest(LogStoreDraft store) => throw new NotSupportedException();
      public Task<LogPrepareResult> PostGetMeta_LogStorePrepare(LogStorePrepareRequest request) => throw new NotSupportedException();
      public Task<LogStoreSettingsView> PostGetMeta_LogStoreSave(LogStoreSave request) => throw new NotSupportedException();
      public Task<LogRetentionView> GetMeta_LogRetention() => throw new NotSupportedException();
      public Task<LogRetentionPreview> PostGetMeta_LogRetentionPreview(LogRetentionRules rules) => throw new NotSupportedException();
      public Task<LogRetentionView> PostGetMeta_LogRetentionSave(LogRetentionRules rules) => throw new NotSupportedException();
   }

   private (ILoggerFactory Factory, ClientLogFolder Folder) Logger(ClientLogOptions options, Func<ClientLogUser>? user = null) {
      var folder = new ClientLogFolder(options);
      var provider = new ClientLogProvider(folder, options, user ?? (() => default));
      return (LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)), folder);
   }

   [Fact]
   public void EventsAreWrittenLocallyWithTheUserOfTheMomentAndBelowMinimumAreSkipped() {
      var current = new ClientLogUser("U1", "budi", null);
      var (factory, folder) = Logger(Options(), () => current);
      var logger = factory.CreateLogger("Em.Ui.Test");
      logger.LogDebug("not kept");
      logger.LogInformation("Opened {Screen}", "Invoice");
      current = default;
      logger.LogWarning("after sign-out");
      var events = folder.CloseAndList().SelectMany(folder.Read).ToList();
      Assert.Equal(2, events.Count);
      Assert.Equal(("U1", "budi"), (events[0].UserId, events[0].Account));
      Assert.Equal("client", events[0].Source);
      Assert.Equal("TestClient", events[0].App);
      Assert.Contains("\"Screen\":\"Invoice\"", events[0].Properties);
      Assert.Null(events[1].UserId);
   }

   [Fact]
   public async Task SuccessfulSendDeletesSegmentsAndFailureKeepsThem() {
      var (factory, folder) = Logger(Options());
      factory.CreateLogger("Em.Ui.Test").LogError(new InvalidOperationException("boom"), "Crash");
      var service = new FakeLogService { Fail = true };
      var shipper = new ClientLogShipper(folder, Options(), () => service);
      Assert.Equal(0, await shipper.SendAsync());
      Assert.Equal("offline", shipper.LastError);
      Assert.Single(folder.CloseAndList());

      service.Fail = false;
      Assert.Equal(1, await shipper.SendAsync());
      Assert.Empty(folder.CloseAndList());
      var batch = Assert.Single(service.Batches);
      var e = Assert.Single(batch.Events);
      Assert.Equal(LogEventKind.Error, e.Kind);
      Assert.StartsWith("seg-", batch.BatchId);
   }

   [Fact]
   public async Task NothingIsSentWhileSignedOut() {
      var (factory, folder) = Logger(Options());
      factory.CreateLogger("x").LogInformation("before sign-in");
      var shipper = new ClientLogShipper(folder, Options(), () => null);
      Assert.Equal(0, await shipper.SendAsync());
      Assert.Single(folder.CloseAndList());
   }

   [Fact]
   public async Task FullFolderDropsOldestSegmentsAndReportsTheCount() {
      var options = Options(maxFolder: 3000, segment: 600);
      var (factory, folder) = Logger(options);
      var logger = factory.CreateLogger("Em.Ui.Test");
      for (var i = 0; i < 60; i++) logger.LogInformation("Event number {Number} with some padding text", i);
      folder.CloseAndList();
      Assert.True(folder.Dropped > 0);
      var service = new FakeLogService();
      await new ClientLogShipper(folder, options, () => service).SendAsync();
      Assert.Equal(service.Batches[0].DroppedEvents, 60 - service.Batches.Sum(b => b.Events.Length));
      Assert.Equal(0, folder.Dropped);
   }

   [Fact]
   public void SegmentsLeftOpenByACrashAreRecovered() {
      var options = Options();
      var (factory, _) = Logger(options);
      factory.CreateLogger("x").LogCritical("crash");
      var next = new ClientLogFolder(options);
      next.RecoverOpenSegments();
      Assert.Single(Directory.GetFiles(_folder, "seg-*.jsonl"));
   }
}
