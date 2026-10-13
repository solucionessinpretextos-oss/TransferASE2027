namespace FileXfer.Server.Options;

public sealed class ReceiptSigningOptions
{
    public string PrivateKeyPem { get; set; } = string.Empty;
    public string PrivateKeyPemPath { get; set; } = string.Empty;
    public string PrivateKeyPassword { get; set; } = string.Empty;
    public bool AllowEphemeralDevelopmentKey { get; set; } = true;
}
