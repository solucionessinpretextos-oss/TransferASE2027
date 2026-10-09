using System.Text.RegularExpressions;

namespace FileXfer.Server.Security;

internal static partial class IdentityInputPolicy
{
    private static readonly HashSet<string> AllowedRoles = new(
        new[] { "user", "ops", "admin" },
        StringComparer.Ordinal);

    public static bool TryNormalizeRfc(string? value, out string normalized)
    {
        normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return RfcRegex().IsMatch(normalized);
    }

    public static bool IsValidDeviceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DeviceIdRegex().IsMatch(value);

    public static bool TryNormalizeRoles(IEnumerable<string>? values, out string[] roles)
    {
        roles = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return roles.Length > 0 && roles.All(AllowedRoles.Contains);
    }

    [GeneratedRegex(@"^[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RfcRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DeviceIdRegex();
}
