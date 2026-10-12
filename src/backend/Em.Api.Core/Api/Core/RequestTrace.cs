using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Em.Shared;

namespace Em.Api.Core
{
   /// <summary>
   /// Why an action stopped before its answer reached the caller.
   /// </summary>
   internal enum AbortReason
   {
      /// <summary>
      /// The caller left - the application was closed, the network dropped, or they disconnected themselves.
      /// No answer is sent, because nobody is receiving it anymore.
      /// </summary>
      CallerGone,

      /// <summary>
      /// The action's time limit ran out while the caller was still waiting. The answer is still sent - as a
      /// 504 - because someone who waits has the right to know why they got nothing.
      /// </summary>
      TimedOut
   }

   /// <summary>
   /// The trace of one request on the console: from when it arrives, when its action starts being
   /// handled, until it completes, is refused, or is cancelled - complete with the caller's address, their
   /// identity if any, and how long it took. One object per request, created in
   /// <c>EmApp.ProcessRequest</c> and dying with that request.
   /// </summary>
   /// <remarks>
   /// Internal, not public: this is the engine's own trace, not a log-writing tool for module authors -
   /// that still goes through <c>ServicesBase.Logger</c>. All its lines are written with the category
   /// <see cref="LoggerCategory"/>, so they can be raised or turned off on their own through
   /// <c>appsettings.json</c> without turning off other logs.
   /// </remarks>
   internal sealed class RequestTrace
   {
      /// <summary>
      /// Logger category used by all request trace lines. Deliberately a fixed name, not a type name: this
      /// category is what people type in <c>appsettings.json</c>, so it must not change just because this
      /// class is moved or renamed.
      /// </summary>
      internal const string LoggerCategory = "Em.Api.Request";

      private const string UnknownAddress = "an unknown address";

      private const string AnonymousCaller = "anonymous";

      // Sequence number so the lines of one request can be strung together when several requests run at the
      // same time - without it, IN, START, and DONE on the console cannot be paired up. Deliberately a
      // short number, not the default ASP.NET Core TraceIdentifier, because the reader of the console is a
      // human eye.
      private static int _counter;

      private readonly ILogger _logger;
      private readonly HttpContext _http;
      private readonly LogLevel _infoLevel;
      private readonly string _id;
      private readonly string _requestId;
      private readonly string _method;
      private readonly string _routeLabel;
      private readonly string _caller;
      private readonly long _receivedTimestamp;

      private long _startedTimestamp;
      private ActionRequest? _request;

      private RequestTrace(ILogger logger, HttpContext http, string routeLabel) {
         _logger = logger;
         _http = http;
         _id = (Interlocked.Increment(ref _counter) & 0xFFFFF).ToString("X5");
         // The short id restarts with the process and wraps; the stored request id adds the instance and the
         // start time so it stays unique across restarts and servers.
         _requestId = $"{Environment.MachineName}:{_id}:{DateTime.UtcNow:yyyyMMddHHmmss}";
         http.Items[Em.Api.Core.Log.Audit.AuditWriter.RequestIdItemKey] = _requestId;
         // Reading or sending logs must not produce stored log rows about itself: the log module's own
         // requests are traced at Debug level, which is not stored by default.
         _infoLevel = routeLabel.StartsWith(Defaults.LogModuleName + "/", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Debug : LogLevel.Information;
         _method = http.Request.Method;
         _routeLabel = routeLabel;
         _caller = DescribeCaller(http);
         _receivedTimestamp = Stopwatch.GetTimestamp();
      }

      /// <summary>
      /// Records a request that has just arrived, then returns its trace to be used until the request
      /// finishes. Called first, before the identity gate runs, so a request that is later refused still
      /// shows that it arrived.
      /// </summary>
      internal static RequestTrace Begin(HttpContext http, string routeLabel) {
         var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
         var trace = new RequestTrace(logger, http, routeLabel);

         trace._logger.Log(trace._infoLevel, "#{Id} {Phase,-6} {Method} {Route} from {Caller}",
            trace._id, "IN", trace._method, trace._routeLabel, trace._caller);

         return trace;
      }

      /// <summary>
      /// Tells this trace who the caller is, after the gate has finished checking them. Writes nothing by
      /// itself - the identity only appears on the next line, because only a proven identity deserves to be
      /// written.
      /// </summary>
      internal void Identified(ActionRequest request) => _request = request;

      /// <summary>
      /// Opens the logging scope of the request: its unique id, trace id and client address. Lines written by
      /// any code while the scope is open carry these values (the log engine stores them as envelope fields).
      /// </summary>
      internal IDisposable? BeginRequestScope() =>
         _logger.BeginScope(new Dictionary<string, object?> {
            ["RequestId"] = _requestId,
            ["TraceId"] = Activity.Current?.TraceId.ToString(),
            ["ClientAddress"] = _http.Connection.RemoteIpAddress?.ToString()
         });

      /// <summary>Opens the logging scope of the caller's identity, once the gate has checked it.</summary>
      internal IDisposable? BeginIdentityScope() {
         if (_request is not { IsAuthenticated: true } request) return null;
         return _logger.BeginScope(new Dictionary<string, object?> {
            ["UserId"] = request.cUserId,
            ["Account"] = request.cUserAccount,
            ["SessionId"] = request.cUserSessionId,
            ["DebugKey"] = request.DebugKeyName,
            ["Impersonating"] = request.IsImpersonating,
            ["ActingAs"] = request.IsImpersonating ? request.cUserAccount : null
         });
      }

      /// <summary>
      /// Records that the action started being handled, and starts the processing timer. Called after its
      /// arguments are bound, so what is measured is really the action's working time.
      /// </summary>
      internal void Processing() {
         _startedTimestamp = Stopwatch.GetTimestamp();

         _logger.Log(_infoLevel, "#{Id} {Phase,-6} {Method} {Route} from {Caller} as {User}",
            _id, "START", _method, _routeLabel, _caller, DescribeUser());
      }

      /// <summary>
      /// Records an action that finished together with how long it took. A failed result also comes through
      /// here - the action did get to run - and is raised in level according to its status.
      /// </summary>
      internal void Completed(ActionResult result) {
         _logger.Log(LevelFor(result.StatusCode, result.ValidResult),
            "#{Id} {Phase,-6} {Method} {Route} from {Caller} as {User} - {StatusCode} in {Elapsed:0.0} ms (total {Total:0.0} ms)",
            _id, "DONE", _method, _routeLabel, _caller, DescribeUser(), result.StatusCode,
            Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
            Stopwatch.GetElapsedTime(_receivedTimestamp).TotalMilliseconds);
      }

      /// <summary>
      /// Records an action that stopped before its answer reached the caller. Deliberately a line of its own,
      /// not <see cref="Completed"/> with another status: <c>DONE 200</c> for an answer that never arrived
      /// would mislead exactly when the log is needed most.
      /// </summary>
      /// <remarks>
      /// Its level is <see cref="LogLevel.Warning"/>, not <see cref="LogLevel.Error"/>. A caller closing
      /// their application is no server disturbance, and if every occurrence raised a red flag, the result
      /// would not be people becoming alert but the log no longer being read.
      /// </remarks>
      internal void Aborted(AbortReason reason) {
         var described = reason == AbortReason.CallerGone
            ? "the caller went away"
            : "it ran out of time";

         _logger.Log(_infoLevel == LogLevel.Debug ? LogLevel.Debug : LogLevel.Warning,
            "#{Id} {Phase,-6} {Method} {Route} from {Caller} as {User} - {Reason} after {Elapsed:0.0} ms (total {Total:0.0} ms)",
            _id, "ABORT", _method, _routeLabel, _caller, DescribeUser(), described,
            Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
            Stopwatch.GetElapsedTime(_receivedTimestamp).TotalMilliseconds);
      }

      /// <summary>
      /// Records a request answered without its action getting to run - its identity was refused, the action
      /// does not exist, the verb is wrong, or its parameters could not be bound.
      /// </summary>
      internal void Rejected(ActionResult result) {
         _logger.Log(LevelFor(result.StatusCode, result.ValidResult),
            "#{Id} {Phase,-6} {Method} {Route} from {Caller} as {User} - {StatusCode} {Reason} after {Elapsed:0.0} ms",
            _id, "REJECT", _method, _routeLabel, _caller, DescribeUser(), result.StatusCode,
            result.ErrorMessage ?? "no reason given",
            Stopwatch.GetElapsedTime(_receivedTimestamp).TotalMilliseconds);
      }

      // 500 and above is a server error, so it deserves Error; 4xx is a caller error, so Warning is enough
      // to keep the production console from filling up with things that are not disturbances.
      // The log module's own lines stay at Debug unless the server failed (5xx).
      private LogLevel LevelFor(int statusCode, bool validResult) =>
         validResult ? _infoLevel :
         statusCode >= 500 ? LogLevel.Error : _infoLevel == LogLevel.Debug ? LogLevel.Debug : LogLevel.Warning;

      /// <summary>
      /// Composes the caller's label for the log: the account name when the name is proven from data,
      /// otherwise just the id, and <c>anonymous</c> while there is no identity at all. A name attached in the
      /// header is never used here - it comes from the caller, not from data, and a log that records a claim
      /// as fact is worse than a log that stays silent.
      /// </summary>
      private string DescribeUser() {
         if (_request is not { IsAuthenticated: true } request) {
            return AnonymousCaller;
         }

         var who = request.cUserAccount is { } account
            ? $"{account} ({request.cUserId})"
            : request.cUserId!;

         if (!request.IsDebugRequest) {
            return who;
         }

         return request.IsImpersonating
            ? $"{who} [debug:{request.DebugKeyName}, impersonating]"
            : $"{who} [debug:{request.DebugKeyName}]";
      }

      /// <summary>
      /// Composes the label for the caller's address. The one in front is always the real client address: when
      /// the request arrives through a trusted proxy, <c>UseForwardedHeaders</c> has already replaced it from
      /// <c>X-Forwarded-For</c> before this line runs, and the replaced proxy address is also written after
      /// <c>via</c> so the path it took stays visible.
      /// </summary>
      internal static string DescribeCaller(HttpContext http) {
         var client = http.Connection.RemoteIpAddress?.ToString() ?? UnknownAddress;
         var original = http.Request.Headers[ForwardedHeadersDefaults.XOriginalForHeaderName];

         if (original.Count == 0) {
            return client;
         }

         var hops = original
            .SelectMany(r => (r ?? string.Empty)
               .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(StripPort)
            .Where(r => r.Length > 0)
            .ToArray();

         return hops.Length == 0 ? client : $"{client} via {string.Join(" via ", hops)}";
      }

      // The address left behind by the middleware also carries a port number ("10.0.0.2:51324"), and a
      // proxy's port explains nothing - what the log reader is looking for is the machine.
      private static string StripPort(string value) =>
         IPEndPoint.TryParse(value, out var endpoint) ? endpoint.Address.ToString() : value;
   }
}
