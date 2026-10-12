/*
    070 - Log engine: audit outbox (ta_LogAudit).

    Run on the core database after tables/010-core.sql, and on every other database whose context writes
    audit entries (a module connection named in AddAudit(...).Database("...")). When the log store is the
    internal database, this table in the core database is also the audit store. Safe to run again: the table
    is created only when it is missing. Guide: doc/engine/engine-log.md.

    Entries are written in the transaction of the data they describe. With a log store in another database the
    rows are written with cLogAuditShipState = 1, moved by the API to the store, then deleted here.

      cLogAuditActorType   user, robot, debug, system, anonymous
      cLogAuditSource      server (proven by the server) or client (reported by a client)
      cLogAuditShipState   0 in the store, 1 waiting to be moved to the store
      cLogAuditTargetKey   EF primary key; composite keys as an ordered JSON array

    Append-only: the application account needs only SELECT, INSERT and DELETE on this table, e.g.
      -- GRANT SELECT, INSERT, DELETE ON [dbo].[ta_LogAudit] TO [app_user];
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

COMMIT TRANSACTION;
GO
