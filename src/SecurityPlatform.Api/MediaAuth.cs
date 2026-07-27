using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;
using SecurityPlatform.Modules.Security;

namespace SecurityPlatform.Api;

/// <summary>
/// Autorização de streaming delegada pelo nó de mídia.
///
/// Sem isto, quem alcança as portas de HLS/WebRTC/RTSP assiste qualquer câmera
/// sem passar pelo login — os direitos por câmera valeriam só no painel, e não
/// no vídeo em si. Com isto, cada requisição de stream é conferida contra o
/// mesmo <see cref="PermissionService"/> que governa o resto do sistema.
///
/// Vive no projeto de composição porque liga dois módulos: segurança (validação
/// do token) e VMS (convenção de nome do path).
/// </summary>
public static partial class MediaAuth
{
    // cam7 (main), cam7s (sub), cam7tc (transcoder H.264)
    [GeneratedRegex(@"^cam(?<id>\d+)(s|tc)?$", RegexOptions.Compiled)]
    private static partial Regex PathDeCamera();

    public static IEndpointRouteBuilder MapMediaAuth(this IEndpointRouteBuilder app)
    {
        // Chamado pelo proprio no de midia, nao pelo navegador — anonimo por
        // definicao, mas so responde 200 com token valido e direito na camera.
        app.MapPost("/api/media/auth", async (
            MediaAuthRequest req, AuthService auth, PermissionService perms,
            PlatformDbContext db, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("MediaAuth");

            // Publicacao (uma fonte enviando video para o no) nunca vem do
            // navegador; so o proprio servidor publica, e ele usa a API interna.
            if (!string.Equals(req.Action, "read", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var match = PathDeCamera().Match(req.Path ?? "");
            if (!match.Success)
            {
                log.LogWarning("Path de midia desconhecido: {Path}", req.Path);
                return Results.Unauthorized();
            }

            var deviceId = int.Parse(match.Groups["id"].Value);

            // O path precisa corresponder a uma camera cadastrada. Sem esta
            // checagem, um administrador destrancaria qualquer path 'camN',
            // inclusive de camera ja excluida cujo path tenha sobrado.
            var existe = await db.Devices.AnyAsync(
                d => d.Id == deviceId && d.Kind == DeviceKind.Camera);

            if (!existe)
            {
                log.LogWarning("Stream negado (camera inexistente) {Path} de {Ip}", req.Path, req.Ip);
                return Results.Unauthorized();
            }

            // Loopback: gravador FFmpeg e health no All-in-One leem o path
            // local do MediaMTX (rtsp://127.0.0.1:8554/camN) sem JWT — assim
            // a câmera sofre 1 pull (MediaMTX) e live+gravação reutilizam.
            // IP vem do MediaMTX (cliente real); externo continua exigindo JWT.
            if (IsLoopback(req.Ip))
                return Results.Ok();

            // Player: ?jwt= no HLS/WHEP; alguns builds MediaMTX mandam em Token/Password/User.
            var token = ExtrairToken(req.Query)
                ?? NullIfEmpty(req.Token)
                ?? NullIfEmpty(req.Password)
                ?? NullIfEmpty(req.User);

            var principal = auth.ValidateToken(token);
            if (principal is null)
            {
                log.LogWarning(
                    "Stream negado (token invalido) camera {Id} de {Ip} proto={Proto} queryLen={Q}",
                    deviceId, req.Ip, req.Protocol, req.Query?.Length ?? 0);
                return Results.Unauthorized();
            }

            var userId = principal.UserId();
            if (!await perms.HasAsync(userId, Permissions.CameraView, ObjectTypes.Camera, deviceId))
            {
                log.LogWarning("Stream negado (sem direito) camera {Id} usuario {User}",
                    deviceId, principal.UserName());
                return Results.Unauthorized();
            }

            return Results.Ok();
        }).AllowAnonymous();

        return app;
    }

    /// <summary>Lê <c>jwt=</c> de uma querystring crua, com ou sem o '?'.</summary>
    private static string? ExtrairToken(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var raw = query.TrimStart('?');
        try
        {
            var q = System.Web.HttpUtility.ParseQueryString(raw);
            var jwt = q["jwt"] ?? q["token"] ?? q["access_token"];
            if (!string.IsNullOrWhiteSpace(jwt)) return jwt;
        }
        catch { /* parse manual abaixo */ }

        // Fallback se ParseQueryString falhar (URL-encoded parcial).
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] is "jwt" or "token" or "access_token")
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool IsLoopback(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        var s = ip.Trim();
        // MediaMTX pode mandar "127.0.0.1", "::1", "[::1]:port" ou "127.0.0.1:port"
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end > 1) s = s[1..end];
        }
        else
        {
            var colon = s.LastIndexOf(':');
            // IPv4:port (uma única ':'); IPv6 sem colchetes não é o caso típico aqui.
            if (colon > 0 && s.Count(c => c == ':') == 1 && System.Net.IPAddress.TryParse(s[..colon], out _))
                s = s[..colon];
        }

        return System.Net.IPAddress.TryParse(s, out var addr) && System.Net.IPAddress.IsLoopback(addr);
    }
}

/// <summary>Corpo enviado pelo MediaMTX na autenticação externa.</summary>
public record MediaAuthRequest(
    string? User,
    string? Password,
    string? Token,
    string? Ip,
    string? Action,
    string? Path,
    string? Protocol,
    string? Id,
    string? Query);
