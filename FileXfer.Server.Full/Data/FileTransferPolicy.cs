using System;
using System.ComponentModel.DataAnnotations;

namespace FileXfer.Server.Data;

public sealed class FileTypePolicy
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(24)]
    public string Extension { get; set; } = string.Empty;

    public bool IsAllowed { get; set; }

    public bool RequiresVerification { get; set; } = true;

    public long? MaxFileBytes { get; set; }

    public int Version { get; set; } = 1;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [Required, MaxLength(64)]
    public string UpdatedBy { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string ChangeReason { get; set; } = string.Empty;
}

public sealed class FileTypePolicyRevision
{
    [Key]
    public Guid Id { get; set; }

    public Guid PolicyId { get; set; }

    [Required, MaxLength(24)]
    public string Extension { get; set; } = string.Empty;

    public bool IsAllowed { get; set; }

    public bool RequiresVerification { get; set; }

    public long? MaxFileBytes { get; set; }

    public int Version { get; set; }

    public DateTimeOffset ChangedUtc { get; set; } = DateTimeOffset.UtcNow;

    [Required, MaxLength(64)]
    public string ChangedBy { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string ChangeReason { get; set; } = string.Empty;
}

public sealed class UploadReceipt
{
    [Key]
    public Guid Id { get; set; }

    public Guid UploadId { get; set; }

    public DateTimeOffset IssuedUtc { get; set; }

    [Required, MaxLength(8192)]
    public string CanonicalPayloadJson { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string PayloadSha256 { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? PreviousChainHash { get; set; }

    [Required, MaxLength(64)]
    public string ChainHash { get; set; } = string.Empty;

    [Required, MaxLength(1024)]
    public string SignatureBase64 { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string SignatureAlgorithm { get; set; } = "RSA-PSS-SHA256";

    [Required, MaxLength(128)]
    public string KeyId { get; set; } = string.Empty;

    [Required, MaxLength(2048)]
    public string PublicKeyPem { get; set; } = string.Empty;
}

public sealed class ReceiptChainState
{
    [Key]
    public int Id { get; set; } = 1;

    [MaxLength(64)]
    public string? HeadHash { get; set; }

    public int Version { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
