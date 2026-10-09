IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] uniqueidentifier NOT NULL,
        [TimestampUtc] datetimeoffset NOT NULL,
        [ActorRfc] nvarchar(13) NULL,
        [Action] nvarchar(128) NULL,
        [Ip] nvarchar(64) NULL,
        [Details] nvarchar(2048) NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [Devices] (
        [Id] uniqueidentifier NOT NULL,
        [DeviceId] nvarchar(64) NOT NULL,
        [Rfc] nvarchar(13) NOT NULL,
        [PublicKeyPem] nvarchar(max) NOT NULL,
        [FriendlyName] nvarchar(256) NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        [Revoked] bit NOT NULL,
        [RevokedUtc] datetimeoffset NULL,
        CONSTRAINT [PK_Devices] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [Lockouts] (
        [Id] uniqueidentifier NOT NULL,
        [Rfc] nvarchar(450) NOT NULL,
        [LockoutUntilUtc] datetimeoffset NULL,
        [FailCount] int NOT NULL,
        [UpdatedUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Lockouts] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [MfaSecrets] (
        [Id] uniqueidentifier NOT NULL,
        [Rfc] nvarchar(13) NOT NULL,
        [SecretBase32] nvarchar(256) NOT NULL,
        [Active] bit NOT NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_MfaSecrets] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [Nonces] (
        [Id] uniqueidentifier NOT NULL,
        [ValueBase64] nvarchar(256) NOT NULL,
        [ExpiresUtc] datetimeoffset NOT NULL,
        [DeviceId] nvarchar(64) NULL,
        [Rfc] nvarchar(13) NULL,
        CONSTRAINT [PK_Nonces] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [RfcProvisioningRequests] (
        [Id] uniqueidentifier NOT NULL,
        [Rfc] nvarchar(13) NOT NULL,
        [DisplayName] nvarchar(256) NULL,
        [ContactEmail] nvarchar(256) NULL,
        [Reason] nvarchar(1024) NULL,
        [Status] nvarchar(32) NOT NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(64) NULL,
        [ReviewedUtc] datetimeoffset NULL,
        [ReviewedBy] nvarchar(64) NULL,
        [ReviewNotes] nvarchar(1024) NULL,
        CONSTRAINT [PK_RfcProvisioningRequests] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [Uploads] (
        [Id] uniqueidentifier NOT NULL,
        [Rfc] nvarchar(450) NOT NULL,
        [FileUuid] nvarchar(450) NOT NULL,
        [OriginalFileName] nvarchar(450) NULL,
        [OriginalSourcePath] nvarchar(450) NULL,
        [StoredRelativePath] nvarchar(450) NOT NULL,
        [BytesReceived] bigint NOT NULL,
        [Sha256Server] nvarchar(max) NULL,
        [Sha256Client] nvarchar(max) NULL,
        [Status] nvarchar(max) NOT NULL,
        [LastError] nvarchar(max) NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        [CompletedUtc] datetimeoffset NULL,
        [DeviceId] nvarchar(max) NULL,
        CONSTRAINT [PK_Uploads] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] uniqueidentifier NOT NULL,
        [Rfc] nvarchar(13) NOT NULL,
        [DisplayName] nvarchar(256) NULL,
        [Roles] nvarchar(max) NOT NULL,
        [MfaRequired] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [Notes] nvarchar(1024) NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(64) NULL,
        [LastModifiedUtc] datetimeoffset NULL,
        [LastModifiedBy] nvarchar(64) NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_TimestampUtc] ON [AuditLogs] ([TimestampUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Lockouts_Rfc] ON [Lockouts] ([Rfc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_MfaSecrets_Rfc_Active] ON [MfaSecrets] ([Rfc], [Active]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_Nonces_ValueBase64] ON [Nonces] ([ValueBase64]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_RfcProvisioningRequests_Rfc] ON [RfcProvisioningRequests] ([Rfc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_RfcProvisioningRequests_Status_CreatedUtc] ON [RfcProvisioningRequests] ([Status], [CreatedUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Uploads_FileUuid] ON [Uploads] ([FileUuid]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_Uploads_Rfc_OriginalFileName] ON [Uploads] ([Rfc], [OriginalFileName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_Uploads_Rfc_OriginalSourcePath] ON [Uploads] ([Rfc], [OriginalSourcePath]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE INDEX [IX_Uploads_StoredRelativePath] ON [Uploads] ([StoredRelativePath]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Rfc] ON [Users] ([Rfc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20251105180306_Initial'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20251105180306_Initial', N'8.0.8');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    IF EXISTS (
        SELECT 1
        FROM [Uploads]
        WHERE LEN([Status]) > 32
           OR LEN([Sha256Server]) > 64
           OR LEN([Sha256Client]) > 64
           OR LEN([Rfc]) > 13
           OR LEN([OriginalFileName]) > 255
           OR LEN([LastError]) > 1024
           OR LEN([FileUuid]) > 32
           OR LEN([DeviceId]) > 64)
    BEGIN
        THROW 51001, 'SecurityHardening2027: existen filas de Uploads que exceden los nuevos límites. Corrija los datos y reintente; no se truncó información.', 1;
    END;

    IF EXISTS (
        SELECT [Rfc], [DeviceId]
        FROM [Devices]
        GROUP BY [Rfc], [DeviceId]
        HAVING COUNT_BIG(*) > 1)
    BEGIN
        THROW 51002, 'SecurityHardening2027: existen dispositivos duplicados por RFC/DeviceId. Resuélvalos antes de crear el índice único.', 1;
    END;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DROP INDEX [IX_Uploads_Rfc_OriginalSourcePath] ON [Uploads];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'Status');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var0 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [Status] nvarchar(32) NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DECLARE @var1 sysname;
    SELECT @var1 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'Sha256Server');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var1 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [Sha256Server] nvarchar(64) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DECLARE @var2 sysname;
    SELECT @var2 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'Sha256Client');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var2 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [Sha256Client] nvarchar(64) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DROP INDEX [IX_Uploads_Rfc_OriginalFileName] ON [Uploads];
    DECLARE @var3 sysname;
    SELECT @var3 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'Rfc');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var3 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [Rfc] nvarchar(13) NOT NULL;
    CREATE INDEX [IX_Uploads_Rfc_OriginalFileName] ON [Uploads] ([Rfc], [OriginalFileName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DROP INDEX [IX_Uploads_Rfc_OriginalFileName] ON [Uploads];
    DECLARE @var4 sysname;
    SELECT @var4 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'OriginalFileName');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var4 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [OriginalFileName] nvarchar(255) NULL;
    CREATE INDEX [IX_Uploads_Rfc_OriginalFileName] ON [Uploads] ([Rfc], [OriginalFileName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DECLARE @var5 sysname;
    SELECT @var5 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'LastError');
    IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var5 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [LastError] nvarchar(1024) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DROP INDEX [IX_Uploads_FileUuid] ON [Uploads];
    DECLARE @var6 sysname;
    SELECT @var6 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'FileUuid');
    IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var6 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [FileUuid] nvarchar(32) NOT NULL;
    CREATE UNIQUE INDEX [IX_Uploads_FileUuid] ON [Uploads] ([FileUuid]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    DECLARE @var7 sysname;
    SELECT @var7 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Uploads]') AND [c].[name] = N'DeviceId');
    IF @var7 IS NOT NULL EXEC(N'ALTER TABLE [Uploads] DROP CONSTRAINT [' + @var7 + '];');
    ALTER TABLE [Uploads] ALTER COLUMN [DeviceId] nvarchar(64) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [IdActo] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [IdAuditoria] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [IdEnte] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [IdOficio] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [IdRequerimiento] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [IdResultado] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [Periodo] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    CREATE TABLE [RefreshTokens] (
        [Id] uniqueidentifier NOT NULL,
        [Rfc] nvarchar(13) NOT NULL,
        [TokenHash] nvarchar(256) NOT NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        [ExpiresUtc] datetimeoffset NOT NULL,
        [IsRevoked] bit NOT NULL,
        [DeviceId] nvarchar(64) NULL,
        [CreatedFromIp] nvarchar(64) NULL,
        CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    CREATE INDEX [IX_Nonces_ExpiresUtc] ON [Nonces] ([ExpiresUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Devices_Rfc_DeviceId] ON [Devices] ([Rfc], [DeviceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshTokens_TokenHash] ON [RefreshTokens] ([TokenHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008224342_SecurityHardening2027'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008224342_SecurityHardening2027', N'8.0.8');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    ALTER TABLE [Users] ADD [SecurityVersion] int NOT NULL DEFAULT 1;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    ALTER TABLE [RefreshTokens] ADD [AbsoluteExpiresUtc] datetimeoffset NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    ALTER TABLE [RefreshTokens] ADD [FamilyId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    ALTER TABLE [RefreshTokens] ADD [MfaVerified] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    ALTER TABLE [RefreshTokens] ADD [ParentTokenId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    ALTER TABLE [RefreshTokens] ADD [SecurityVersion] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_FamilyId_IsRevoked] ON [RefreshTokens] ([FamilyId], [IsRevoked]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN

                    UPDATE [Users]
                    SET [SecurityVersion] = 1
                    WHERE [SecurityVersion] < 1;

                    UPDATE [RefreshTokens]
                    SET [IsRevoked] = 1,
                        [AbsoluteExpiresUtc] = [ExpiresUtc],
                        [FamilyId] = NEWID(),
                        [SecurityVersion] = 0,
                        [MfaVerified] = 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009203637_SessionSecurity2027'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009203637_SessionSecurity2027', N'8.0.8');
END;
GO

COMMIT;
GO

