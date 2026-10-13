BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [ExpectedLength] bigint NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [ExpectedSha256] nvarchar(64) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [LastActivityUtc] datetimeoffset NOT NULL DEFAULT (SYSDATETIMEOFFSET());
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [PolicyExtension] nvarchar(24) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [PolicyVersion] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [ReceiptId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [ResumeExpiresUtc] datetimeoffset NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [ResumedBytes] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [TraceId] nvarchar(64) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [VerificationOutcome] nvarchar(32) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [VerificationPerformed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD [VerificationRequired] bit NOT NULL DEFAULT CAST(1 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE TABLE [FileTypePolicies] (
        [Id] uniqueidentifier NOT NULL,
        [Extension] nvarchar(24) NOT NULL,
        [IsAllowed] bit NOT NULL,
        [RequiresVerification] bit NOT NULL,
        [MaxFileBytes] bigint NULL,
        [Version] int NOT NULL,
        [UpdatedUtc] datetimeoffset NOT NULL,
        [UpdatedBy] nvarchar(64) NOT NULL,
        [ChangeReason] nvarchar(512) NOT NULL,
        CONSTRAINT [PK_FileTypePolicies] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE TABLE [ReceiptChainStates] (
        [Id] int NOT NULL,
        [HeadHash] nvarchar(64) NULL,
        [Version] int NOT NULL,
        [UpdatedUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ReceiptChainStates] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE TABLE [UploadReceipts] (
        [Id] uniqueidentifier NOT NULL,
        [UploadId] uniqueidentifier NOT NULL,
        [IssuedUtc] datetimeoffset NOT NULL,
        [CanonicalPayloadJson] nvarchar(max) NOT NULL,
        [PayloadSha256] nvarchar(64) NOT NULL,
        [PreviousChainHash] nvarchar(64) NULL,
        [ChainHash] nvarchar(64) NOT NULL,
        [SignatureBase64] nvarchar(1024) NOT NULL,
        [SignatureAlgorithm] nvarchar(32) NOT NULL,
        [KeyId] nvarchar(128) NOT NULL,
        [PublicKeyPem] nvarchar(2048) NOT NULL,
        CONSTRAINT [PK_UploadReceipts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UploadReceipts_Uploads_UploadId] FOREIGN KEY ([UploadId]) REFERENCES [Uploads] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE TABLE [FileTypePolicyRevisions] (
        [Id] uniqueidentifier NOT NULL,
        [PolicyId] uniqueidentifier NOT NULL,
        [Extension] nvarchar(24) NOT NULL,
        [IsAllowed] bit NOT NULL,
        [RequiresVerification] bit NOT NULL,
        [MaxFileBytes] bigint NULL,
        [Version] int NOT NULL,
        [ChangedUtc] datetimeoffset NOT NULL,
        [ChangedBy] nvarchar(64) NOT NULL,
        [ChangeReason] nvarchar(512) NOT NULL,
        CONSTRAINT [PK_FileTypePolicyRevisions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FileTypePolicyRevisions_FileTypePolicies_PolicyId] FOREIGN KEY ([PolicyId]) REFERENCES [FileTypePolicies] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Uploads_ReceiptId] ON [Uploads] ([ReceiptId]) WHERE [ReceiptId] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE INDEX [IX_Uploads_Status_ResumeExpiresUtc] ON [Uploads] ([Status], [ResumeExpiresUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FileTypePolicies_Extension] ON [FileTypePolicies] ([Extension]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE INDEX [IX_FileTypePolicyRevisions_Extension_ChangedUtc] ON [FileTypePolicyRevisions] ([Extension], [ChangedUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FileTypePolicyRevisions_PolicyId_Version] ON [FileTypePolicyRevisions] ([PolicyId], [Version]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE INDEX [IX_UploadReceipts_IssuedUtc] ON [UploadReceipts] ([IssuedUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UploadReceipts_UploadId] ON [UploadReceipts] ([UploadId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    ALTER TABLE [Uploads] ADD CONSTRAINT [FK_Uploads_UploadReceipts_ReceiptId] FOREIGN KEY ([ReceiptId]) REFERENCES [UploadReceipts] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261013164437_TransferCapabilities2027'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261013164437_TransferCapabilities2027', N'8.0.8');
END;
GO

COMMIT;
GO

