/*
    070 - Log engine: audit outbox (ta_LogAudit), MySQL 8.0+.
    Same table and meaning as doc/sqlscript/mssql/tables/070-log-audit.sql; run on every application database
    whose context writes audit entries. Safe to run again. Guide: doc/engine/engine-log.md.
*/

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
