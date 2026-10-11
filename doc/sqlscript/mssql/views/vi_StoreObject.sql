/*
    vi_StoreObject - view, one object per file.

    Object with its current version (size, MIME type, blob, delete marker flag).
    Run after tables/060-objectstore.sql (ta_StoreObject, ta_StoreObjectVersion).
    Safe to run again (DROP IF EXISTS, then CREATE). The query below the last GO
    is an execution test and may run along.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DROP VIEW IF EXISTS [dbo].[vi_StoreObject];
GO

CREATE VIEW [dbo].[vi_StoreObject] AS
SELECT o.[cStoreObjectId], o.[cStoreBucketId], o.[cStoreObjectKey], o.[cStoreObjectKeyHash],
       o.[cStoreObjectVersionCurrent], o.[cStoreObjectVersionNext], o.[cStoreObjectState],
       o.[ustamp], o.[datestamp], o.[json_object],
       v.[cStoreObjectBlobHash], v.[cStoreObjectVersionSize], v.[cStoreObjectVersionMime],
       v.[cStoreObjectVersionMarker], v.[datestamp] AS [cvStoreObjectVersionDate]
FROM [dbo].[ta_StoreObject] o
LEFT JOIN [dbo].[ta_StoreObjectVersion] v
       ON v.[cStoreObjectId] = o.[cStoreObjectId] AND v.[cStoreObjectVersionId] = o.[cStoreObjectVersionCurrent];
GO

-- Execution test
SELECT TOP 100 * FROM [dbo].[vi_StoreObject] ORDER BY [cStoreBucketId], [cStoreObjectKey];
GO
