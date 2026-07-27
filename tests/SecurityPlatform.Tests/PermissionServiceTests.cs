using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Tests;

/// <summary>
/// A resolucao de direitos e o ponto onde um erro nao aparece como falha: ele
/// aparece como alguem vendo uma camera que nao deveria. Estes testes cobrem as
/// regras que o resto do sistema assume verdadeiras.
/// </summary>
public class PermissionServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly PlatformDbContext _db;
    private readonly PermissionService _perms;

    public PermissionServiceTests()
    {
        // SQLite em memoria mantem o schema real (indices, unique) — banco
        // falso esconderia justamente os erros de modelagem.
        _conn = new SqliteConnection("Filename=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_conn).Options;

        _db = new PlatformDbContext(options);
        _db.Database.EnsureCreated();
        _perms = new PermissionService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------- cenarios

    /// <summary>Portaria (1,2) e Garagem (3) sob Predio; Externa (4) fora da arvore.</summary>
    private async Task<(int predio, int portaria, int garagem)> SeedCamerasAsync()
    {
        _db.Devices.AddRange(
            new Device { Id = 1, TenantId = 1, Name = "Entrada", Kind = DeviceKind.Camera },
            new Device { Id = 2, TenantId = 1, Name = "Hall", Kind = DeviceKind.Camera },
            new Device { Id = 3, TenantId = 1, Name = "Garagem G1", Kind = DeviceKind.Camera },
            new Device { Id = 4, TenantId = 1, Name = "Externa", Kind = DeviceKind.Camera });

        var predio = new CameraGroup { Name = "Predio" };
        _db.CameraGroups.Add(predio);
        await _db.SaveChangesAsync();

        var portaria = new CameraGroup { Name = "Portaria", ParentId = predio.Id };
        var garagem = new CameraGroup { Name = "Garagem", ParentId = predio.Id };
        _db.CameraGroups.AddRange(portaria, garagem);
        await _db.SaveChangesAsync();

        _db.CameraGroupMembers.AddRange(
            new CameraGroupMember { GroupId = portaria.Id, DeviceId = 1 },
            new CameraGroupMember { GroupId = portaria.Id, DeviceId = 2 },
            new CameraGroupMember { GroupId = garagem.Id, DeviceId = 3 });
        await _db.SaveChangesAsync();

        return (predio.Id, portaria.Id, garagem.Id);
    }

    private async Task<int> SeedRoleAsync(string nome, params string[] permissoes)
    {
        var role = new Role { Name = nome };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        foreach (var p in permissoes)
            _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        await _db.SaveChangesAsync();
        return role.Id;
    }

    private async Task<(int userId, int groupId)> SeedUserInGroupAsync(string nome, int? roleId)
    {
        var user = new User { Username = nome, TenantId = 1, Active = true };
        _db.Users.Add(user);

        var group = new UserGroup { Name = $"grupo-{nome}", RoleId = roleId, TenantId = 1 };
        _db.UserGroups.Add(group);
        await _db.SaveChangesAsync();

        _db.UserGroupMembers.Add(new UserGroupMember { UserId = user.Id, GroupId = group.Id });
        await _db.SaveChangesAsync();
        return (user.Id, group.Id);
    }

    private void Grant(int groupId, string objectType, int? objectId,
        string permission, RightEffect effect = RightEffect.Allow)
        => _db.ObjectRights.Add(new ObjectRight
        {
            TenantId = 1,
            SubjectType = SubjectType.Group,
            SubjectId = groupId,
            ObjectType = objectType,
            ObjectId = objectId,
            Permission = permission,
            Effect = effect
        });

    // ---------------------------------------------------------------- testes

    [Fact]
    public async Task Direito_em_grupo_de_camera_alcanca_as_cameras_do_grupo()
    {
        var (_, portaria, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("ana", role);

        Grant(groupId, ObjectTypes.CameraGroup, portaria, Permissions.FromRole);
        await _db.SaveChangesAsync();

        var visiveis = await _perms.VisibleCameraIdsAsync(userId);

        Assert.Equal([1, 2], visiveis.Order());
    }

    [Fact]
    public async Task Direito_no_grupo_pai_desce_para_os_subgrupos()
    {
        var (predio, _, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("bruno", role);

        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        await _db.SaveChangesAsync();

        var visiveis = await _perms.VisibleCameraIdsAsync(userId);

        // 1 e 2 da Portaria, 3 da Garagem. A 4 esta fora da arvore.
        Assert.Equal([1, 2, 3], visiveis.Order());
        Assert.DoesNotContain(4, visiveis);
    }

    [Fact]
    public async Task Camera_adicionada_ao_grupo_depois_ja_nasce_com_acesso()
    {
        var (_, portaria, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("carla", role);

        Grant(groupId, ObjectTypes.CameraGroup, portaria, Permissions.FromRole);
        await _db.SaveChangesAsync();

        Assert.DoesNotContain(4, await _perms.VisibleCameraIdsAsync(userId));

        // Nenhum direito e tocado — so a associacao da camera ao grupo.
        _db.CameraGroupMembers.Add(new CameraGroupMember { GroupId = portaria, DeviceId = 4 });
        await _db.SaveChangesAsync();

        Assert.Contains(4, await _perms.VisibleCameraIdsAsync(userId));
    }

    [Fact]
    public async Task Deny_pontual_vence_o_allow_amplo_do_grupo()
    {
        var (predio, _, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("diego", role);

        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        Grant(groupId, ObjectTypes.Camera, 3, Permissions.FromRole, RightEffect.Deny);
        await _db.SaveChangesAsync();

        var visiveis = await _perms.VisibleCameraIdsAsync(userId);

        Assert.Equal([1, 2], visiveis.Order());
        Assert.False(await _perms.HasAsync(userId, Permissions.CameraView, ObjectTypes.Camera, 3));
    }

    [Fact]
    public async Task Trocar_o_perfil_do_grupo_muda_o_acesso_sem_mexer_nos_direitos()
    {
        var (predio, _, _) = await SeedCamerasAsync();
        var visualizador = await SeedRoleAsync("Visualizador", Permissions.CameraView);
        var operador = await SeedRoleAsync("Operador", Permissions.CameraView, Permissions.CameraPtz);

        var (userId, groupId) = await SeedUserInGroupAsync("elisa", visualizador);
        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        await _db.SaveChangesAsync();

        Assert.False(await _perms.HasAsync(userId, Permissions.CameraPtz, ObjectTypes.Camera, 1));

        // Uma coluna muda; nenhuma linha de ObjectRight e reescrita.
        (await _db.UserGroups.FindAsync(groupId))!.RoleId = operador;
        await _db.SaveChangesAsync();

        Assert.True(await _perms.HasAsync(userId, Permissions.CameraPtz, ObjectTypes.Camera, 1));
    }

    [Fact]
    public async Task Permissao_fora_do_perfil_nao_e_concedida()
    {
        var (predio, _, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Visualizador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("fabio", role);

        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        await _db.SaveChangesAsync();

        Assert.True(await _perms.HasAsync(userId, Permissions.CameraView, ObjectTypes.Camera, 1));
        Assert.False(await _perms.HasAsync(userId, Permissions.CameraExport, ObjectTypes.Camera, 1));
        Assert.False(await _perms.HasAsync(userId, Permissions.CameraConfig, ObjectTypes.Camera, 1));
    }

    [Fact]
    public async Task Usuario_inativo_perde_todo_o_acesso()
    {
        var (predio, _, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("gil", role);

        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        await _db.SaveChangesAsync();

        Assert.NotEmpty(await _perms.VisibleCameraIdsAsync(userId));

        (await _db.Users.FindAsync(userId))!.Active = false;
        await _db.SaveChangesAsync();

        Assert.Empty(await _perms.VisibleCameraIdsAsync(userId));
        Assert.False(await _perms.HasAsync(userId, Permissions.CameraView, ObjectTypes.Camera, 1));
    }

    [Fact]
    public async Task Ciclo_na_arvore_de_grupos_nao_trava_a_resolucao()
    {
        var (predio, portaria, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("helena", role);

        // Configuracao invalida vinda de um banco legado: Predio -> Portaria -> Predio.
        (await _db.CameraGroups.FindAsync(predio))!.ParentId = portaria;
        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        await _db.SaveChangesAsync();

        // O que importa e terminar; sem protecao isto seria um laco infinito.
        var visiveis = await _perms.VisibleCameraIdsAsync(userId);
        Assert.Equal([1, 2, 3], visiveis.Order());
    }

    [Fact]
    public async Task Administrador_ve_tudo_sem_nenhum_direito_cadastrado()
    {
        await SeedCamerasAsync();
        var admin = new User { Username = "admin", IsAdmin = true, TenantId = 1 };
        _db.Users.Add(admin);
        await _db.SaveChangesAsync();

        Assert.Equal([1, 2, 3, 4], (await _perms.VisibleCameraIdsAsync(admin.Id)).Order());
        Assert.True(await _perms.HasAsync(admin.Id, Permissions.SystemConfig));
    }

    [Fact]
    public async Task Direitos_de_outro_tenant_nao_vazam()
    {
        await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);

        var user = new User { Username = "ivo", TenantId = 1, Active = true };
        _db.Users.Add(user);
        var group = new UserGroup { Name = "outro", RoleId = role, TenantId = 1 };
        _db.UserGroups.Add(group);
        await _db.SaveChangesAsync();

        _db.UserGroupMembers.Add(new UserGroupMember { UserId = user.Id, GroupId = group.Id });

        // Direito gravado sob outro tenant nao pode valer para este usuario.
        _db.ObjectRights.Add(new ObjectRight
        {
            TenantId = 99,
            SubjectType = SubjectType.Group, SubjectId = group.Id,
            ObjectType = ObjectTypes.Camera, ObjectId = null,
            Permission = Permissions.FromRole, Effect = RightEffect.Allow
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await _perms.VisibleCameraIdsAsync(user.Id));
    }

    [Fact]
    public async Task Explain_diz_de_onde_veio_o_direito()
    {
        var (_, portaria, _) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("julia", role);

        Grant(groupId, ObjectTypes.CameraGroup, portaria, Permissions.FromRole);
        await _db.SaveChangesAsync();

        var explicacao = await _perms.ExplainAsync(userId);
        var view = explicacao.Permissions.First(p => p.Permission == Permissions.CameraView);

        Assert.True(view.Granted);
        Assert.Contains("Portaria", view.Origin);
        Assert.Contains("perfil", view.Origin);
        Assert.Contains("grupo-julia", explicacao.Groups);

        var export = explicacao.Permissions.First(p => p.Permission == Permissions.CameraExport);
        Assert.False(export.Granted);
    }

    [Fact]
    public async Task Previa_do_grupo_bate_com_o_acesso_efetivo_dos_membros()
    {
        var (predio, _, garagem) = await SeedCamerasAsync();
        var role = await SeedRoleAsync("Operador", Permissions.CameraView);
        var (userId, groupId) = await SeedUserInGroupAsync("karina", role);

        Grant(groupId, ObjectTypes.CameraGroup, predio, Permissions.FromRole);
        Grant(groupId, ObjectTypes.CameraGroup, garagem, Permissions.FromRole);
        await _db.SaveChangesAsync();

        var previa = await _perms.CamerasForSubjectAsync(SubjectType.Group, groupId);
        var efetivo = await _perms.VisibleCameraIdsAsync(userId);

        // A previa mostrada antes de salvar precisa ser exatamente o que o
        // membro vai enxergar depois.
        Assert.Equal(efetivo.Order(), previa.Order());
    }
}
