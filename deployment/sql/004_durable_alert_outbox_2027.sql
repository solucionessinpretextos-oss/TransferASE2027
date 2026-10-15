BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015202505_DurableAlertOutbox2027'
)
BEGIN
    CREATE TABLE [AlertOutboxDeliveries] (
        [Id] uniqueidentifier NOT NULL,
        [EventId] uniqueidentifier NOT NULL,
        [Transport] nvarchar(32) NOT NULL,
        [DeduplicationKey] nvarchar(64) NOT NULL,
        [DeduplicationBucket] bigint NOT NULL,
        [Title] nvarchar(200) NOT NULL,
        [Message] nvarchar(max) NOT NULL,
        [OccurredUtc] datetimeoffset NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [AttemptCount] int NOT NULL,
        [NextAttemptUtc] datetimeoffset NOT NULL,
        [LeaseId] uniqueidentifier NULL,
        [LeaseExpiresUtc] datetimeoffset NULL,
        [LastFailureCode] nvarchar(128) NULL,
        [DeliveredUtc] datetimeoffset NULL,
        [CreatedUtc] datetimeoffset NOT NULL,
        [UpdatedUtc] datetimeoffset NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_AlertOutboxDeliveries] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AlertOutboxDeliveries_AttemptCount] CHECK ([AttemptCount] >= 0),
        CONSTRAINT [CK_AlertOutboxDeliveries_DeliveredUtc] CHECK ([Status] <> N'Delivered' OR [DeliveredUtc] IS NOT NULL),
        CONSTRAINT [CK_AlertOutboxDeliveries_Lease] CHECK (([Status] = N'Processing' AND [LeaseId] IS NOT NULL AND [LeaseExpiresUtc] IS NOT NULL) OR ([Status] <> N'Processing' AND [LeaseId] IS NULL AND [LeaseExpiresUtc] IS NULL)),
        CONSTRAINT [CK_AlertOutboxDeliveries_Status] CHECK ([Status] IN (N'Pending', N'Processing', N'Delivered', N'DeadLetter'))
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015202505_DurableAlertOutbox2027'
)
BEGIN
    CREATE INDEX [IX_AlertOutboxDeliveries_EventId] ON [AlertOutboxDeliveries] ([EventId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015202505_DurableAlertOutbox2027'
)
BEGIN
    CREATE INDEX [IX_AlertOutboxDeliveries_Status_NextAttemptUtc_LeaseExpiresUtc] ON [AlertOutboxDeliveries] ([Status], [NextAttemptUtc], [LeaseExpiresUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015202505_DurableAlertOutbox2027'
)
BEGIN
    CREATE INDEX [IX_AlertOutboxDeliveries_Status_UpdatedUtc] ON [AlertOutboxDeliveries] ([Status], [UpdatedUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015202505_DurableAlertOutbox2027'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AlertOutboxDeliveries_Transport_DeduplicationKey_DeduplicationBucket] ON [AlertOutboxDeliveries] ([Transport], [DeduplicationKey], [DeduplicationBucket]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261015202505_DurableAlertOutbox2027'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261015202505_DurableAlertOutbox2027', N'8.0.32');
END;
GO

COMMIT;
GO

