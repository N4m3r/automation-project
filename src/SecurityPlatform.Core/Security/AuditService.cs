using Microsoft.AspNetCore.Http;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Core.Security;

/// <summary>Trilha de auditoria — quem, o que, quando e de onde.</summary>
public class AuditService(PlatformDbContext db)
{
    public async Task LogAsync(
        string action,
        string ip,
        string username,
        int? userId = null,
        bool success = true,
        string objectType = "",
        string objectId = "",
        string detail = "",
        CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Username = username,
            Action = action,
            ObjectType = objectType,
            ObjectId = objectId,
            Success = success,
            Detail = detail,
            IpAddress = ip
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Atalho para endpoints: extrai usuario e IP do proprio contexto.</summary>
    public Task WriteAsync(
        HttpContext ctx,
        string action,
        string objectType = "",
        string objectId = "",
        bool success = true,
        string detail = "")
        => LogAsync(
            action,
            ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            ctx.User.UserName(),
            ctx.User.UserId() is var id and > 0 ? id : null,
            success, objectType, objectId, detail, ctx.RequestAborted);
}
