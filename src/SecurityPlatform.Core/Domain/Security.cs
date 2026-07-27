namespace SecurityPlatform.Core.Domain;

/// <summary>Permissoes granulares do sistema. Usadas em <see cref="ObjectRight"/>.</summary>
public static class Permissions
{
    public const string CameraView     = "camera.view";      // ver ao vivo
    public const string CameraPlayback = "camera.playback";  // reproduzir gravacoes
    public const string CameraExport   = "camera.export";    // exportar video
    public const string CameraPtz      = "camera.ptz";       // controlar PTZ
    public const string CameraConfig   = "camera.config";    // cadastrar/editar camera
    public const string EventAck       = "event.ack";        // tratar eventos
    public const string UserManage     = "user.manage";      // gerir usuarios/grupos
    public const string SystemConfig   = "system.config";    // configuracao do servidor
    public const string AuditView      = "audit.view";       // ver log de auditoria

    /// <summary>
    /// Curinga usado em <see cref="ObjectRight.Permission"/>: "todas as
    /// permissoes do perfil do sujeito". Nao e uma permissao concedivel
    /// diretamente — e resolvido pelo PermissionService na leitura.
    /// </summary>
    public const string FromRole = "*";

    public static readonly string[] All =
    [
        CameraView, CameraPlayback, CameraExport, CameraPtz, CameraConfig,
        EventAck, UserManage, SystemConfig, AuditView
    ];

    /// <summary>Aceita tanto uma permissao conhecida quanto o curinga de perfil.</summary>
    public static bool IsValid(string permission)
        => permission == FromRole || All.Contains(permission);
}

/// <summary>
/// Tipos de objeto sobre os quais um direito pode ser concedido.
/// Conceder em <see cref="CameraGroup"/> e o que evita repetir permissao
/// camera a camera: a camera nova entra no grupo e ja herda tudo.
/// </summary>
public static class ObjectTypes
{
    public const string Camera      = "camera";
    public const string CameraGroup = "cameragroup";

    public static readonly string[] All = [Camera, CameraGroup];
}

public enum SubjectType { User, Group }

/// <summary>Deny sempre vence Allow na resolucao de direitos.</summary>
public enum RightEffect { Allow, Deny }

/// <summary>
/// Perfil nomeado: um conjunto de permissoes reaplicavel. Existe para o
/// administrador escolher "Operador" em vez de marcar permissao por permissao,
/// e para que mudar o perfil se reflita em todos os grupos que o usam.
/// </summary>
public class Role
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Perfis de fabrica nao podem ser editados nem excluidos.</summary>
    public bool BuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RolePermission
{
    public int Id { get; set; }
    public int RoleId { get; set; }
    public string Permission { get; set; } = "";
}

public class User
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    /// <summary>Administrador ignora a checagem de direitos por objeto.</summary>
    public bool IsAdmin { get; set; }

    public bool Active { get; set; } = true;

    /// <summary>Obriga troca de senha no proximo login (senha inicial/reset).</summary>
    public bool MustChangePassword { get; set; }

    // --- 2FA (TOTP)
    public bool TwoFactorEnabled { get; set; }
    public string? TotpSecret { get; set; }

    /// <summary>Faixas de IP permitidas, separadas por ';' (CIDR ou IP). Vazio = qualquer.</summary>
    public string AllowedIpRanges { get; set; } = "";

    // --- Bloqueio por tentativas
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UserGroup
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// Perfil que define <b>o que</b> o grupo pode fazer. Os <see cref="ObjectRight"/>
    /// do grupo definem <b>sobre quais</b> cameras. Separar as duas perguntas e o
    /// que reduz a configuracao a "escolha um perfil e marque os grupos de camera".
    /// </summary>
    public int? RoleId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UserGroupMember
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int GroupId { get; set; }
}

/// <summary>
/// Direito sobre um objeto. <see cref="ObjectId"/> nulo significa "todos os
/// objetos deste tipo" — permite conceder amplo e negar pontualmente.
///
/// <see cref="ObjectType"/> aceita <c>camera</c> (uma camera) ou
/// <c>cameragroup</c> (um grupo inteiro, incluindo subgrupos). O direito em
/// grupo e resolvido na leitura, entao camera adicionada ao grupo depois ja
/// nasce com o acesso — nao ha nada para reconfigurar.
/// </summary>
public class ObjectRight
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public SubjectType SubjectType { get; set; }
    public int SubjectId { get; set; }
    public string ObjectType { get; set; } = ObjectTypes.Camera;
    public int? ObjectId { get; set; }

    /// <summary>
    /// Permissao concedida, ou <see cref="Permissions.FromRole"/> para dizer
    /// "as permissoes do perfil deste sujeito". Guardar o curinga evita gravar
    /// uma linha por permissao × objeto e faz a troca de perfil valer na hora.
    /// </summary>
    public string Permission { get; set; } = "";

    public RightEffect Effect { get; set; } = RightEffect.Allow;
}

/// <summary>Trilha de auditoria: quem fez o que, quando e de onde.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int? UserId { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";      // login, camera.create, export...
    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public bool Success { get; set; } = true;
    public string Detail { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
