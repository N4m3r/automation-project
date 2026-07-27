namespace SecurityPlatform.Core.Domain;

public enum DeviceKind { Camera, AccessPoint, AlarmPanel }

public enum DeviceStatus { Unknown, Online, Offline }

public enum RecordingMode { Off, Continuous, OnEvent }

/// <summary>Cliente/condominio. Base do Multi-Tenant do GMC.</summary>
public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Dispositivo generico dos tres dominios. <see cref="Driver"/> aponta para a
/// implementacao registrada no DriverRegistry — e o que mantem o core
/// agnostico de fabricante.
/// </summary>
public class Device
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Camera;
    public string Driver { get; set; } = "onvif";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 80;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Se preenchido, ignora a montagem automatica de URL do driver.</summary>
    public string StreamUrl { get; set; } = "";

    public RecordingMode Recording { get; set; } = RecordingMode.Continuous;
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Teto de disco por camera, em GB. 0 = sem limite. A retencao apaga o mais
    /// antigo ao estourar — sem isso so o prazo limita, e o disco enche.
    /// </summary>
    public int MaxStorageGb { get; set; }

    /// <summary>
    /// Gravacao por evento: quantos segundos continuar gravando depois do
    /// ultimo evento antes de encerrar o processo.
    /// </summary>
    public int EventRecordSeconds { get; set; } = 60;

    /// <summary>
    /// Segundos de pré-alarme (ring buffer) no modo OnEvent.
    /// 0 = desliga (só grava após o evento). Padrão 15.
    /// </summary>
    public int PreEventSeconds { get; set; } = 15;

    /// <summary>
    /// Perfil de mídia usado na gravação (canal principal em geral).
    /// Nulo = canal padrão do driver (ex.: 101).
    /// </summary>
    public int? RecordingProfileId { get; set; }

    /// <summary>
    /// Perfil de mídia do ao vivo (substream em grids grandes).
    /// Nulo = mesmo stream da gravação.
    /// </summary>
    public int? LiveProfileId { get; set; }

    /// <summary>Inclui trilha de áudio na gravação quando a câmera envia.</summary>
    public bool RecordAudio { get; set; } = true;

    /// <summary>
    /// Ao detectar buracos na gravação contínua, tenta puxar o trecho da SD
    /// (edge / Profile G light) via RTSP de playback do fabricante.
    /// </summary>
    public bool EdgePullEnabled { get; set; }

    public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
    public DateTime? LastSeen { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Evento unificado (video, acesso, alarme).</summary>
public class DeviceEvent
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int? DeviceId { get; set; }
    public string Type { get; set; } = "";       // motion, video_loss, door_opened...
    public int Severity { get; set; } = 1;       // 1=info 2=warn 3=critical
    public string Payload { get; set; } = "{}";  // JSON livre
    public bool Acknowledged { get; set; }

    /// <summary>open | treating | resolved — fluxo de tratamento (estilo Digifort).</summary>
    public string TreatmentStatus { get; set; } = "open";

    public string? TreatmentNote { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Recording
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int DeviceId { get; set; }
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public string Trigger { get; set; } = "continuous";

    /// <summary>
    /// Gravacao de incidente: a retencao automatica nao apaga. E o que impede
    /// a prova de um evento sumir porque o prazo venceu.
    /// </summary>
    public bool Protected { get; set; }

    /// <summary>
    /// Segmento cifrado em repouso (.mp4.enc). Playback/export decifram sob demanda.
    /// </summary>
    public bool Encrypted { get; set; }
}

/// <summary>
/// Marcacao de um trecho relevante na linha do tempo. Protege as gravacoes que
/// cobrem o intervalo enquanto existir.
/// </summary>
public class Bookmark
{
    public long Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int DeviceId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Cadeia de custódia de exportação forense (hash + assinatura).</summary>
public class ExportRecord
{
    public long Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int DeviceId { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string FileName { get; set; } = "";
    public string? FilePath { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>SHA-256 hex do MP4 exportado.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>HMAC-SHA256 Base64 (sidecar .sig).</summary>
    public string? Signature { get; set; }
    public bool Watermark { get; set; }
    public bool BlurFaces { get; set; }
    public int SegmentCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Log imutável de purge de retenção (LGPD auditável).</summary>
public class RetentionPurgeLog
{
    public long Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int DeviceId { get; set; }
    public long? RecordingId { get; set; }
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime StartedAt { get; set; }
    public string Reason { get; set; } = ""; // retention_days | camera_quota | global_quota | prebuffer | archive
    public DateTime PurgedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Máscara de privacidade por câmera (polígonos normalizados 0–1).
/// JSON: [{"points":[[x,y],...]}, ...]
/// </summary>
public class PrivacyMask
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int DeviceId { get; set; }
    public string Name { get; set; } = "mask";
    public string PolygonsJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
