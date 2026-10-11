/*
    vi_StoreVolume - view, one object per file.

    Volume with the number and physical bytes of the blob copies it holds, per copy state.
    Run after tables/060-objectstore.sql (ta_StoreVolume, ta_StoreObjectBlobVol, ta_StoreObjectBlob).
    Safe to run again (DROP IF EXISTS, then CREATE). The query below the last GO
    is an execution test and may run along.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DROP VIEW IF EXISTS [dbo].[vi_StoreVolume];
GO

CREATE VIEW [dbo].[vi_StoreVolume] AS
SELECT v.[cStoreVolumeId], v.[cStoreVolumeName], v.[cStoreVolumeState], v.[cStoreVolumeType],
       v.[cStoreVolumePath], v.[cStoreVolumeNote], v.[ustamp], v.[datestamp], v.[json_object],
       ISNULL(c.[cvStoreVolumeBlobs], 0)   AS [cvStoreVolumeBlobs],
       ISNULL(c.[cvStoreVolumeBytes], 0)   AS [cvStoreVolumeBytes],
       ISNULL(c.[cvStoreVolumeMissing], 0) AS [cvStoreVolumeMissing]
FROM [dbo].[ta_StoreVolume] v
LEFT JOIN (
    SELECT bv.[cStoreVolumeId],
           SUM(CASE WHEN bv.[cStoreObjectBlobVolState] = 2 THEN 1 ELSE 0 END) AS [cvStoreVolumeBlobs],
           SUM(CASE WHEN bv.[cStoreObjectBlobVolState] = 2 THEN b.[cStoreObjectBlobSize] ELSE 0 END) AS [cvStoreVolumeBytes],
           SUM(CASE WHEN bv.[cStoreObjectBlobVolState] = 3 THEN 1 ELSE 0 END) AS [cvStoreVolumeMissing]
    FROM [dbo].[ta_StoreObjectBlobVol] bv
    JOIN [dbo].[ta_StoreObjectBlob] b ON b.[cStoreObjectBlobHash] = bv.[cStoreObjectBlobHash]
    GROUP BY bv.[cStoreVolumeId]
) c ON c.[cStoreVolumeId] = v.[cStoreVolumeId];
GO

-- Execution test
SELECT TOP 100 * FROM [dbo].[vi_StoreVolume] ORDER BY [cStoreVolumeName];
GO
