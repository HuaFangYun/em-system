/*
    071 - Log engine: log store, MySQL 8.0+.
    Same tables and meaning as doc/sqlscript/mssql/tables/071-log-store.sql (see the value lists there). Run by
    Log Manager > Settings > Database > Prepare (embedded in Em.Api.Core) or by hand. Safe to run again; nothing
    is ever dropped. Times are UTC.
*/

CREATE TABLE IF NOT EXISTS `ta_LogSchema`
(
    `cLogSchemaId`      int          NOT NULL,
    `cLogSchemaVersion` int          NOT NULL,
    `ustamp`            datetime(3)  NOT NULL,
    PRIMARY KEY (`cLogSchemaId`),
    CONSTRAINT `CK_LogSchemaId` CHECK (`cLogSchemaId` = 1)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `ta_LogEvent`
(
    `cLogEventSeq`           bigint         NOT NULL AUTO_INCREMENT,
    `cLogEventTime`          datetime(3)    NOT NULL,
    `cLogEventKind`          int            NOT NULL,
    `cLogEventLevel`         int            NOT NULL,
    `cLogEventCategory`      varchar(200)   NOT NULL,
    `cLogEventTemplate`      varchar(1000)  NULL,
    `cLogEventMessage`       text           NOT NULL,
    `cLogEventProperties`    longtext       NULL,
    `cLogEventException`     longtext       NULL,
    `cLogEventFingerprint`   varchar(32)    NULL,
    `cLogEventApp`           varchar(100)   NULL,
    `cLogEventAppVersion`    varchar(50)    NULL,
    `cLogEventInstance`      varchar(100)   NULL,
    `cLogEventEnvironment`   varchar(50)    NULL,
    `cLogEventTenant`        varchar(100)   NULL,
    `cLogEventTraceId`       varchar(64)    NULL,
    `cLogEventSpanId`        varchar(32)    NULL,
    `cLogEventRequestId`     varchar(100)   NULL,
    `cLogEventSessionId`     varchar(26)    NULL,
    `cLogEventUserId`        varchar(26)    NULL,
    `cLogEventAccount`       varchar(100)   NULL,
    `cLogEventActingAs`      varchar(100)   NULL,
    `cLogEventClientAddress` varchar(64)    NULL,
    `cLogEventSource`        varchar(10)    NOT NULL,
    `cLogEventSender`        int            NOT NULL,
    `cLogEventSenderId`      varchar(100)   NULL,
    `cLogEventReceived`      datetime(3)    NOT NULL,
    PRIMARY KEY (`cLogEventSeq`),
    KEY `IX_LogEventTime` (`cLogEventTime`),
    KEY `IX_LogEventKindLevel` (`cLogEventKind`, `cLogEventLevel`, `cLogEventTime`),
    KEY `IX_LogEventRequest` (`cLogEventRequestId`),
    KEY `IX_LogEventFingerprint` (`cLogEventFingerprint`, `cLogEventTime`),
    KEY `IX_LogEventUser` (`cLogEventUserId`, `cLogEventTime`),
    CONSTRAINT `CK_LogEventKind` CHECK (`cLogEventKind` IN (0, 1, 2)),
    CONSTRAINT `CK_LogEventLevel` CHECK (`cLogEventLevel` BETWEEN 0 AND 5)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `ta_LogError`
(
    `cLogErrorId`             char(26)       NOT NULL,
    `cLogErrorFingerprint`    varchar(32)    NOT NULL,
    `cLogErrorType`           varchar(300)   NOT NULL,
    `cLogErrorTopFrame`       varchar(1000)  NULL,
    `cLogErrorMessage`        varchar(2000)  NULL,
    `cLogErrorCount`          bigint         NOT NULL,
    `cLogErrorFirstSeen`      datetime(3)    NOT NULL,
    `cLogErrorLastSeen`       datetime(3)    NOT NULL,
    `cLogErrorLastApp`        varchar(100)   NULL,
    `cLogErrorLastAppVersion` varchar(50)    NULL,
    `cLogErrorUsers`          longtext       NULL,
    `cLogErrorUserCount`      int            NOT NULL DEFAULT 0,
    `cLogErrorState`          int            NOT NULL DEFAULT 1,
    `ustamp`                  datetime(3)    NOT NULL,
    `datestamp`               datetime(3)    NOT NULL,
    `json_object`             longtext       NULL,
    PRIMARY KEY (`cLogErrorId`),
    UNIQUE KEY `UQ_LogErrorFingerprint` (`cLogErrorFingerprint`),
    KEY `IX_LogErrorLastSeen` (`cLogErrorLastSeen`),
    CONSTRAINT `CK_LogErrorState` CHECK (`cLogErrorState` IN (1, 2, 3, 4))
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `ta_LogIngestBatch`
(
    `cLogIngestBatchId`     varchar(64)   NOT NULL,
    `cLogIngestBatchTime`   datetime(3)   NOT NULL,
    `cLogIngestBatchSender` varchar(100)  NULL,
    `cLogIngestBatchEvents` int           NOT NULL,
    PRIMARY KEY (`cLogIngestBatchId`),
    KEY `IX_LogIngestBatchTime` (`cLogIngestBatchTime`)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `ta_LogAudit`
(
    `cLogAuditId`            char(26)       NOT NULL,
    `cLogAuditSeq`           bigint         NOT NULL AUTO_INCREMENT,
    `cLogAuditGroup`         varchar(100)   NOT NULL,
    `cLogAuditKind`          varchar(100)   NOT NULL,
    `cLogAuditTime`          datetime(3)    NOT NULL,
    `cLogAuditActorType`     varchar(20)    NOT NULL,
    `cLogAuditActorUserId`   varchar(26)    NULL,
    `cLogAuditActorAccount`  varchar(100)   NULL,
    `cLogAuditActingAs`      varchar(100)   NULL,
    `cLogAuditTargetType`    varchar(100)   NULL,
    `cLogAuditTargetKey`     varchar(400)   NULL,
    `cLogAuditDocNo`         varchar(100)   NULL,
    `cLogAuditSummary`       varchar(1000)  NULL,
    `cLogAuditReason`        varchar(1000)  NULL,
    `cLogAuditChanges`       longtext       NULL,
    `cLogAuditSnapshot`      longtext       NULL,
    `cLogAuditDetail`        longtext       NULL,
    `cLogAuditSource`        varchar(10)    NOT NULL DEFAULT 'server',
    `cLogAuditApp`           varchar(100)   NULL,
    `cLogAuditInstance`      varchar(100)   NULL,
    `cLogAuditClientAddress` varchar(64)    NULL,
    `cLogAuditRequestId`     varchar(100)   NULL,
    `cLogAuditTraceId`       varchar(64)    NULL,
    `cLogAuditSessionId`     varchar(26)    NULL,
    `cLogAuditShipState`     int            NOT NULL DEFAULT 0,
    PRIMARY KEY (`cLogAuditId`),
    UNIQUE KEY `UQ_LogAuditSeq` (`cLogAuditSeq`),
    KEY `IX_LogAuditGroupTarget` (`cLogAuditGroup`, `cLogAuditTargetKey`, `cLogAuditTime`),
    KEY `IX_LogAuditGroupTime` (`cLogAuditGroup`, `cLogAuditTime`),
    KEY `IX_LogAuditGroupDocNo` (`cLogAuditGroup`, `cLogAuditDocNo`),
    KEY `IX_LogAuditActor` (`cLogAuditActorUserId`, `cLogAuditTime`),
    KEY `IX_LogAuditTarget` (`cLogAuditTargetType`, `cLogAuditTargetKey`, `cLogAuditTime`),
    KEY `IX_LogAuditSession` (`cLogAuditSessionId`),
    KEY `IX_LogAuditTime` (`cLogAuditTime`),
    KEY `IX_LogAuditShip` (`cLogAuditShipState`, `cLogAuditSeq`),
    CONSTRAINT `CK_LogAuditShipState` CHECK (`cLogAuditShipState` IN (0, 1)),
    CONSTRAINT `CK_LogAuditSource` CHECK (`cLogAuditSource` IN ('server', 'client'))
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

INSERT IGNORE INTO `ta_LogSchema` (`cLogSchemaId`, `cLogSchemaVersion`, `ustamp`) VALUES (1, 1, UTC_TIMESTAMP(3));
