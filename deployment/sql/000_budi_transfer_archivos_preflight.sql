SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() <> N'Budi_Dev'
    THROW 51000, 'Ejecute el preflight únicamente en la base Budi_Dev.', 1;

IF OBJECT_ID(N'dbo.Transfer_ArchivosLocales', N'U') IS NULL
    THROW 51001, 'No existe dbo.Transfer_ArchivosLocales.', 1;

DECLARE @CanAlter int = HAS_PERMS_BY_NAME(N'dbo.Transfer_ArchivosLocales', N'OBJECT', N'ALTER');
IF ISNULL(@CanAlter, 0) <> 1
    THROW 51002, 'La cuenta no tiene permiso ALTER sobre dbo.Transfer_ArchivosLocales.', 1;

IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ResultadoId') IS NULL
   OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ColaboraId') IS NULL
   OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'Estatus') IS NULL
   OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'RFC_Origen') IS NULL
   OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'Ruta_ARchivo_Fisico') IS NULL
    THROW 51003, 'Faltan columnas base requeridas; no ejecute la migración.', 1;

IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'MovimientoId') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
       WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
         AND c.name = N'MovimientoId' AND t.name = N'int'
   )
    THROW 51005, 'MovimientoId existe con un tipo incompatible.', 1;

IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ID_Upload') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
       WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
         AND c.name = N'ID_Upload' AND t.name = N'uniqueidentifier'
   )
    THROW 51006, 'ID_Upload existe con un tipo incompatible.', 1;

DECLARE @DuplicateUploadIds bigint = 0;
IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ID_Upload') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql
        N'SELECT @Duplicates = COUNT_BIG(*)
          FROM (
              SELECT ID_Upload
              FROM dbo.Transfer_ArchivosLocales
              WHERE ID_Upload IS NOT NULL
              GROUP BY ID_Upload
              HAVING COUNT_BIG(*) > 1
          ) duplicate_groups;',
        N'@Duplicates bigint OUTPUT',
        @Duplicates = @DuplicateUploadIds OUTPUT;
END;

IF @DuplicateUploadIds > 0
    THROW 51004, 'Existen ID_Upload duplicados; corrija los datos antes de migrar.', 1;

SELECT
    DB_NAME() AS DatabaseName,
    COUNT_BIG(*) AS TotalRows,
    SUM(CASE WHEN Estatus = 'A' THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END) AS ActiveRows,
    CASE WHEN COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'MovimientoId') IS NULL THEN 0 ELSE 1 END AS HasMovimientoId,
    CASE WHEN COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ID_Upload') IS NULL THEN 0 ELSE 1 END AS HasUploadId,
    @DuplicateUploadIds AS DuplicateUploadIdGroups,
    CASE WHEN EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND name = N'UX_Transfer_ArchivosLocales_ID_Upload'
    ) THEN 1 ELSE 0 END AS HasUploadIndex,
    CASE WHEN EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND name = N'IX_Transfer_ArchivosLocales_ResultadoContexto'
    ) THEN 1 ELSE 0 END AS HasResultContextIndex
FROM dbo.Transfer_ArchivosLocales;

SELECT
    CAST(DATABASEPROPERTYEX(DB_NAME(), N'Status') AS nvarchar(128)) AS DatabaseStatus,
    CAST(DATABASEPROPERTYEX(DB_NAME(), N'Recovery') AS nvarchar(128)) AS RecoveryModel,
    CAST(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'BACKUP DATABASE') AS int) AS CanBackup,
    CAST(SUM(size) * 8.0 / 1024 AS decimal(18, 2)) AS DatabaseSizeMb
FROM sys.database_files;

SELECT TOP (1)
    backup_finish_date AS LastFullBackup,
    CAST(backup_size / 1048576.0 AS decimal(18, 2)) AS BackupSizeMb
FROM msdb.dbo.backupset
WHERE database_name = DB_NAME()
  AND type = 'D'
ORDER BY backup_finish_date DESC;
