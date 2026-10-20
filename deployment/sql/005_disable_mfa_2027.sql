SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT 1
    FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261019090000_DisableMfa2027'
)
BEGIN
    UPDATE [Users]
    SET [MfaRequired] = 0,
        [SecurityVersion] = [SecurityVersion] + 1,
        [LastModifiedUtc] = SYSUTCDATETIME(),
        [LastModifiedBy] = N'migration'
    WHERE [MfaRequired] = 1;

    UPDATE [MfaSecrets]
    SET [Active] = 0
    WHERE [Active] = 1;

    UPDATE [RefreshTokens]
    SET [IsRevoked] = 1,
        [MfaVerified] = 0
    WHERE [IsRevoked] = 0 OR [MfaVerified] = 1;

    INSERT INTO [AuditLogs]
        ([Id], [TimestampUtc], [ActorRfc], [Action], [Ip], [Details])
    VALUES
        (NEWID(), SYSUTCDATETIME(), N'migration', N'authentication_mfa_disabled', NULL,
         N'MFA deshabilitado globalmente; enrolamientos desactivados y sesiones anteriores revocadas.');

    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261019090000_DisableMfa2027', N'8.0.32');
END;
GO

COMMIT;
GO
