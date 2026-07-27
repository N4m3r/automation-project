namespace SecurityPlatform.Core.Domain;

/// <summary>
/// Configurações do servidor — linha única (Id = 1).
/// Espelha a área "Configurações → Sistema" de um cliente de administração
/// profissional: geral, gravações, limites de disco, SMTP e retenção de logs.
/// </summary>
public class SystemSettings
{
    public int Id { get; set; } = 1;

    // --- Geral
    public string ServerName { get; set; } = "Servidor Principal";
    public string Description { get; set; } = "";
    public string TimeZone { get; set; } = "America/Sao_Paulo";
    public string Language { get; set; } = "pt-BR";

    // --- Gravações
    public string StorageRoot { get; set; } = "./data/recordings";
    public int DefaultRetentionDays { get; set; } = 7;
    public int SegmentSeconds { get; set; } = 600;
    public bool EncryptRecordings { get; set; }

    /// <summary>Grava marca d'água com usuário e data nos vídeos exportados.</summary>
    public bool WatermarkExport { get; set; } = true;

    /// <summary>
    /// LGPD: aplica blur forte no export (anonimização de faces/corpos no quadro).
    /// Usa FFmpeg boxblur — não é detecção facial ML, mas remove identificação visual.
    /// </summary>
    public bool BlurFacesOnExport { get; set; }

    /// <summary>
    /// Pasta de arquivo frio (NAS/secundário). Vazio = desliga archive.
    /// Gravações mais antigas que <see cref="ArchiveAfterDays"/> são movidas.
    /// </summary>
    public string ArchivePath { get; set; } = "";

    /// <summary>Dias em storage quente antes de mover para archive. 0 = desliga.</summary>
    public int ArchiveAfterDays { get; set; }

    // --- Limites de disco
    /// <summary>Abaixo deste percentual livre, o sistema alerta.</summary>
    public int DiskWarningPercent { get; set; } = 15;

    /// <summary>Abaixo deste percentual livre, apaga as gravações mais antigas.</summary>
    public int DiskCriticalPercent { get; set; } = 5;

    // --- SMTP (notificação de eventos)
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string SmtpFrom { get; set; } = "";

    // --- Nó de mídia
    public string MediaServerApi { get; set; } = "http://localhost:9997";
    public string MediaPublicHost { get; set; } = "http://localhost";

    // --- Logs
    public int SystemLogRetentionDays { get; set; } = 30;
    public int EventLogRetentionDays { get; set; } = 90;
    public int AuditRetentionDays { get; set; } = 365;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum IpFilterMode { Allow, Deny }

/// <summary>
/// Filtro de IP no nível do servidor — complementa a faixa por usuário.
/// Havendo qualquer regra Allow, só os IPs listados entram.
/// </summary>
public class IpFilter
{
    public int Id { get; set; }
    public IpFilterMode Mode { get; set; } = IpFilterMode.Allow;

    /// <summary>IP exato ou CIDR (ex.: 192.168.1.0/24).</summary>
    public string Address { get; set; } = "";

    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
