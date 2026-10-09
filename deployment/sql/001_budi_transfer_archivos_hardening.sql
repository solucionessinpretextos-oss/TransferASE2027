SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Transfer_ArchivosLocales', N'U') IS NULL
    THROW 51000, 'No existe dbo.Transfer_ArchivosLocales en la base BuDi.', 1;

IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'MovimientoId') IS NULL
    ALTER TABLE dbo.Transfer_ArchivosLocales ADD MovimientoId int NULL;

IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ID_Upload') IS NULL
    ALTER TABLE dbo.Transfer_ArchivosLocales ADD ID_Upload uniqueidentifier NULL;

IF EXISTS (
    SELECT ID_Upload
    FROM dbo.Transfer_ArchivosLocales
    WHERE ID_Upload IS NOT NULL
    GROUP BY ID_Upload
    HAVING COUNT_BIG(*) > 1
)
    THROW 51001, 'Hay ID_Upload duplicados. Corrija los datos antes de crear el índice único.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
      AND name = N'UX_Transfer_ArchivosLocales_ID_Upload'
)
BEGIN
    CREATE UNIQUE INDEX UX_Transfer_ArchivosLocales_ID_Upload
        ON dbo.Transfer_ArchivosLocales(ID_Upload)
        WHERE ID_Upload IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
      AND name = N'IX_Transfer_ArchivosLocales_ResultadoContexto'
)
BEGIN
    CREATE INDEX IX_Transfer_ArchivosLocales_ResultadoContexto
        ON dbo.Transfer_ArchivosLocales(ResultadoId, ColaboraId, MovimientoId, Estatus)
        INCLUDE (RFC_Origen, Ruta_ARchivo_Fisico);
END;

COMMIT TRANSACTION;
