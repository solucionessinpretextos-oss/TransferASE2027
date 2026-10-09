// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/JwtService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de backend: concentra reglas de negocio, acceso a BuDi, seguridad auxiliar o integración interna.
// Interacción: Es invocado por controladores, endpoints gRPC u otros servicios; suele ser el punto donde se conectan DTOs, SQL y reglas de negocio.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.IdentityModel.Tokens.Jwt; using System.Security.Claims; using System.Text;
using FileXfer.Server.Options; using Microsoft.Extensions.Options; using Microsoft.IdentityModel.Tokens;
namespace FileXfer.Server.Services;
public sealed class JwtService{
  private readonly JwtOptions _opt; private readonly SymmetricSecurityKey _key;
  public JwtService(IOptions<JwtOptions> opt){
    _opt=opt.Value;
    if(string.IsNullOrWhiteSpace(_opt.SigningKey)) throw new InvalidOperationException("JWT signing key no configurada.");
    var keyBytes=Encoding.UTF8.GetBytes(_opt.SigningKey);
    if(keyBytes.Length<32) throw new InvalidOperationException("JWT signing key debe tener al menos 32 bytes.");
    _key=new SymmetricSecurityKey(keyBytes);
  }
  public string Issue(
      string rfc,
      IEnumerable<string> roles,
      int securityVersion,
      string authenticationMethod,
      string? deviceId=null,
      TimeSpan? ttl=null){
    var creds=new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
    var now=DateTimeOffset.UtcNow;
    var claims=new List<Claim>{
      new("rfc",rfc),
      new(ClaimTypes.NameIdentifier,rfc),
      new("securityVersion",securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
      new("amr",authenticationMethod),
      new("auth_time",now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
    };
    foreach(var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
    if(!string.IsNullOrWhiteSpace(deviceId)) claims.Add(new("deviceId", deviceId));
    var accessMinutes=Math.Clamp(_opt.AccessTokenMinutes,5,60);
    var token=new JwtSecurityToken(_opt.Issuer,_opt.Audience,claims,DateTime.UtcNow.AddMinutes(-1),DateTime.UtcNow.Add((ttl??TimeSpan.FromMinutes(accessMinutes))),creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
  }
}
