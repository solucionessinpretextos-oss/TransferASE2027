using System.Security.Claims;

namespace FileXfer.Server.Security;

public static class RfcResourceAuthorization
{
    public static bool CanAccess(ClaimsPrincipal principal, string targetRfc)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var callerRfc = principal.FindFirst("rfc")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(callerRfc) &&
            string.Equals(callerRfc, targetRfc?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasPrivilegedRole = principal.IsInRole("admin") || principal.IsInRole("ops");
        return hasPrivilegedRole && principal.HasClaim("amr", "efirma");
    }
}
