/*
    vi_StoreBucket - view, one object per file.

    Bucket with its policy rules and quota counters. cvStoreBucketOwner and cvStoreBucketDescription come
    from json_object: the application that manages the bucket (NULL for an ordinary bucket) and the
    description it declared.
    Run after tables/060-objectstore.sql (ta_StoreBucket, ta_StorePolicy).
    Safe to run again (DROP IF EXISTS, then CREATE). The query below the last GO
    is an execution test and may run along.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DROP VIEW IF EXISTS [dbo].[vi_StoreBucket];
GO

CREATE VIEW [dbo].[vi_StoreBucket] AS
SELECT b.[cStoreBucketId], b.[cStoreBucketName], b.[cStorePolicyId], b.[cStoreBucketState], b.[cStoreBucketNote],
       b.[cStoreBucketBytes], b.[cStoreBucketObjects], b.[ustamp], b.[datestamp], b.[json_object],
       CASE WHEN ISJSON(b.[json_object]) = 1 THEN JSON_VALUE(b.[json_object], '$.owner') END       AS [cvStoreBucketOwner],
       CASE WHEN ISJSON(b.[json_object]) = 1 THEN JSON_VALUE(b.[json_object], '$.description') END AS [cvStoreBucketDescription],
       p.[cStorePolicyName], p.[cStorePolicyAccess], p.[cStorePolicyReplicaMin], p.[cStorePolicyReplicaTarget],
       p.[cStorePolicyKeepVersions], p.[cStorePolicyKeepDays], p.[cStorePolicyQuotaBytes],
       p.[cStorePolicyQuotaObjects], p.[cStorePolicyMaxObjectBytes]
FROM [dbo].[ta_StoreBucket] b
JOIN [dbo].[ta_StorePolicy] p ON p.[cStorePolicyId] = b.[cStorePolicyId];
GO

-- Execution test
SELECT TOP 100 * FROM [dbo].[vi_StoreBucket] ORDER BY [cStoreBucketName];
GO
