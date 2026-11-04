using System.Reflection;
using System.Security.Claims;
using FileXfer.Server.Controllers;
using FileXfer.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

internal static class DashboardAccessSmokeTests
{
    public static async Task RunAsync()
    {
        const string actor = "ADM010101AAA";
        const string target = "OPS010101AAA";
        var factory = new AccessDbFactory(new DbContextOptionsBuilder<FileXferDbContext>()
            .UseInMemoryDatabase("dashboard-access-" + Guid.NewGuid()).Options);
        var controller = new RfcAdminController(factory)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("rfc", actor), new Claim(ClaimTypes.Role, "admin"), new Claim("amr", "efirma")
                    }, "test"))
                }
            }
        };
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new RfcUser { Id = Guid.NewGuid(), Rfc = actor, Roles = new[] { "admin", "user" } });
            await db.SaveChangesAsync();
        }

        Require(typeof(RfcAdminController).GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == "PrivilegedEfirma"), "Falta e.firma en la ruta de RFC.");
        foreach (var name in new[] { "Create", "Update", "Activate", "Deactivate" })
            Require(typeof(RfcAdminController).GetMethod(name)!.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Roles == "admin"), $"{name} no exige admin.");

        var dto = new RfcAdminController.RfcUserDto(" ops010101aaa ", "Consulta", new[] { "ops", "user" }, true, "Referencia");
        Require(await controller.Create(dto) is CreatedAtActionResult, "No se creó la autorización.");
        Require(await controller.Create(dto) is ConflictObjectResult, "Se permitió RFC duplicado.");
        Require(await controller.Create(dto with { rfc = "INV010101AAA", roles = new[] { "superadmin" } }) is BadRequestObjectResult, "Se admitió un rol desconocido.");
        Require(await controller.Create(dto with { rfc = "NO-ES-RFC" }) is BadRequestObjectResult, "Se admitió RFC inválido.");
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(u => u.Rfc == target);
            Require(!user.MfaRequired && user.Roles.Contains("ops"), "El alta no conservó e.firma sin MFA.");
            db.RefreshTokens.AddRange(new[] { "dashboard-test", "client-test" }.Select(device => new RefreshToken
            {
                Id = Guid.NewGuid(), Rfc = target, DeviceId = device, TokenHash = Guid.NewGuid().ToString("N"), SecurityVersion = user.SecurityVersion,
                CreatedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1), AbsoluteExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            }));
            await db.SaveChangesAsync();
        }

        Require(await controller.Update(target, dto with { rfc = target, roles = new[] { "user" } }) is OkObjectResult, "No se retiraron los permisos.");
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(u => u.Rfc == target);
            Require(user.IsActive && user.Roles.SequenceEqual(new[] { "user" }), "Retirar Dashboard desactivó la cuenta del cliente.");
            Require(user.SecurityVersion == 2 && await db.RefreshTokens.AllAsync(t => t.IsRevoked), "No se invalidaron JWT y refresh anteriores.");
            Require(await db.AuditLogs.AnyAsync(a => a.ActorRfc == actor && a.Action == "rfc_provision_update" && a.Details!.Contains(target)), "No quedó auditoría atribuida al administrador.");
        }

        Require(await controller.Update(actor, dto with { rfc = actor, roles = new[] { "user" } }) is BadRequestObjectResult, "El administrador pudo retirarse sus permisos.");
        Require(await controller.Deactivate(actor) is BadRequestObjectResult, "El administrador pudo desactivarse.");
        // Simula otra identidad para comprobar el guard del último administrador por separado.
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("rfc", "OTR010101AAA") }, "test"));
        Require(await controller.Update(actor, dto with { rfc = actor, roles = new[] { "ops" } }) is BadRequestObjectResult, "Se retiró el último administrador activo.");

        Require(await controller.Update(target, dto with { rfc = target, roles = new[] { "admin", "user" } }) is OkObjectResult, "No se pudo volver a autorizar.");
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(u => u.Rfc == target);
            Require(user.SecurityVersion == 3 && user.IsActive && user.Roles.Contains("admin"), "No se actualizó la autorización restaurada.");
            Require(await db.Users.CountAsync() == 2, "Se crearon cuentas inesperadas.");
        }
        Console.WriteLine("PASS dashboard-access: normalized RFC, duplicate/invalid rejection, role guards, audit, session revocation, client preserved, self/last-admin protection, reauthorization.");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class AccessDbFactory(DbContextOptions<FileXferDbContext> options) : IDbContextFactory<FileXferDbContext>
    {
        public FileXferDbContext CreateDbContext() => new(options);
        public Task<FileXferDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
