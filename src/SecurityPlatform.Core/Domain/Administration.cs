namespace SecurityPlatform.Core.Domain;

/// <summary>Agrupamento lógico de câmeras (por prédio, andar, setor).</summary>
public class CameraGroup
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int? ParentId { get; set; }          // permite árvore de grupos
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CameraGroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int DeviceId { get; set; }
}

/// <summary>
/// Perfil de mídia: resolução, codec e taxa usados na gravação e no ao vivo.
/// Separar os perfis permite gravar em alta e monitorar em baixa, que é o que
/// viabiliza grids grandes sem saturar a rede.
/// </summary>
public class MediaProfile
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Codec { get; set; } = "H264";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 15;
    public int BitrateKbps { get; set; } = 2048;

    /// <summary>Canal ISAPI/ONVIF correspondente (101 = principal, 102 = substream).</summary>
    public int Channel { get; set; } = 101;

    public bool IsDefault { get; set; }
}

/// <summary>
/// Lease de gravador para HA ativo/passivo. Quem detém o lease de
/// <c>cam:{id}</c> (ou do recurso global) é o único nó que grava.
/// Renovação periódica; se expirar, outro nó assume.
/// </summary>
public class RecorderLease
{
    public int Id { get; set; }
    /// <summary>Ex.: <c>cam:12</c> ou <c>recorder</c>.</summary>
    public string ResourceKey { get; set; } = "";
    public string NodeId { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Layout/mosaico do cliente de monitoramento, por usuário.
/// CellsJson: array de uids de câmera (ex.: "local:3") alinhado às células do grid.
/// </summary>
public class MonitorLayout
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Identificador do grid no cliente (1x1, 2x2, 3x3…).</summary>
    public string LayoutId { get; set; } = "2x2";
    public string CellsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ScheduleKind { Recording, Event }

/// <summary>
/// Faixa horária de agendamento. <see cref="DayOfWeek"/> nulo vale para todos
/// os dias. Sem nenhuma faixa cadastrada, a câmera grava 24x7.
/// </summary>
public class ScheduleSlot
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int DeviceId { get; set; }
    public ScheduleKind Kind { get; set; } = ScheduleKind.Recording;
    public DayOfWeek? Day { get; set; }
    public TimeSpan Start { get; set; } = TimeSpan.Zero;
    public TimeSpan End { get; set; } = TimeSpan.FromHours(24);
    public bool Enabled { get; set; } = true;
}

public enum LicenseEdition { Express, Professional, Enterprise }

/// <summary>
/// Licença instalada. A contagem efetiva é comparada com o cadastro para
/// bloquear o excedente — ver <c>/api/admin/license</c>.
/// </summary>
public class License
{
    public int Id { get; set; }
    public LicenseEdition Edition { get; set; } = LicenseEdition.Express;
    public string Key { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public int VideoChannels { get; set; } = 4;
    public int AccessPoints { get; set; }
    public int AlarmZones { get; set; }
    public bool Failover { get; set; }
    public bool MultiTenant { get; set; }
    public bool AnalyticsLpr { get; set; }
    public bool AnalyticsFacial { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Contato para notificação de eventos (e-mail/push).</summary>
public class Contact
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool Active { get; set; } = true;
}

public enum EventActionKind
{
    Email, PopupVideo, PlaySound, PtzPreset, HttpRequest,
    OutputRelay, PushNotification, Bookmark,
    /// <summary>Apenas cliente: abre live da câmera do evento.</summary>
    OpenLive,
    /// <summary>Apenas cliente: abre playback da câmera do evento.</summary>
    OpenPlayback,
    /// <summary>Apenas cliente: localiza no mapa sinóptico.</summary>
    OpenMap
}

/// <summary>
/// Motor de automação (IFTTT interno): SE &lt;evento&gt; em &lt;dispositivo&gt;
/// ENTÃO &lt;ações&gt; — com agenda e cooldown (estilo Digifort).
/// </summary>
public class AutomationRule
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";

    public string WhenEventType { get; set; } = "";   // motion, intrusion, alarm_trigger...
    public int? WhenDeviceId { get; set; }            // nulo = qualquer dispositivo
    public int MinSeverity { get; set; } = 1;

    /// <summary>JSON: [{"kind":"PtzPreset","deviceId":2,"preset":3}, ...]</summary>
    public string Actions { get; set; } = "[]";

    /// <summary>
    /// Dias da semana ativos, CSV 0–6 (0=domingo). Vazio ou <c>*</c> = todos.
    /// Ex.: <c>1,2,3,4,5</c> = dias úteis.
    /// </summary>
    public string ScheduleDays { get; set; } = "0,1,2,3,4,5,6";

    /// <summary>Início da janela (HH:mm local). Ex.: 08:00</summary>
    public string ScheduleStart { get; set; } = "00:00";

    /// <summary>Fim da janela (HH:mm local). Ex.: 18:00. Pode cruzar meia-noite.</summary>
    public string ScheduleEnd { get; set; } = "23:59";

    /// <summary>Fuso IANA (ex.: America/Sao_Paulo). Vazio = UTC.</summary>
    public string TimeZone { get; set; } = "America/Sao_Paulo";

    /// <summary>
    /// Timer de anti-repetição: segundos mínimos entre disparos da mesma regra
    /// (0 = sem limite). Evita flood de popup/sirene.
    /// </summary>
    public int CooldownSeconds { get; set; }

    /// <summary>Último disparo efetivo (UTC) — usado no cooldown.</summary>
    public DateTime? LastFiredAt { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Botão de ação sobre eventos (estilo Digifort / cliente de monitoramento).
/// O admin cadastra botões; o operador os vê em cada card de evento e executa
/// ações manuais (ack, tratar, live, relé, HTTP, e-mail, etc.).
/// </summary>
public class EventActionButton
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;

    /// <summary>Texto do botão no posto (ex.: "Confirmar", "Em tratamento").</summary>
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Emoji ou chave de ícone (ex.: ✓ 🔧 📍).</summary>
    public string Icon { get; set; } = "⚡";

    /// <summary>Cor do botão (#RRGGBB).</summary>
    public string Color { get; set; } = "#238636";

    /// <summary>
    /// Tipos de evento onde o botão aparece. "*" ou vazio = todos.
    /// CSV: "motion,intrusion,alarm_trigger".
    /// </summary>
    public string EventTypes { get; set; } = "*";

    /// <summary>Severidade mínima para exibir (1–3). 0/1 = qualquer.</summary>
    public int MinSeverity { get; set; } = 1;

    /// <summary>
    /// JSON de ações no mesmo formato de <see cref="AutomationRule.Actions"/>,
    /// mais kinds de cliente: OpenLive, OpenPlayback, OpenMap.
    /// </summary>
    public string Actions { get; set; } = "[]";

    /// <summary>Marca o evento como reconhecido ao executar.</summary>
    public bool AutoAcknowledge { get; set; }

    /// <summary>open | treating | resolved | vazio (não altera).</summary>
    public string? SetStatus { get; set; }

    /// <summary>Pede confirmação no cliente antes de executar.</summary>
    public bool RequireConfirm { get; set; }

    /// <summary>Pede comentário (nota de tratamento).</summary>
    public bool RequireComment { get; set; }

    public int SortOrder { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
