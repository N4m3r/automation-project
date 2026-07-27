using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Security;

namespace SecurityPlatform.Core.Data;

public class PlatformDbContext(
    DbContextOptions<PlatformDbContext> options,
    ISecretProtector? secrets = null) : DbContext(options)
{
    private readonly ISecretProtector _secrets = secrets ?? new NullSecretProtector();

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceEvent> Events => Set<DeviceEvent>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMember> UserGroupMembers => Set<UserGroupMember>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<ObjectRight> ObjectRights => Set<ObjectRight>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<CameraGroup> CameraGroups => Set<CameraGroup>();
    public DbSet<CameraGroupMember> CameraGroupMembers => Set<CameraGroupMember>();
    public DbSet<MediaProfile> MediaProfiles => Set<MediaProfile>();
    public DbSet<ScheduleSlot> ScheduleSlots => Set<ScheduleSlot>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<EventActionButton> EventActionButtons => Set<EventActionButton>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<IpFilter> IpFilters => Set<IpFilter>();
    public DbSet<MonitorLayout> MonitorLayouts => Set<MonitorLayout>();
    public DbSet<RecorderLease> RecorderLeases => Set<RecorderLease>();
    public DbSet<SynopticMap> SynopticMaps => Set<SynopticMap>();
    public DbSet<MapMarker> MapMarkers => Set<MapMarker>();
    public DbSet<LicensePlateRule> LicensePlateRules => Set<LicensePlateRule>();
    public DbSet<AccessPerson> AccessPeople => Set<AccessPerson>();
    public DbSet<AccessCredential> AccessCredentials => Set<AccessCredential>();
    public DbSet<AccessDoor> AccessDoors => Set<AccessDoor>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    public DbSet<AlarmPanel> AlarmPanels => Set<AlarmPanel>();
    public DbSet<AlarmZone> AlarmZones => Set<AlarmZone>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<AccessPresence> AccessPresences => Set<AccessPresence>();
    public DbSet<AccessVisitor> AccessVisitors => Set<AccessVisitor>();
    public DbSet<AccessSchedule> AccessSchedules => Set<AccessSchedule>();
    public DbSet<FaceGalleryEntry> FaceGalleryEntries => Set<FaceGalleryEntry>();
    public DbSet<AlarmPopTemplate> AlarmPopTemplates => Set<AlarmPopTemplate>();
    public DbSet<ExportRecord> ExportRecords => Set<ExportRecord>();
    public DbSet<RetentionPurgeLog> RetentionPurgeLogs => Set<RetentionPurgeLog>();
    public DbSet<PrivacyMask> PrivacyMasks => Set<PrivacyMask>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>().HasIndex(d => new { d.TenantId, d.Kind });
        b.Entity<DeviceEvent>().HasIndex(e => new { e.TenantId, e.CreatedAt });
        b.Entity<Recording>().HasIndex(r => new { r.DeviceId, r.StartedAt });

        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<UserGroup>().HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        b.Entity<UserGroupMember>().HasIndex(m => new { m.UserId, m.GroupId }).IsUnique();
        b.Entity<ObjectRight>().HasIndex(r => new { r.SubjectType, r.SubjectId, r.ObjectType });
        b.Entity<Role>().HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
        b.Entity<RolePermission>().HasIndex(p => new { p.RoleId, p.Permission }).IsUnique();
        b.Entity<Bookmark>().HasIndex(m => new { m.DeviceId, m.StartedAt });
        b.Entity<AuditLog>().HasIndex(a => new { a.TenantId, a.CreatedAt });
        b.Entity<CameraGroupMember>().HasIndex(m => new { m.GroupId, m.DeviceId }).IsUnique();
        b.Entity<ScheduleSlot>().HasIndex(s => new { s.DeviceId, s.Kind });
        b.Entity<AutomationRule>().HasIndex(r => new { r.TenantId, r.WhenEventType });
        b.Entity<EventActionButton>().HasIndex(x => new { x.TenantId, x.SortOrder });
        b.Entity<MonitorLayout>().HasIndex(m => new { m.UserId, m.Name }).IsUnique();
        b.Entity<RecorderLease>().HasIndex(l => l.ResourceKey).IsUnique();
        b.Entity<SynopticMap>().HasIndex(m => new { m.TenantId, m.Name });
        b.Entity<MapMarker>().HasIndex(m => m.MapId);
        b.Entity<MapMarker>().HasIndex(m => m.DeviceId);
        b.Entity<MapMarker>()
            .HasOne(m => m.Map)
            .WithMany(m => m.Markers)
            .HasForeignKey(m => m.MapId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<LicensePlateRule>().HasIndex(r => new { r.TenantId, r.Plate });
        b.Entity<AccessPerson>().HasIndex(p => new { p.TenantId, p.FullName });
        b.Entity<AccessCredential>()
            .HasOne(c => c.Person)
            .WithMany(p => p.Credentials)
            .HasForeignKey(c => c.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<AccessCredential>().HasIndex(c => new { c.Kind, c.Value });
        b.Entity<AccessDoor>().HasIndex(d => new { d.TenantId, d.Name });
        b.Entity<AccessLog>().HasIndex(l => new { l.TenantId, l.CreatedAt });
        b.Entity<AlarmPanel>().HasIndex(p => new { p.TenantId, p.Account });
        b.Entity<AlarmZone>()
            .HasOne(z => z.Panel)
            .WithMany()
            .HasForeignKey(z => z.PanelId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<AlarmEvent>().HasIndex(e => new { e.TenantId, e.CreatedAt });
        b.Entity<AccessPresence>().HasIndex(p => new { p.TenantId, p.PersonId }).IsUnique();
        b.Entity<AccessVisitor>().HasIndex(v => new { v.TenantId, v.ValidTo });
        b.Entity<AccessSchedule>().HasIndex(s => new { s.TenantId, s.Name });
        b.Entity<FaceGalleryEntry>().HasIndex(f => new { f.TenantId, f.ExternalFaceId });
        b.Entity<AlarmPopTemplate>().HasIndex(t => new { t.TenantId, t.CodePrefix });
        b.Entity<ExportRecord>().HasIndex(e => new { e.TenantId, e.CreatedAt });
        b.Entity<ExportRecord>().HasIndex(e => e.Sha256);
        b.Entity<RetentionPurgeLog>().HasIndex(p => new { p.TenantId, p.PurgedAt });
        b.Entity<PrivacyMask>().HasIndex(m => new { m.DeviceId, m.Enabled });

        // Credenciais de equipamento e de SMTP nunca ficam em claro no banco.
        // O conversor cifra na gravacao e decifra na leitura, entao nenhum
        // driver, endpoint ou consulta precisa saber que ha criptografia.
        var cifrado = new ValueConverter<string, string>(
            claro => _secrets.Protect(claro),
            gravado => _secrets.Unprotect(gravado));

        b.Entity<Device>().Property(d => d.Password).HasConversion(cifrado);
        b.Entity<SystemSettings>().Property(s => s.SmtpPassword).HasConversion(cifrado);

        // Tenant padrao para a instalacao All-in-One funcionar sem configuracao.
        b.Entity<Tenant>().HasData(new Tenant
        {
            Id = 1,
            Name = "Default",
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}
