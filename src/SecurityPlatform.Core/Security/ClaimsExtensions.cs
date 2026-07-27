using System.Security.Claims;

namespace SecurityPlatform.Core.Security;

public static class ClaimsExtensions
{
    /// <summary>Id do usuario autenticado. 0 quando anonimo.</summary>
    public static int UserId(this ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public static string UserName(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.Name) ?? "";

    /// <summary>
    /// Tenant do usuario autenticado, vindo do token. Endpoints usam este valor
    /// em vez do que chega no corpo — aceitar do cliente permitiria criar ou
    /// consultar dados de outro cliente. 1 e a instalacao All-in-One padrao.
    /// </summary>
    public static int TenantId(this ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue("tenant"), out var id) && id > 0 ? id : 1;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("admin");
}
