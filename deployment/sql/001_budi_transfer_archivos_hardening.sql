SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() <> N'BUZONDIGITAL'
    THROW 51000, 'Ejecute la migración únicamente en la base BUZONDIGITAL.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @ApplicationLockResult int;
    EXEC @ApplicationLockResult = sys.sp_getapplock
        @Resource = N'FileXfer:BUZONDIGITAL:001_budi_transfer_archivos_hardening',
        @LockMode = N'Exclusive',
        @LockOwner = N'Transaction',
        @LockTimeout = 0;

    IF @ApplicationLockResult < 0
        THROW 51001, 'Otra ejecución de la migración está activa.', 1;

    IF OBJECT_ID(N'dbo.Transfer_ArchivosLocales', N'U') IS NULL
        THROW 51002, 'No existe dbo.Transfer_ArchivosLocales en la base BuDi.', 1;

    IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ResultadoId') IS NULL
       OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ColaboraId') IS NULL
       OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'Estatus') IS NULL
       OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'RFC_Origen') IS NULL
       OR COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'Ruta_ARchivo_Fisico') IS NULL
        THROW 51003, 'Faltan columnas base requeridas; se canceló la migración.', 1;

    IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'MovimientoId') IS NULL
        ALTER TABLE dbo.Transfer_ArchivosLocales ADD MovimientoId int NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND c.name = N'MovimientoId' AND t.name = N'int'
    )
        THROW 51008, 'MovimientoId existe con un tipo incompatible.', 1;

    IF COL_LENGTH(N'dbo.Transfer_ArchivosLocales', N'ID_Upload') IS NULL
        ALTER TABLE dbo.Transfer_ArchivosLocales ADD ID_Upload uniqueidentifier NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND c.name = N'ID_Upload' AND t.name = N'uniqueidentifier'
    )
        THROW 51009, 'ID_Upload existe con un tipo incompatible.', 1;

    DECLARE @DuplicateUploadIdGroups bigint = 0;
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
        @Duplicates = @DuplicateUploadIdGroups OUTPUT;

    IF @DuplicateUploadIdGroups > 0
        THROW 51004, 'Hay ID_Upload duplicados. Corrija los datos antes de crear el índice único.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND name = N'UX_Transfer_ArchivosLocales_ID_Upload'
          AND (
              is_unique = 0
              OR has_filter = 0
              OR is_disabled = 1
              OR REPLACE(REPLACE(REPLACE(UPPER(filter_definition), N'[', N''), N']', N''), N' ', N'')
                 NOT IN (N'(ID_UPLOADISNOTNULL)', N'ID_UPLOADISNOTNULL')
              OR 1 <> (
                  SELECT COUNT(*)
                  FROM sys.index_columns ic
                  WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                    AND ic.index_id = sys.indexes.index_id
                    AND ic.key_ordinal > 0
              )
              OR NOT EXISTS (
                  SELECT 1
                  FROM sys.index_columns ic
                  JOIN sys.columns c
                    ON c.object_id = ic.object_id
                   AND c.column_id = ic.column_id
                  WHERE ic.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
                    AND ic.index_id = sys.indexes.index_id
                    AND ic.key_ordinal = 1
                    AND c.name = N'ID_Upload'
              )
          )
    )
        THROW 51005, 'El índice UX_Transfer_ArchivosLocales_ID_Upload existe con una definición incompatible.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND name = N'UX_Transfer_ArchivosLocales_ID_Upload'
    )
    BEGIN
        EXEC sys.sp_executesql N'
            CREATE UNIQUE INDEX UX_Transfer_ArchivosLocales_ID_Upload
                ON dbo.Transfer_ArchivosLocales(ID_Upload)
                WHERE ID_Upload IS NOT NULL;';
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

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND i.name = N'UX_Transfer_ArchivosLocales_ID_Upload'
          AND i.is_unique = 1
          AND i.has_filter = 1
          AND i.is_disabled = 0
          AND REPLACE(REPLACE(REPLACE(UPPER(i.filter_definition), N'[', N''), N']', N''), N' ', N'')
              IN (N'(ID_UPLOADISNOTNULL)', N'ID_UPLOADISNOTNULL')
          AND 1 = (
              SELECT COUNT(*)
              FROM sys.index_columns ic
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
          )
          AND EXISTS (
              SELECT 1
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal = 1
                AND c.name = N'ID_Upload'
          )
    )
        THROW 51006, 'No se pudo garantizar el índice único filtrado de ID_Upload.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales')
          AND i.name = N'IX_Transfer_ArchivosLocales_ResultadoContexto'
          AND i.is_disabled = 0
          AND 4 = (
              SELECT COUNT(*)
              FROM sys.index_columns ic
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
          )
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'ResultadoId'
          )
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'ColaboraId'
          )
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = N'MovimientoId'
          )
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 4 AND c.name = N'Estatus'
          )
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 AND c.name = N'RFC_Origen'
          )
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 AND c.name = N'Ruta_ARchivo_Fisico'
          )
    )
        THROW 51007, 'El índice de contexto de resultados no cumple el contrato requerido.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
