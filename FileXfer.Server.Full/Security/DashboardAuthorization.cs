using Microsoft.AspNetCore.Authorization;

namespace FileXfer.Server.Security;

public static class DashboardAuthorization
{
    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy("PrivilegedEfirma", policy => policy
            .RequireAuthenticatedUser().RequireRole("admin", "ops").RequireClaim("amr", "efirma"));
        options.AddPolicy("AdminEfirma", policy => policy
            .RequireAuthenticatedUser().RequireRole("admin").RequireClaim("amr", "efirma"));
    }
}
