namespace FileXfer.Server.Dtos.Admin;

public sealed class FilePolicyDto
{
    public Guid Id { get; set; }
    public string Extension { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public bool RequiresVerification { get; set; }
    public long? MaxFileBytes { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public string ChangeReason { get; set; } = string.Empty;
    public bool IsVerificationBypass => IsAllowed && !RequiresVerification;
}

public sealed class EffectiveFilePolicyDto
{
    public string Extension { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public bool RequiresVerification { get; set; }
    public long? MaxFileBytes { get; set; }
    public int Version { get; set; }
}

public sealed class UpdateFilePolicyRequest
{
    public bool IsAllowed { get; set; }
    public bool RequiresVerification { get; set; } = true;
    public long? MaxFileBytes { get; set; }
    public int ExpectedVersion { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class RollbackFilePolicyRequest
{
    public Guid RevisionId { get; set; }
    public int ExpectedVersion { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class FilePolicyRevisionDto
{
    public Guid Id { get; set; }
    public string Extension { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public bool RequiresVerification { get; set; }
    public long? MaxFileBytes { get; set; }
    public int Version { get; set; }
    public DateTimeOffset ChangedUtc { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string ChangeReason { get; set; } = string.Empty;
}
