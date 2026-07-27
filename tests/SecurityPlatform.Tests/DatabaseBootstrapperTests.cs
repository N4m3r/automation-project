using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Tests;

/// <summary>
/// O boot passou a usar EF Migrations. Dois cenarios importam: um banco novo,
/// que nasce da migration; e um banco legado criado por <c>EnsureCreated</c>
/// (schema completo, sem <c>__EFMigrationsHistory</c>), que precisa ser adotado
/// como baseline em vez de recriado. Estes testes cobrem os dois na mesma
/// conexao SQLite em memoria.
/// </summary>
public class DatabaseBootstrapperTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public DatabaseBootstrapperTests()
    {
        _conn = new SqliteConnection("Filename=:memory:");
        _conn.Open();
    }

    public void Dispose()
    {
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    // O MigrationsAssembly precisa ser o mesmo do boot real, senao GetMigrations
    // e Migrate nao enxergam a InitialCreate.
    private PlatformDbContext NovoContexto()
        => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_conn, x => x.MigrationsAssembly("SecurityPlatform.Migrations.Sqlite"))
            .Options);

    [Fact]
    public async Task Banco_novo_e_criado_pela_migration_com_o_tenant_padrao()
    {
        using var db = NovoContexto();

        await DatabaseBootstrapper.MigrateAsync(db, NullLogger.Instance);

        // A migration aplicou tudo, inclusive o seed do Tenant Default.
        Assert.Equal(new[] { "Default" }, await db.Tenants.Select(t => t.Name).ToListAsync());
        Assert.Empty(await db.Roles.ToListAsync());
        Assert.Empty(await db.Bookmarks.ToListAsync());

        // A migration ficou registrada no historico.
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Banco_legado_do_EnsureCreated_e_adotado_como_baseline_sem_recriar()
    {
        // Simula a instalação da era EnsureCreated: schema no estado da
        // InitialCreate, sem __EFMigrationsHistory. EnsureCreated() no modelo
        // atual já teria as colunas novas e quebraria o ALTER da migration
        // seguinte — por isso montamos o legado migrando só a baseline e
        // apagando o histórico.
        using (var legado = NovoContexto())
        {
            await MigrateAteAsync(legado, "20260723143457_InitialCreate");
            // Insert SQL cru: o modelo C# já tem colunas da migration seguinte.
            await legado.Database.ExecuteSqlRawAsync("""
                INSERT INTO Devices
                    (TenantId, Name, Kind, Driver, Host, Port, Username, Password, StreamUrl,
                     Recording, RetentionDays, MaxStorageGb, EventRecordSeconds, Status, CreatedAt)
                VALUES
                    (1, 'Entrada', 0, 'onvif', '10.0.0.5', 80, '', '', '',
                     1, 7, 0, 60, 0, '2024-01-01 00:00:00')
                """);
            await legado.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");
        }

        using var db = NovoContexto();

        // Sem o baseline, Migrate tentaria recriar as tabelas e lancaria aqui.
        await DatabaseBootstrapper.MigrateAsync(db, NullLogger.Instance);

        // Dado antigo preservado; todas as migrations (incluindo as posteriores
        // à baseline) ficam aplicadas.
        Assert.Equal("Entrada", (await db.Devices.SingleAsync()).Name);
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Rodar_o_boot_duas_vezes_nao_falha()
    {
        using (var db = NovoContexto())
            await DatabaseBootstrapper.MigrateAsync(db, NullLogger.Instance);

        using (var db = NovoContexto())
            await DatabaseBootstrapper.MigrateAsync(db, NullLogger.Instance);   // reinicio do servico

        using var final = NovoContexto();
        Assert.Single(await final.Tenants.ToListAsync());   // seed nao duplicou
    }

    [Fact]
    public async Task Banco_legado_continua_gravavel_apos_o_baseline()
    {
        using (var legado = NovoContexto())
        {
            await MigrateAteAsync(legado, "20260723143457_InitialCreate");
            await legado.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");
        }

        using var db = NovoContexto();
        await DatabaseBootstrapper.MigrateAsync(db, NullLogger.Instance);

        var role = new Role { Name = "Operador", BuiltIn = true };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.Bookmarks.Add(new Bookmark
        {
            DeviceId = 1, Title = "Incidente",
            StartedAt = DateTime.UtcNow.AddMinutes(-5), EndedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.True(role.Id > 0);                              // auto-incremento funciona
        Assert.Single(await db.Bookmarks.ToListAsync());
    }

    /// <summary>Aplica migrations até o alvo (inclusive), via IMigrator.</summary>
    private static Task MigrateAteAsync(PlatformDbContext db, string target)
        => db.GetService<IMigrator>().MigrateAsync(target);
}
