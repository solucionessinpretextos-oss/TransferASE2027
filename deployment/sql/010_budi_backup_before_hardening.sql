:on error exit

-- Requiere una ruta nueva suministrada por SQLCMD:
--   -v BackupFile="D:\Backups\BUZONDIGITAL_YYYYMMDD_HHMM.bak"

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'BUZONDIGITAL'
    THROW 51010, 'Ejecute el respaldo únicamente en la base BUZONDIGITAL.', 1;

IF ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'BACKUP DATABASE'), 0) <> 1
    THROW 51011, 'La cuenta no tiene permiso BACKUP DATABASE.', 1;

BACKUP DATABASE [BUZONDIGITAL]
TO DISK = N'$(BackupFile)'
WITH COPY_ONLY, NOINIT, COMPRESSION, CHECKSUM,
     NAME = N'BUZONDIGITAL before FileXfer hardening', STATS = 10;

RESTORE VERIFYONLY
FROM DISK = N'$(BackupFile)'
WITH CHECKSUM;

SELECT
    N'backup_verified' AS Status,
    N'$(BackupFile)' AS BackupFile,
    SYSDATETIMEOFFSET() AS VerifiedAt;
