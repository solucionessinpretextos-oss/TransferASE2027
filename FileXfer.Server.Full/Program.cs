// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Program.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Punto de arranque del backend: configura DI, autenticación, Kestrel, Swagger, REST, gRPC y servicios.
// Interacción: Es la raíz del pipeline de ejecución; de aquí dependen controladores, servicios, seguridad y hosting.
// Regla de mantenimiento: Cambios de orden en middleware pueden romper autenticación, CORS, Swagger, gRPC o routing.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Config;
using System.Text;
using FileXfer.Server;
using FileXfer.Server.Data;
using FileXfer.Server.Endpoints;
using FileXfer.Server.Options;
using FileXfer.Server.Services;
using FileXfer.Server.Security;
using FileXfer.Server.Uploading;
using FileXfer.Pdf;
using FileXfer.FileSafety;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
    var maxUploadBytesForKestrel = context.Configuration.GetValue<long?>("Storage:MaxUploadBytes")
                                   ?? 1024L * 1024L * 1024L;
    options.Limits.MaxRequestBodySize = maxUploadBytesForKestrel + (16L * 1024L * 1024L);
    var maxStreamsPerConnection = context.Configuration.GetValue<int?>("Storage:MaxConcurrentUploadsTotal") ?? 200;
    options.Limits.Http2.MaxStreamsPerConnection = Math.Clamp(maxStreamsPerConnection, 1, 1_000);
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Security:Jwt"));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("Security:AuthRateLimit"));
builder.Services.Configure<DevicePolicyOptions>(builder.Configuration.GetSection("Security:DevicePolicy"));
builder.Services.Configure<SatOptions>(builder.Configuration.GetSection("Security:SAT"));
builder.Services.Configure<DebugFeaturesOptions>(builder.Configuration.GetSection("DebugFeatures"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));
var receiptSigning = builder.Configuration.GetSection("Security:Receipts").Get<ReceiptSigningOptions>()
                     ?? new ReceiptSigningOptions();
builder.Services.AddSingleton(receiptSigning);

var configuredSat = builder.Configuration.GetSection("Security:SAT").Get<SatOptions>() ?? new SatOptions();
if (!builder.Environment.IsDevelopment() && configuredSat.TrustedRootCertificatePaths.Length == 0)
{
    throw new InvalidOperationException(
        "Security:SAT:TrustedRootCertificatePaths debe contener al menos una raíz SAT fuera de Development.");
}
if (!builder.Environment.IsDevelopment() &&
    configuredSat.RequireOcsp &&
    configuredSat.AllowedRevocationHosts.Length == 0)
{
    throw new InvalidOperationException(
        "Security:SAT:AllowedRevocationHosts debe declarar los hosts OCSP/CRL SAT cuando RequireOcsp=true.");
}

var defaultDevPath = @"C:\Tmp\Transfer\uploads";
var configuredPath = builder.Configuration["Storage:StoragePath"]
                     ?? builder.Configuration["Storage:BasePath"];

var storageBasePath = !string.IsNullOrWhiteSpace(configuredPath)
    ? configuredPath
    : builder.Environment.IsDevelopment()
        ? defaultDevPath
        : throw new InvalidOperationException(
            "Storage:BasePath es obligatorio fuera de Development. No se usará almacenamiento implícito en producción.");

Directory.CreateDirectory(storageBasePath);
var maxUploadBytes = builder.Configuration.GetValue<long?>("Storage:MaxUploadBytes")
                     ?? 1024L * 1024L * 1024L;
var maxChunkBytes = builder.Configuration.GetValue<int?>("Storage:MaxChunkBytes")
                    ?? 2 * 1024 * 1024;
var maxConcurrentUploadsTotal = builder.Configuration.GetValue<int?>("Storage:MaxConcurrentUploadsTotal")
                                ?? 200;
var maxConcurrentUploadsPerRfc = builder.Configuration.GetValue<int?>("Storage:MaxConcurrentUploadsPerRfc")
                                 ?? 5;
var firstChunkTimeoutSeconds = builder.Configuration.GetValue<int?>("Storage:FirstChunkTimeoutSeconds") ?? 30;
var idleChunkTimeoutSeconds = builder.Configuration.GetValue<int?>("Storage:IdleChunkTimeoutSeconds") ?? 120;
var maxUploadDurationMinutes = builder.Configuration.GetValue<int?>("Storage:MaxUploadDurationMinutes") ?? 60;
var resumeRetentionHours = builder.Configuration.GetValue<int?>("Storage:ResumeRetentionHours") ?? 24;

if (maxUploadBytes <= 0)
    throw new InvalidOperationException("Storage:MaxUploadBytes debe ser mayor que cero.");
if (maxChunkBytes is <= 0 or > 4 * 1024 * 1024)
    throw new InvalidOperationException("Storage:MaxChunkBytes debe estar entre 1 y 4194304 bytes.");
if (maxConcurrentUploadsTotal is < 1 or > 10_000)
    throw new InvalidOperationException("Storage:MaxConcurrentUploadsTotal debe estar entre 1 y 10000.");
if (maxConcurrentUploadsPerRfc is < 1 or > 100 ||
    maxConcurrentUploadsPerRfc > maxConcurrentUploadsTotal)
{
    throw new InvalidOperationException(
        "Storage:MaxConcurrentUploadsPerRfc debe estar entre 1 y 100 y no superar el límite global.");
}
if (firstChunkTimeoutSeconds is < 5 or > 300)
    throw new InvalidOperationException("Storage:FirstChunkTimeoutSeconds debe estar entre 5 y 300.");
if (idleChunkTimeoutSeconds is < 5 or > 900)
    throw new InvalidOperationException("Storage:IdleChunkTimeoutSeconds debe estar entre 5 y 900.");
if (maxUploadDurationMinutes is < 1 or > 1_440)
    throw new InvalidOperationException("Storage:MaxUploadDurationMinutes debe estar entre 1 y 1440.");
if (resumeRetentionHours is < 1 or > 168)
    throw new InvalidOperationException("Storage:ResumeRetentionHours debe estar entre 1 y 168.");

builder.Services.AddSingleton(new StorageOptions
{
    BasePath = storageBasePath,
    MaxUploadBytes = maxUploadBytes,
    MaxChunkBytes = maxChunkBytes,
    MaxConcurrentUploadsTotal = maxConcurrentUploadsTotal,
    MaxConcurrentUploadsPerRfc = maxConcurrentUploadsPerRfc,
    FirstChunkTimeoutSeconds = firstChunkTimeoutSeconds,
    IdleChunkTimeoutSeconds = idleChunkTimeoutSeconds,
    MaxUploadDurationMinutes = maxUploadDurationMinutes,
    ResumeRetentionHours = resumeRetentionHours
});
builder.Services.AddClientUpdates(builder.Configuration);

var cs = builder.Configuration.GetConnectionString("FileXferDb");

if (!string.IsNullOrWhiteSpace(cs))
{
    builder.Services.AddDbContextFactory<FileXferDbContext>(o => o.UseSqlServer(cs));
}
else if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDbContextFactory<FileXferDbContext>(o => o.UseInMemoryDatabase("fx"));
}
else
{
    throw new InvalidOperationException(
        "ConnectionStrings:FileXferDb es obligatoria fuera de Development. " +
        "Producción no puede usar una base volátil en memoria.");
}

if (!builder.Environment.IsDevelopment() &&
    string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("BudiDev")))
{
    throw new InvalidOperationException(
        "ConnectionStrings:BudiDev es obligatoria fuera de Development.");
}

var allowedHosts = builder.Configuration["AllowedHosts"]?.Trim();
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts == "*"))
{
    throw new InvalidOperationException(
        "AllowedHosts debe contener los nombres DNS explícitos del servicio fuera de Development.");
}

builder.Services.AddSingleton<IPathProtector, AesGcmPathProtector>();
builder.Services.AddSingleton<IMfaSecretProtector, AesGcmMfaSecretProtector>();
builder.Services.AddSingleton<IBudiDevRepository, BudiDevRepository>();

var jwt = builder.Configuration.GetSection("Security:Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
    throw new InvalidOperationException("Security:Jwt:Issuer y Audience son obligatorios.");
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
    throw new InvalidOperationException("Security:Jwt:SigningKey debe proporcionarse mediante configuración segura.");

var jwtKeyBytes = Encoding.UTF8.GetBytes(jwt.SigningKey);
if (jwtKeyBytes.Length < 32)
    throw new InvalidOperationException("Security:Jwt:SigningKey debe contener al menos 32 bytes de entropía.");

var signingKey = new SymmetricSecurityKey(
    jwtKeyBytes
);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var rfc = context.Principal?.FindFirst("rfc")?.Value?.Trim().ToUpperInvariant();
                var versionRaw = context.Principal?.FindFirst("securityVersion")?.Value;
                if (string.IsNullOrWhiteSpace(rfc) ||
                    !int.TryParse(versionRaw, out var tokenSecurityVersion))
                {
                    context.Fail("Token sin versión de seguridad.");
                    return;
                }

                var factory = context.HttpContext.RequestServices
                    .GetRequiredService<IDbContextFactory<FileXferDbContext>>();
                await using var validationDb = await factory.CreateDbContextAsync(context.HttpContext.RequestAborted);
                var userState = await validationDb.Users
                    .AsNoTracking()
                    .Where(user => user.Rfc == rfc)
                    .Select(user => new { user.IsActive, user.SecurityVersion })
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

                if (userState is null || !userState.IsActive || userState.SecurityVersion != tokenSecurityVersion)
                {
                    context.Fail("La sesión fue revocada o el usuario ya no está activo.");
                    return;
                }

                var deviceId = context.Principal?.FindFirst("deviceId")?.Value;
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    var deviceRevoked = await validationDb.Devices
                        .AsNoTracking()
                        .Where(device => device.Rfc == rfc && device.DeviceId == deviceId)
                        .Select(device => (bool?)device.Revoked)
                        .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
                    if (deviceRevoked == true)
                    {
                        context.Fail("El dispositivo fue revocado.");
                    }
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PrivilegedMfa", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("admin", "ops");
        policy.RequireClaim("amr", "efirma+mfa");
    });
    options.AddPolicy("AdminMfa", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("admin");
        policy.RequireClaim("amr", "efirma+mfa");
    });
});

const string AdminDashboardCorsPolicy = "AllowAdminDashboardDev";

builder.Services.AddCors(options =>
{
    options.AddPolicy(AdminDashboardCorsPolicy, policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddGrpc();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<UploadSessionStore>();
builder.Services.AddSingleton<FileAssembler>();
builder.Services.AddSingleton<IPdfSafetyService, QpdfPdfSafetyService>();
builder.Services.AddSingleton<IFileSafetyService, DefaultFileSafetyService>();
builder.Services.AddSingleton<IFilePolicyService, FilePolicyService>();
builder.Services.AddSingleton<IUploadReceiptService, UploadReceiptService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<ISatCertificateValidator, SatCertificateValidator>();
builder.Services.AddSingleton<RateLockService>();
builder.Services.AddHostedService<AuthStateCleanupService>();
builder.Services.AddHostedService<ResumableUploadCleanupService>();

builder.Services.AddSingleton<ISystemHealthService, SystemHealthService>();

builder.Services.AddSingleton<LogAlertSink>();
builder.Services.AddSingleton<EmailAlertSink>();
builder.Services.AddSingleton<WebhookAlertSink>();
builder.Services.AddSingleton<IAlertSink>(sp =>
{
    var opts = sp.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<AlertOptions>>().Value;

    var sinks = new List<IAlertSink> { sp.GetRequiredService<LogAlertSink>() };
    if (opts.Enabled)
    {
        sinks.Add(sp.GetRequiredService<EmailAlertSink>());
        sinks.Add(sp.GetRequiredService<WebhookAlertSink>());
    }
    return new CompositeAlertSink(sinks);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FileXferDbContext>>();
    await using var db = await factory.CreateDbContextAsync();

    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
    else
        await db.Database.EnsureCreatedAsync();

    var seedAdmins = builder.Configuration
        .GetSection("Security:SeedAdmins")
        .Get<string[]>() ?? Array.Empty<string>();

    // El bootstrap solo existe para una base vacía. Mantener un RFC en configuración no debe
    // recrear silenciosamente una cuenta administrativa eliminada o desactivada.
    var allowBootstrap = !await db.Users.AnyAsync();
    foreach (var rfcRaw in allowBootstrap ? seedAdmins : Array.Empty<string>())
    {
        var r = rfcRaw.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(r)) continue;

        if (!await db.Users.AnyAsync(x => x.Rfc == r))
        {
            db.Users.Add(new RfcUser
            {
                Id = Guid.NewGuid(),
                Rfc = r,
                Roles = new[] { "admin" },
                MfaRequired = false,
                IsActive = true,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedBy = "seed"
            });

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                ActorRfc = "seed",
                Action = "rfc_provision_seed_admin",
                Details = $"RFC={r}"
            });
        }
    }

    await db.SaveChangesAsync();

    var filePolicyService = scope.ServiceProvider.GetRequiredService<IFilePolicyService>();
    await filePolicyService.EnsureSeededAsync();
    _ = scope.ServiceProvider.GetRequiredService<IUploadReceiptService>();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var incoming = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    var correlationId = Guid.TryParse(incoming, out var parsed)
        ? parsed.ToString("D")
        : Guid.NewGuid().ToString("D");
    context.TraceIdentifier = correlationId;
    System.Diagnostics.Activity.Current?.SetTag("filexfer.correlation_id", correlationId);
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return Task.CompletedTask;
    });
    using var correlationScope = app.Logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
        ["TraceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty
    });
    await next();
});

app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
    context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    context.Response.Headers.TryAdd("X-Permitted-Cross-Domain-Policies", "none");
    if (!app.Environment.IsDevelopment() || !context.Request.Path.StartsWithSegments("/swagger"))
    {
        context.Response.Headers.TryAdd(
            "Content-Security-Policy",
            "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
            "form-action 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'");
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
    app.UseCors(AdminDashboardCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<UploadGrpcService>();
app.MapControllers();
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

{
    var debugOpts = app.Services
        .GetRequiredService<
            Microsoft.Extensions.Options.IOptions<DebugFeaturesOptions>>()
        .Value;

    if (app.Environment.IsDevelopment() &&
        debugOpts.EnableDebugUploadEndpoints &&
        debugOpts.AllowedEnvironments.Contains(
            app.Environment.EnvironmentName,
            StringComparer.OrdinalIgnoreCase))
    {
        app.MapDebugUpload(debugOpts.AllowedRoles);
    }
}

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow
}));

app.Run();

public partial class Program { }
