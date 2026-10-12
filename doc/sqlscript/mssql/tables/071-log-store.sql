/*
    071 - Log engine: log store (telemetry events, error groups, ingest batches, audit, schema version).

    Run on the log store database. With the internal store that is the core database (run after
    070-log-audit.sql); another store is usually prepared from Log Manager > Settings > Database > Prepare,
    which runs this same file (embedded in Em.Api.Core). Safe to run again; nothing is ever dropped.
    Guide: doc/engine/engine-log.md.

      cLogEventKind    0 Diagnostic, 1 Error, 2 Access
      cLogEventLevel   0 Trace, 1 Debug, 2 Information, 3 Warning, 4 Error, 5 Critical
      cLogEventSender  0 the API process, 1 a signed-in user's client, 2 a robot
      cLogErrorState   1 New, 2 Acknowledged, 3 Resolved, 4 Ignored
      Times are UTC.

    Runtime rights: SELECT/INSERT/UPDATE/DELETE on ta_LogError, SELECT/INSERT/DELETE on the other tables.
*/

SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
GO

BEGIN TRANSACTION;

IF OBJECT_ID(N'[dbo].[ta_LogSchema]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_LogSchema]
    (
        [cLogSchemaId]      int           NOT NULL,
        [cLogSchemaVersion] int           NOT NULL,
        [ustamp]            datetime2(3)  NOT NULL,
        CONSTRAINT [PK_LogSchema] PRIMARY KEY ([cLogSchemaId]),
        CONSTRAINT [CK_LogSchemaId] CHECK ([cLogSchemaId] = 1)
    );
END;

IF OBJECT_ID(N'[dbo].[ta_LogEvent]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_LogEvent]
    (
        [cLogEventSeq]           bigint          IDENTITY(1, 1) NOT NULL,
        [cLogEventTime]          datetime2(3)    NOT NULL,
        [cLogEventKind]          int             NOT NULL,
        [cLogEventLevel]         int             NOT NULL,
        [cLogEventCategory]      nvarchar(200)   NOT NULL,
        [cLogEventTemplate]      nvarchar(1000)  NULL,
        [cLogEventMessage]       nvarchar(4000)  NOT NULL,
        [cLogEventProperties]    nvarchar(max)   NULL,
        [cLogEventException]     nvarchar(max)   NULL,
        [cLogEventFingerprint]   varchar(32)     NULL,
        [cLogEventApp]           nvarchar(100)   NULL,
        [cLogEventAppVersion]    nvarchar(50)    NULL,
        [cLogEventInstance]      nvarchar(100)   NULL,
        [cLogEventEnvironment]   nvarchar(50)    NULL,
        [cLogEventTenant]        nvarchar(100)   NULL,
        [cLogEventTraceId]       varchar(64)     NULL,
        [cLogEventSpanId]        varchar(32)     NULL,
        [cLogEventRequestId]     varchar(100)    NULL,
        [cLogEventSessionId]     varchar(26)     NULL,
        [cLogEventUserId]        varchar(26)     NULL,
        [cLogEventAccount]       nvarchar(100)   NULL,
        [cLogEventActingAs]      nvarchar(100)   NULL,
        [cLogEventClientAddress] varchar(64)     NULL,
        [cLogEventSource]        varchar(10)     NOT NULL,
        [cLogEventSender]        int             NOT NULL,
        [cLogEventSenderId]      nvarchar(100)   NULL,
        [cLogEventReceived]      datetime2(3)    NOT NULL,
        CONSTRAINT [PK_LogEvent] PRIMARY KEY CLUSTERED ([cLogEventSeq]),
        CONSTRAINT [CK_LogEventKind] CHECK ([cLogEventKind] IN (0, 1, 2)),
        CONSTRAINT [CK_LogEventLevel] CHECK ([cLogEventLevel] BETWEEN 0 AND 5)
    );
    CREATE INDEX [IX_LogEventTime] ON [dbo].[ta_LogEvent] ([cLogEventTime]);
    CREATE INDEX [IX_LogEventKindLevel] ON [dbo].[ta_LogEvent] ([cLogEventKind], [cLogEventLevel], [cLogEventTime]);
    CREATE INDEX [IX_LogEventRequest] ON [dbo].[ta_LogEvent] ([cLogEventRequestId]);
    CREATE INDEX [IX_LogEventFingerprint] ON [dbo].[ta_LogEvent] ([cLogEventFingerprint], [cLogEventTime]);
    CREATE INDEX [IX_LogEventUser] ON [dbo].[ta_LogEvent] ([cLogEventUserId], [cLogEventTime]);
END;

IF OBJECT_ID(N'[dbo].[ta_LogError]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_LogError]
    (
        [cLogErrorId]             char(26)        NOT NULL,
        [cLogErrorFingerprint]    varchar(32)     NOT NULL,
        [cLogErrorType]           nvarchar(300)   NOT NULL,
        [cLogErrorTopFrame]       nvarchar(1000)  NULL,
        [cLogErrorMessage]        nvarchar(2000)  NULL,
        [cLogErrorCount]          bigint          NOT NULL,
        [cLogErrorFirstSeen]      datetime2(3)    NOT NULL,
        [cLogErrorLastSeen]       datetime2(3)    NOT NULL,
        [cLogErrorLastApp]        nvarchar(100)   NULL,
        [cLogErrorLastAppVersion] nvarchar(50)    NULL,
        [cLogErrorUsers]          nvarchar(max)   NULL,
        [cLogErrorUserCount]      int             NOT NULL CONSTRAINT [DF_LogErrorUserCount] DEFAULT (0),
        [cLogErrorState]          int             NOT NULL CONSTRAINT [DF_LogErrorState] DEFAULT (1),
        [ustamp]                  datetime2(3)    NOT NULL,
        [datestamp]               datetime2(3)    NOT NULL,
        [json_object]             nvarchar(max)   NULL,
        CONSTRAINT [PK_LogError] PRIMARY KEY ([cLogErrorId]),
        CONSTRAINT [UQ_LogErrorFingerprint] UNIQUE ([cLogErrorFingerprint]),
        CONSTRAINT [CK_LogErrorState] CHECK ([cLogErrorState] IN (1, 2, 3, 4))
    );
    CREATE INDEX [IX_LogErrorLastSeen] ON [dbo].[ta_LogError] ([cLogErrorLastSeen] DESC);
END;

IF OBJECT_ID(N'[dbo].[ta_LogIngestBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_LogIngestBatch]
    (
        [cLogIngestBatchId]     varchar(64)    NOT NULL,
        [cLogIngestBatchTime]   datetime2(3)   NOT NULL,
        [cLogIngestBatchSender] nvarchar(100)  NULL,
        [cLogIngestBatchEvents] int            NOT NULL,
        CONSTRAINT [PK_LogIngestBatch] PRIMARY KEY ([cLogIngestBatchId])
    );
    CREATE INDEX [IX_LogIngestBatchTime] ON [dbo].[ta_LogIngestBatch] ([cLogIngestBatchTime]);
END;

IF OBJECT_ID(N'[dbo].[ta_LogAudit]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_LogAudit]
    (
        [cLogAuditId]            char(26)        NOT NULL,
        [cLogAuditSeq]           bigint          IDENTITY(1, 1) NOT NULL,
        [cLogAuditGroup]         varchar(100)    NOT NULL,
        [cLogAuditKind]          varchar(100)    NOT NULL,
        [cLogAuditTime]          datetime2(3)    NOT NULL,
        [cLogAuditActorType]     varchar(20)     NOT NULL,
        [cLogAuditActorUserId]   varchar(26)     NULL,
        [cLogAuditActorAccount]  nvarchar(100)   NULL,
        [cLogAuditActingAs]      nvarchar(100)   NULL,
        [cLogAuditTargetType]    varchar(100)    NULL,
        [cLogAuditTargetKey]     nvarchar(400)   NULL,
        [cLogAuditDocNo]         nvarchar(100)   NULL,
        [cLogAuditSummary]       nvarchar(1000)  NULL,
        [cLogAuditReason]        nvarchar(1000)  NULL,
        [cLogAuditChanges]       nvarchar(max)   NULL,
        [cLogAuditSnapshot]      nvarchar(max)   NULL,
        [cLogAuditDetail]        nvarchar(max)   NULL,
        [cLogAuditSource]        varchar(10)     NOT NULL CONSTRAINT [DF_LogAuditSource] DEFAULT ('server'),
        [cLogAuditApp]           nvarchar(100)   NULL,
        [cLogAuditInstance]      nvarchar(100)   NULL,
        [cLogAuditClientAddress] varchar(64)     NULL,
        [cLogAuditRequestId]     varchar(100)    NULL,
        [cLogAuditTraceId]       varchar(64)     NULL,
        [cLogAuditSessionId]     varchar(26)     NULL,
        [cLogAuditShipState]     int             NOT NULL CONSTRAINT [DF_LogAuditShipState] DEFAULT (0),
        CONSTRAINT [PK_LogAudit] PRIMARY KEY NONCLUSTERED ([cLogAuditId]),
        CONSTRAINT [UQ_LogAuditSeq] UNIQUE CLUSTERED ([cLogAuditSeq]),
        CONSTRAINT [CK_LogAuditShipState] CHECK ([cLogAuditShipState] IN (0, 1)),
        CONSTRAINT [CK_LogAuditSource] CHECK ([cLogAuditSource] IN ('server', 'client'))
    );
    CREATE INDEX [IX_LogAuditGroupTarget] ON [dbo].[ta_LogAudit] ([cLogAuditGroup], [cLogAuditTargetKey], [cLogAuditTime] DESC);
    CREATE INDEX [IX_LogAuditGroupTime] ON [dbo].[ta_LogAudit] ([cLogAuditGroup], [cLogAuditTime] DESC);
    CREATE INDEX [IX_LogAuditGroupDocNo] ON [dbo].[ta_LogAudit] ([cLogAuditGroup], [cLogAuditDocNo]);
    CREATE INDEX [IX_LogAuditActor] ON [dbo].[ta_LogAudit] ([cLogAuditActorUserId], [cLogAuditTime] DESC);
    CREATE INDEX [IX_LogAuditTarget] ON [dbo].[ta_LogAudit] ([cLogAuditTargetType], [cLogAuditTargetKey], [cLogAuditTime] DESC);
    CREATE INDEX [IX_LogAuditSession] ON [dbo].[ta_LogAudit] ([cLogAuditSessionId]);
    CREATE INDEX [IX_LogAuditTime] ON [dbo].[ta_LogAudit] ([cLogAuditTime] DESC);
    CREATE INDEX [IX_LogAuditShip] ON [dbo].[ta_LogAudit] ([cLogAuditSeq]) WHERE [cLogAuditShipState] = 1;
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ta_LogSchema] WHERE [cLogSchemaId] = 1)
    INSERT INTO [dbo].[ta_LogSchema] ([cLogSchemaId], [cLogSchemaVersion], [ustamp]) VALUES (1, 1, SYSUTCDATETIME());

COMMIT TRANSACTION;
GO
