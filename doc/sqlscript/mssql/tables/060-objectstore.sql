/*
    060 - Object storage engine: volumes, policies, buckets, objects, versions and blobs.

    Run on the core database after tables/010-core.sql (ta_Meta is used for the instance lease).
    Safe to run again: every object is created only when it is missing. Run the views
    views/vi_StoreBucket.sql, views/vi_StoreObject.sql and views/vi_StoreVolume.sql after all tables/ files.
    The application supplies ULIDs and stamps. Guide: doc/engine/engine-object-storage.md.

    State values (defined by the application):
      cStoreVolumeState          1 Active, 2 ReadOnly, 3 Offline
      cStoreVolumeType           1 LocalFolder
      cStorePolicyState          1 Active, 2 Disabled (no new buckets)
      cStorePolicyAccess         1 Private, 2 PublicRead
      cStoreBucketState          1 Active, 2 ReadOnly
      cStoreObjectState          1 Active
      cStoreObjectBlobState      1 Ready, 2 Orphan
      cStoreObjectBlobVolState   1 Writing, 2 Ready, 3 Missing, 4 Deleting

    Blob identity is the SHA-256 of the content (binary(32)); blobs are shared across buckets.
    Object keys are case-sensitive (binary collation). Uniqueness of a key inside a bucket is
    enforced through cStoreObjectKeyHash (SHA-256 of the UTF-8 key) because an index on the full
    nvarchar(1024) key would exceed the 1700-byte index key limit.
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

IF OBJECT_ID(N'[dbo].[ta_StoreVolume]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StoreVolume]
    (
        [cStoreVolumeId]    char(26)       NOT NULL,
        [cStoreVolumeName]  varchar(100)   COLLATE Latin1_General_100_CI_AS NOT NULL,
        [cStoreVolumeState] int            NOT NULL,
        [cStoreVolumeType]  int            NOT NULL,
        [cStoreVolumePath]  nvarchar(400)  NOT NULL,
        [cStoreVolumeNote]  nvarchar(500)  NULL,
        [ustamp]            datetime       NOT NULL,
        [datestamp]         datetime       NOT NULL,
        [json_object]       nvarchar(max)  NULL,
        CONSTRAINT [PK_StoreVolume] PRIMARY KEY ([cStoreVolumeId]),
        CONSTRAINT [UQ_StoreVolumeName] UNIQUE ([cStoreVolumeName]),
        CONSTRAINT [CK_StoreVolumeState] CHECK ([cStoreVolumeState] IN (1, 2, 3)),
        CONSTRAINT [CK_StoreVolumeType] CHECK ([cStoreVolumeType] IN (1))
    );
END;

IF OBJECT_ID(N'[dbo].[ta_StorePolicy]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StorePolicy]
    (
        [cStorePolicyId]             char(26)       NOT NULL,
        [cStorePolicyName]           varchar(100)   COLLATE Latin1_General_100_CI_AS NOT NULL,
        [cStorePolicyState]          int            NOT NULL,
        [cStorePolicyNote]           nvarchar(500)  NULL,
        [cStorePolicyReplicaMin]     int            NOT NULL CONSTRAINT [DF_StorePolicyReplicaMin] DEFAULT (1),
        [cStorePolicyReplicaTarget]  int            NOT NULL CONSTRAINT [DF_StorePolicyReplicaTarget] DEFAULT (1),
        [cStorePolicyKeepVersions]   int            NULL,
        [cStorePolicyKeepDays]       int            NULL,
        [cStorePolicyQuotaBytes]     bigint         NULL,
        [cStorePolicyQuotaObjects]   bigint         NULL,
        [cStorePolicyMaxObjectBytes] bigint         NULL,
        [cStorePolicyAccess]         int            NOT NULL CONSTRAINT [DF_StorePolicyAccess] DEFAULT (1),
        [ustamp]                     datetime       NOT NULL,
        [datestamp]                  datetime       NOT NULL,
        [json_object]                nvarchar(max)  NULL,
        CONSTRAINT [PK_StorePolicy] PRIMARY KEY ([cStorePolicyId]),
        CONSTRAINT [UQ_StorePolicyName] UNIQUE ([cStorePolicyName]),
        CONSTRAINT [CK_StorePolicyState] CHECK ([cStorePolicyState] IN (1, 2)),
        CONSTRAINT [CK_StorePolicyReplica] CHECK ([cStorePolicyReplicaMin] >= 1 AND [cStorePolicyReplicaMin] <= [cStorePolicyReplicaTarget]),
        CONSTRAINT [CK_StorePolicyKeepVersions] CHECK ([cStorePolicyKeepVersions] IS NULL OR [cStorePolicyKeepVersions] >= 0),
        CONSTRAINT [CK_StorePolicyKeepDays] CHECK ([cStorePolicyKeepDays] IS NULL OR [cStorePolicyKeepDays] >= 1),
        CONSTRAINT [CK_StorePolicyQuotaBytes] CHECK ([cStorePolicyQuotaBytes] IS NULL OR [cStorePolicyQuotaBytes] >= 0),
        CONSTRAINT [CK_StorePolicyQuotaObjects] CHECK ([cStorePolicyQuotaObjects] IS NULL OR [cStorePolicyQuotaObjects] >= 0),
        CONSTRAINT [CK_StorePolicyMaxObjectBytes] CHECK ([cStorePolicyMaxObjectBytes] IS NULL OR [cStorePolicyMaxObjectBytes] >= 1),
        CONSTRAINT [CK_StorePolicyAccess] CHECK ([cStorePolicyAccess] IN (1, 2))
    );
END;

IF OBJECT_ID(N'[dbo].[ta_StorePolicyVolume]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StorePolicyVolume]
    (
        [cStorePolicyId]          char(26) NOT NULL,
        [cStoreVolumeId]          char(26) NOT NULL,
        [cStorePolicyVolumeOrder] int      NOT NULL,
        CONSTRAINT [PK_StorePolicyVolume] PRIMARY KEY ([cStorePolicyId], [cStoreVolumeId]),
        CONSTRAINT [FK_StorePolicyVolumePolicy] FOREIGN KEY ([cStorePolicyId]) REFERENCES [dbo].[ta_StorePolicy] ([cStorePolicyId]) ON DELETE CASCADE,
        CONSTRAINT [FK_StorePolicyVolumeVolume] FOREIGN KEY ([cStoreVolumeId]) REFERENCES [dbo].[ta_StoreVolume] ([cStoreVolumeId])
    );
    CREATE INDEX [IX_StorePolicyVolumeVolume] ON [dbo].[ta_StorePolicyVolume] ([cStoreVolumeId]);
END;

IF OBJECT_ID(N'[dbo].[ta_StoreBucket]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StoreBucket]
    (
        [cStoreBucketId]      char(26)       NOT NULL,
        [cStoreBucketName]    varchar(63)    COLLATE Latin1_General_100_BIN2 NOT NULL,
        [cStorePolicyId]      char(26)       NOT NULL,
        [cStoreBucketState]   int            NOT NULL,
        [cStoreBucketNote]    nvarchar(500)  NULL,
        [cStoreBucketBytes]   bigint         NOT NULL CONSTRAINT [DF_StoreBucketBytes] DEFAULT (0),
        [cStoreBucketObjects] bigint         NOT NULL CONSTRAINT [DF_StoreBucketObjects] DEFAULT (0),
        [ustamp]              datetime       NOT NULL,
        [datestamp]           datetime       NOT NULL,
        [json_object]         nvarchar(max)  NULL,
        CONSTRAINT [PK_StoreBucket] PRIMARY KEY ([cStoreBucketId]),
        CONSTRAINT [UQ_StoreBucketName] UNIQUE ([cStoreBucketName]),
        CONSTRAINT [FK_StoreBucketPolicy] FOREIGN KEY ([cStorePolicyId]) REFERENCES [dbo].[ta_StorePolicy] ([cStorePolicyId]),
        CONSTRAINT [CK_StoreBucketState] CHECK ([cStoreBucketState] IN (1, 2)),
        CONSTRAINT [CK_StoreBucketCounters] CHECK ([cStoreBucketBytes] >= 0 AND [cStoreBucketObjects] >= 0)
    );
    CREATE INDEX [IX_StoreBucketPolicy] ON [dbo].[ta_StoreBucket] ([cStorePolicyId]);
END;

IF OBJECT_ID(N'[dbo].[ta_StoreObject]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StoreObject]
    (
        [cStoreObjectId]             char(26)        NOT NULL,
        [cStoreBucketId]             char(26)        NOT NULL,
        [cStoreObjectKey]            nvarchar(1024)  COLLATE Latin1_General_100_BIN2 NOT NULL,
        [cStoreObjectKeyHash]        binary(32)      NOT NULL,
        -- First 400 characters of the key, indexed for prefix listing (see the header).
        [cStoreObjectKeyPrefix]      AS CAST(LEFT([cStoreObjectKey], 400) AS nvarchar(400)) PERSISTED,
        [cStoreObjectVersionCurrent] int             NULL,
        [cStoreObjectVersionNext]    int             NOT NULL CONSTRAINT [DF_StoreObjectVersionNext] DEFAULT (1),
        [cStoreObjectState]          int             NOT NULL,
        [ustamp]                     datetime        NOT NULL,
        [datestamp]                  datetime        NOT NULL,
        [json_object]                nvarchar(max)   NULL,
        CONSTRAINT [PK_StoreObject] PRIMARY KEY ([cStoreObjectId]),
        CONSTRAINT [UQ_StoreObjectKey] UNIQUE ([cStoreBucketId], [cStoreObjectKeyHash]),
        CONSTRAINT [FK_StoreObjectBucket] FOREIGN KEY ([cStoreBucketId]) REFERENCES [dbo].[ta_StoreBucket] ([cStoreBucketId]),
        CONSTRAINT [CK_StoreObjectVersionNext] CHECK ([cStoreObjectVersionNext] >= 1)
    );
    CREATE INDEX [IX_StoreObjectKeyPrefix] ON [dbo].[ta_StoreObject] ([cStoreBucketId], [cStoreObjectKeyPrefix])
        INCLUDE ([cStoreObjectVersionCurrent]);
END;

IF OBJECT_ID(N'[dbo].[ta_StoreObjectBlob]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StoreObjectBlob]
    (
        [cStoreObjectBlobHash]     binary(32) NOT NULL,
        [cStoreObjectBlobCrc32]    binary(4)  NOT NULL,
        [cStoreObjectBlobSize]     bigint     NOT NULL,
        [cStoreObjectBlobState]    int        NOT NULL,
        [cStoreObjectBlobOrphanAt] datetime   NULL,
        [datestamp]                datetime   NOT NULL,
        CONSTRAINT [PK_StoreObjectBlob] PRIMARY KEY ([cStoreObjectBlobHash]),
        CONSTRAINT [CK_StoreObjectBlobState] CHECK ([cStoreObjectBlobState] IN (1, 2)),
        CONSTRAINT [CK_StoreObjectBlobSize] CHECK ([cStoreObjectBlobSize] >= 0)
    );
    CREATE INDEX [IX_StoreObjectBlobState] ON [dbo].[ta_StoreObjectBlob] ([cStoreObjectBlobState], [cStoreObjectBlobOrphanAt]);
END;

IF OBJECT_ID(N'[dbo].[ta_StoreObjectVersion]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StoreObjectVersion]
    (
        [cStoreObjectId]            char(26)       NOT NULL,
        [cStoreObjectVersionId]     int            NOT NULL,
        [cStoreObjectBlobHash]      binary(32)     NULL,
        [cStoreObjectVersionSize]   bigint         NOT NULL,
        [cStoreObjectVersionMime]   varchar(255)   NOT NULL,
        [cStoreObjectVersionMarker] bit            NOT NULL,
        [datestamp]                 datetime       NOT NULL,
        [json_object]               nvarchar(max)  NULL,
        CONSTRAINT [PK_StoreObjectVersion] PRIMARY KEY ([cStoreObjectId], [cStoreObjectVersionId]),
        CONSTRAINT [FK_StoreObjectVersionObject] FOREIGN KEY ([cStoreObjectId]) REFERENCES [dbo].[ta_StoreObject] ([cStoreObjectId]),
        CONSTRAINT [FK_StoreObjectVersionBlob] FOREIGN KEY ([cStoreObjectBlobHash]) REFERENCES [dbo].[ta_StoreObjectBlob] ([cStoreObjectBlobHash]),
        -- A delete marker has no blob and no size; any other version has a blob.
        CONSTRAINT [CK_StoreObjectVersionMarker] CHECK (
            ([cStoreObjectVersionMarker] = 1 AND [cStoreObjectBlobHash] IS NULL AND [cStoreObjectVersionSize] = 0)
         OR ([cStoreObjectVersionMarker] = 0 AND [cStoreObjectBlobHash] IS NOT NULL AND [cStoreObjectVersionSize] >= 0))
    );
    CREATE INDEX [IX_StoreObjectVersionBlob] ON [dbo].[ta_StoreObjectVersion] ([cStoreObjectBlobHash]);
END;

IF OBJECT_ID(N'[dbo].[ta_StoreObjectBlobVol]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ta_StoreObjectBlobVol]
    (
        [cStoreObjectBlobHash]          binary(32) NOT NULL,
        [cStoreVolumeId]                char(26)   NOT NULL,
        [cStoreObjectBlobVolState]      int        NOT NULL,
        [cStoreObjectBlobVolVerifiedAt] datetime   NULL,
        [datestamp]                     datetime   NOT NULL,
        CONSTRAINT [PK_StoreObjectBlobVol] PRIMARY KEY ([cStoreObjectBlobHash], [cStoreVolumeId]),
        CONSTRAINT [FK_StoreObjectBlobVolBlob] FOREIGN KEY ([cStoreObjectBlobHash]) REFERENCES [dbo].[ta_StoreObjectBlob] ([cStoreObjectBlobHash]),
        CONSTRAINT [FK_StoreObjectBlobVolVolume] FOREIGN KEY ([cStoreVolumeId]) REFERENCES [dbo].[ta_StoreVolume] ([cStoreVolumeId]),
        CONSTRAINT [CK_StoreObjectBlobVolState] CHECK ([cStoreObjectBlobVolState] IN (1, 2, 3, 4))
    );
    CREATE INDEX [IX_StoreObjectBlobVolVolume] ON [dbo].[ta_StoreObjectBlobVol] ([cStoreVolumeId], [cStoreObjectBlobVolState]);
END;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ta_Meta] WHERE [cMetaKey] = 'ObjectStoreSchemaVersion')
    INSERT INTO [dbo].[ta_Meta] ([cMetaKey], [cMetaValue], [cMetaDescription], [ustamp])
    VALUES ('ObjectStoreSchemaVersion', '1', 'Object storage schema version', GETUTCDATE());

COMMIT TRANSACTION;
GO
