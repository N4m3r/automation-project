namespace SecurityPlatform.Core.Domain;

// ---------------------------------------------------------------------------
// Analytics / LPR (tecnologias embarcadas)
// ---------------------------------------------------------------------------

/// <summary>Lista de placas: allow (VIP) ou deny (bloqueio / alerta).</summary>
public class LicensePlateRule
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    /// <summary>Placa normalizada (sem espaços/hífen, upper).</summary>
    public string Plate { get; set; } = "";
    /// <summary>allow | deny | watch</summary>
    public string ListType { get; set; } = "watch";
    public string OwnerName { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}

// ---------------------------------------------------------------------------
// Controle de Acesso (SCA) — MVP
// ---------------------------------------------------------------------------

public class AccessPerson
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string FullName { get; set; } = "";
    public string Document { get; set; } = "";
    public string Company { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>Horário de acesso (null = 24h).</summary>
    public int? ScheduleId { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<AccessCredential> Credentials { get; set; } = [];
}

public class AccessCredential
{
    public int Id { get; set; }
    public int PersonId { get; set; }
    public AccessPerson? Person { get; set; }
    /// <summary>card | pin | qr | plate | face</summary>
    public string Kind { get; set; } = "card";
    public string Value { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
}

public class AccessDoor
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    /// <summary>Device de I/O ou câmera com relé.</summary>
    public int? DeviceId { get; set; }
    public string RelayAction { get; set; } = "relay_on";
    public int UnlockSeconds { get; set; } = 5;
    public bool AntiPassback { get; set; }
    /// <summary>in | out | both — sentido do fluxo para anti-passback.</summary>
    public string Direction { get; set; } = "both";
    public string ZoneFrom { get; set; } = "outside";
    public string ZoneTo { get; set; } = "inside";
    /// <summary>Eclusa: outra porta que precisa estar fechada.</summary>
    public int? InterlockWithDoorId { get; set; }
    public bool InterlockRequireClosed { get; set; }
    /// <summary>Estado lógico da porta (atualizado no unlock / force close).</summary>
    public bool IsOpen { get; set; }
    public DateTime? OpenUntil { get; set; }
    /// <summary>Horário em que a porta aceita unlock (null = 24h).</summary>
    public int? ScheduleId { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Janela de horário SCA: dias da semana + faixa HH:mm no fuso informado.
/// Aplicável a porta e/ou pessoa (AND se ambos tiverem schedule).
/// </summary>
public class AccessSchedule
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    /// <summary>Dias 0=dom … 6=sáb, separados por vírgula (ex.: 1,2,3,4,5).</summary>
    public string DaysOfWeek { get; set; } = "1,2,3,4,5";
    /// <summary>Início inclusivo HH:mm.</summary>
    public string StartHm { get; set; } = "08:00";
    /// <summary>Fim exclusivo HH:mm (22:00 = até 21:59).</summary>
    public string EndHm { get; set; } = "18:00";
    public string TimeZone { get; set; } = "America/Sao_Paulo";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Posição da pessoa nas zonas (anti-passback).</summary>
public class AccessPresence
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int PersonId { get; set; }
    public string CurrentZone { get; set; } = "outside";
    public int? LastDoorId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class AccessVisitor
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string FullName { get; set; } = "";
    public string HostName { get; set; } = "";
    public string CredentialValue { get; set; } = "";
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    public DateTime ValidTo { get; set; } = DateTime.UtcNow.AddHours(8);
    public bool Active { get; set; } = true;
    public string Notes { get; set; } = "";
}

public class AccessLog
{
    public long Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int? DoorId { get; set; }
    public int? PersonId { get; set; }
    public string CredentialValue { get; set; } = "";
    public string Result { get; set; } = ""; // granted | denied
    public string Reason { get; set; } = "";
    public string ZoneAfter { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Galeria facial (módulo licenciado <c>AnalyticsFacial</c>).
/// Match por faceId externo da câmera e/ou por fingerprint visual leve.
/// </summary>
public class FaceGalleryEntry
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    /// <summary>ID facial do fabricante (ISAPI/VAPIX) quando houver.</summary>
    public string ExternalFaceId { get; set; } = "";
    /// <summary>URL externa ou data URL da foto de referência.</summary>
    public string PhotoUrl { get; set; } = "";
    /// <summary>Caminho relativo da foto no storage local (data/faces/...).</summary>
    public string PhotoPath { get; set; } = "";
    /// <summary>
    /// Fingerprint visual compacto (Base64 de float32[] normalizado).
    /// Gerado no enroll/search — comparação por similaridade de cosseno.
    /// </summary>
    public string EmbeddingJson { get; set; } = "";
    /// <summary>allow | deny | watch — lista de interesse (como LPR).</summary>
    public string ListType { get; set; } = "watch";
    public string Notes { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

// ---------------------------------------------------------------------------
// Alarmes (SIA / Contact ID) — MVP
// ---------------------------------------------------------------------------

public class AlarmPanel
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    /// <summary>Account number SIA (ex. 1234).</summary>
    public string Account { get; set; } = "";
    public string Protocol { get; set; } = "SIA-DC09";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AlarmZone
{
    public int Id { get; set; }
    public int PanelId { get; set; }
    public AlarmPanel? Panel { get; set; }
    public string ZoneCode { get; set; } = "";
    public string Name { get; set; } = "";
    public int? CameraId { get; set; }
    public int? MapId { get; set; }
    public string Notes { get; set; } = "";
}

public class AlarmEvent
{
    public long Id { get; set; }
    public int TenantId { get; set; } = 1;
    public int? PanelId { get; set; }
    public string Account { get; set; } = "";
    public string Code { get; set; } = "";
    public string Zone { get; set; } = "";
    public string Raw { get; set; } = "";
    public int Severity { get; set; } = 2;
    public bool Acknowledged { get; set; }
    /// <summary>open | treating | resolved</summary>
    public string Status { get; set; } = "open";
    public int? AssignedUserId { get; set; }
    public string TreatmentNotes { get; set; } = "";
    /// <summary>JSON array de índices de passos POP concluídos.</summary>
    public string PopProgressJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>POP — procedimento operacional por código de alarme.</summary>
public class AlarmPopTemplate
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    /// <summary>Prefixo/código SIA (ex. BA, FA, *).</summary>
    public string CodePrefix { get; set; } = "*";
    public string Title { get; set; } = "";
    /// <summary>JSON array de strings com os passos.</summary>
    public string StepsJson { get; set; } = "[]";
    public bool Active { get; set; } = true;
}
