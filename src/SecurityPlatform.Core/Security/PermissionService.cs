using Microsoft.EntityFrameworkCore;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Core.Security;

/// <summary>
/// Resolve direitos efetivos combinando usuario, grupos e perfis.
///
/// Tres regras, nesta ordem:
/// 1. Deny sempre vence Allow.
/// 2. ObjectId nulo vale para todos os objetos daquele tipo ("conceda tudo,
///    negue uma camera").
/// 3. Um direito sobre <c>cameragroup</c> vale para todas as cameras do grupo
///    e dos subgrupos. Como a expansao acontece na leitura, camera adicionada
///    ao grupo depois ja entra com o acesso — nao ha nada para reconfigurar.
///
/// O curinga <see cref="Permissions.FromRole"/> em um direito significa "as
/// permissoes do perfil deste sujeito", entao trocar o perfil de um grupo muda
/// o acesso de todos os membros sem reescrever uma linha de direito.
/// </summary>
public class PermissionService(PlatformDbContext db)
{
    public async Task<bool> HasAsync(
        int userId, string permission, string objectType = ObjectTypes.Camera,
        int? objectId = null, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.Active) return false;
        if (user.IsAdmin) return true;

        // Permissao global (gerir usuarios, configurar sistema): nao depende de
        // um objeto, basta existir um Allow em qualquer alcance.
        var resolved = await ResolveAsync(user, permission, ct);
        if (objectType != ObjectTypes.Camera || objectId is null)
            return resolved.Any(r => r.Effect == RightEffect.Allow)
                && !resolved.Any(r => r.Effect == RightEffect.Deny && r.CameraId is null);

        var applicable = resolved.Where(r => r.CameraId is null || r.CameraId == objectId).ToList();
        if (applicable.Any(r => r.Effect == RightEffect.Deny)) return false;
        return applicable.Any(r => r.Effect == RightEffect.Allow);
    }

    /// <summary>Ids de camera que o usuario pode ver — usado para filtrar listagens.</summary>
    public async Task<HashSet<int>> VisibleCameraIdsAsync(
        int userId, string permission = Permissions.CameraView, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.Active) return [];

        var allIds = await AllCameraIdsAsync(user.TenantId, ct);
        if (user.IsAdmin) return [.. allIds];

        var resolved = await ResolveAsync(user, permission, ct);

        var allowed = new HashSet<int>();
        foreach (var r in resolved.Where(r => r.Effect == RightEffect.Allow))
        {
            if (r.CameraId is null) allowed.UnionWith(allIds);
            else allowed.Add(r.CameraId.Value);
        }

        foreach (var r in resolved.Where(r => r.Effect == RightEffect.Deny))
        {
            if (r.CameraId is null) return [];
            allowed.Remove(r.CameraId.Value);
        }

        return allowed;
    }

    /// <summary>
    /// Direitos efetivos com a origem de cada um. Responde "por que este usuario
    /// ve esta camera?" — a pergunta que sem isso vira tentativa e erro.
    /// </summary>
    public async Task<EffectiveRights> ExplainAsync(int userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new EffectiveRights(userId, "", false, false, [], [], []);

        var groups = await GroupsOfAsync(userId, ct);
        var groupNames = groups.Select(g => g.Name).ToList();

        if (user.IsAdmin)
        {
            var todas = await AllCameraIdsAsync(user.TenantId, ct);
            var tudo = Permissions.All.Select(p =>
                new PermissionOrigin(p, true, "Administrador — ignora a checagem por objeto")).ToList();
            return new EffectiveRights(userId, user.Username, true, user.Active, groupNames, tudo, [.. todas]);
        }

        var detalhes = new List<PermissionOrigin>();
        foreach (var permission in Permissions.All)
        {
            var resolved = await ResolveAsync(user, permission, ct);

            var deny = resolved.FirstOrDefault(r => r.Effect == RightEffect.Deny);
            var allow = resolved.FirstOrDefault(r => r.Effect == RightEffect.Allow);

            detalhes.Add(deny is not null && allow is null
                ? new PermissionOrigin(permission, false, $"Negado por {deny.Source}")
                : allow is not null
                    ? new PermissionOrigin(permission, true, allow.Source)
                    : new PermissionOrigin(permission, false, "Nenhum direito concede esta permissao"));
        }

        var visiveis = await VisibleCameraIdsAsync(userId, Permissions.CameraView, ct);
        return new EffectiveRights(userId, user.Username, false, user.Active,
            groupNames, detalhes, [.. visiveis]);
    }

    /// <summary>
    /// Cameras que um sujeito (grupo ou usuario) alcanca com uma permissao —
    /// usado para mostrar a previa antes de salvar a configuracao de um grupo.
    /// </summary>
    public async Task<HashSet<int>> CamerasForSubjectAsync(
        SubjectType subjectType, int subjectId,
        string permission = Permissions.CameraView, CancellationToken ct = default)
    {
        var roleId = subjectType == SubjectType.Group
            ? (await db.UserGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == subjectId, ct))?.RoleId
            : null;

        var rolePerms = await PermissionsOfRoleAsync(roleId, ct);
        var rights = await db.ObjectRights.AsNoTracking()
            .Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId)
            .ToListAsync(ct);

        var membership = await MembershipAsync(ct);
        var allIds = await AllCameraIdsAsync(tenantId: null, ct);

        var resolved = new List<ResolvedRight>();
        foreach (var right in rights)
        {
            if (!Applies(right, permission, rolePerms)) continue;
            resolved.AddRange(Expand(right, membership, "previa"));
        }

        var allowed = new HashSet<int>();
        foreach (var r in resolved.Where(r => r.Effect == RightEffect.Allow))
        {
            if (r.CameraId is null) allowed.UnionWith(allIds);
            else allowed.Add(r.CameraId.Value);
        }
        foreach (var r in resolved.Where(r => r.Effect == RightEffect.Deny))
        {
            if (r.CameraId is null) return [];
            allowed.Remove(r.CameraId.Value);
        }
        return allowed;
    }

    // ------------------------------------------------------------- resolucao

    /// <summary>
    /// Coleta os direitos do usuario e dos grupos dele, ja expandindo grupos de
    /// camera em cameras e o curinga de perfil na permissao pedida.
    /// </summary>
    private async Task<List<ResolvedRight>> ResolveAsync(
        User user, string permission, CancellationToken ct)
    {
        var groups = await GroupsOfAsync(user.Id, ct);
        var groupIds = groups.Select(g => g.Id).ToList();

        var rights = await db.ObjectRights.AsNoTracking()
            .Where(r => r.TenantId == user.TenantId)
            .Where(r => (r.SubjectType == SubjectType.User && r.SubjectId == user.Id)
                     || (r.SubjectType == SubjectType.Group && groupIds.Contains(r.SubjectId)))
            .ToListAsync(ct);

        if (rights.Count == 0) return [];

        // Perfis em jogo: o do proprio usuario nao existe (usuario nao tem
        // perfil direto), entao so os grupos contribuem.
        var roleIds = groups.Where(g => g.RoleId is not null).Select(g => g.RoleId!.Value).Distinct().ToList();
        var rolePerms = await db.RolePermissions.AsNoTracking()
            .Where(p => roleIds.Contains(p.RoleId))
            .ToListAsync(ct);

        var permsByRole = rolePerms
            .GroupBy(p => p.RoleId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Permission).ToHashSet());

        var membership = await MembershipAsync(ct);
        var nameById = groups.ToDictionary(g => g.Id, g => g.Name);
        var cameraGroupNames = await db.CameraGroups.AsNoTracking()
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        var resolved = new List<ResolvedRight>();
        foreach (var right in rights)
        {
            // Para direito de grupo, o curinga usa as permissoes do perfil do grupo.
            HashSet<string> doSujeito = [];
            if (right.SubjectType == SubjectType.Group
                && groups.FirstOrDefault(g => g.Id == right.SubjectId)?.RoleId is int rid
                && permsByRole.TryGetValue(rid, out var set))
                doSujeito = set;

            if (!Applies(right, permission, doSujeito)) continue;

            var origem = Describe(right, nameById, cameraGroupNames, doSujeito.Contains(permission));
            resolved.AddRange(Expand(right, membership, origem));
        }

        return resolved;
    }

    /// <summary>Um direito vale para a permissao pedida diretamente ou via perfil.</summary>
    private static bool Applies(ObjectRight right, string permission, IReadOnlySet<string> rolePermissions)
        => right.Permission == permission
        || (right.Permission == Permissions.FromRole && rolePermissions.Contains(permission));

    /// <summary>Traduz o direito em alvos concretos: cameras ou "todas".</summary>
    private static IEnumerable<ResolvedRight> Expand(
        ObjectRight right, ILookup<int, int> camerasByGroup, string source)
    {
        if (right.ObjectType == ObjectTypes.CameraGroup)
        {
            // Grupo sem id = todos os grupos = todas as cameras.
            if (right.ObjectId is null)
            {
                yield return new ResolvedRight(null, right.Effect, source);
                yield break;
            }

            foreach (var cameraId in DescendantCameras(right.ObjectId.Value, camerasByGroup))
                yield return new ResolvedRight(cameraId, right.Effect, source);
            yield break;
        }

        yield return new ResolvedRight(right.ObjectId, right.Effect, source);
    }

    /// <summary>Cameras do grupo e de todos os subgrupos, sem repetir nem entrar em ciclo.</summary>
    private static IEnumerable<int> DescendantCameras(int groupId, ILookup<int, int> camerasByGroup)
    {
        var vistos = new HashSet<int>();
        var fila = new Queue<int>();
        fila.Enqueue(groupId);

        while (fila.Count > 0)
        {
            var atual = fila.Dequeue();
            if (!vistos.Add(atual)) continue;              // ciclo em ParentId nao trava

            foreach (var cameraId in camerasByGroup[atual])
                yield return cameraId;

            foreach (var filho in camerasByGroup[-atual])  // chave negativa = subgrupos
                fila.Enqueue(filho);
        }
    }

    /// <summary>
    /// Mapa camera-por-grupo e subgrupo-por-grupo em uma so estrutura: a chave
    /// positiva lista cameras, a negativa lista subgrupos. Uma consulta em vez
    /// de uma por nivel da arvore.
    /// </summary>
    private async Task<ILookup<int, int>> MembershipAsync(CancellationToken ct)
    {
        var membros = await db.CameraGroupMembers.AsNoTracking()
            .Select(m => new { m.GroupId, m.DeviceId }).ToListAsync(ct);

        var filhos = await db.CameraGroups.AsNoTracking()
            .Where(g => g.ParentId != null)
            .Select(g => new { ParentId = g.ParentId!.Value, g.Id }).ToListAsync(ct);

        return membros.Select(m => (Key: m.GroupId, Value: m.DeviceId))
            .Concat(filhos.Select(f => (Key: -f.ParentId, Value: f.Id)))
            .ToLookup(x => x.Key, x => x.Value);
    }

    private static string Describe(
        ObjectRight right, IReadOnlyDictionary<int, string> userGroups,
        IReadOnlyDictionary<int, string> cameraGroups, bool viaPerfil)
    {
        var sujeito = right.SubjectType == SubjectType.Group
            ? $"grupo \"{userGroups.GetValueOrDefault(right.SubjectId, right.SubjectId.ToString())}\""
            : "direito individual";

        var alvo = right.ObjectType == ObjectTypes.CameraGroup
            ? right.ObjectId is null
                ? "todos os grupos de camera"
                : $"grupo de camera \"{cameraGroups.GetValueOrDefault(right.ObjectId.Value, right.ObjectId.ToString()!)}\""
            : right.ObjectId is null ? "todas as cameras" : $"camera {right.ObjectId}";

        return viaPerfil
            ? $"{sujeito} (perfil) sobre {alvo}"
            : $"{sujeito} sobre {alvo}";
    }

    private async Task<List<UserGroup>> GroupsOfAsync(int userId, CancellationToken ct)
        => await db.UserGroupMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.UserGroups, m => m.GroupId, g => g.Id, (_, g) => g)
            .ToListAsync(ct);

    private async Task<HashSet<string>> PermissionsOfRoleAsync(int? roleId, CancellationToken ct)
        => roleId is null
            ? []
            : [.. await db.RolePermissions.AsNoTracking()
                .Where(p => p.RoleId == roleId).Select(p => p.Permission).ToListAsync(ct)];

    private async Task<List<int>> AllCameraIdsAsync(int? tenantId, CancellationToken ct)
    {
        var q = db.Devices.AsNoTracking().Where(d => d.Kind == DeviceKind.Camera);
        if (tenantId is not null) q = q.Where(d => d.TenantId == tenantId);
        return await q.Select(d => d.Id).ToListAsync(ct);
    }

    /// <summary>Direito ja reduzido a uma camera concreta (ou todas, quando nulo).</summary>
    private record ResolvedRight(int? CameraId, RightEffect Effect, string Source);
}

public record PermissionOrigin(string Permission, bool Granted, string Origin);

public record EffectiveRights(
    int UserId,
    string Username,
    bool IsAdmin,
    bool Active,
    IReadOnlyList<string> Groups,
    IReadOnlyList<PermissionOrigin> Permissions,
    IReadOnlyList<int> VisibleCameras);
