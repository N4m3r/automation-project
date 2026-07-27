using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SecurityPlatform.Core.Data;

/// <summary>
/// Leva o banco ao schema atual via EF Migrations, no boot.
///
/// <para>
/// Substitui o antigo par <c>EnsureCreated()</c> + <c>SchemaUpgrader</c>. O
/// desafio que sobra e o legado: instalacoes criadas por <c>EnsureCreated</c>
/// tem o schema completo, mas nao tem a tabela <c>__EFMigrationsHistory</c> —
/// para o EF, elas parecem bancos vazios, e <c>Migrate()</c> tentaria recriar
/// tabelas que ja existem e falharia.
/// </para>
///
/// <para>
/// A ponte e adotar a migration inicial como <b>baseline</b>: quando o banco
/// ja tem tabelas mas nenhum historico, cria-se o historico e carimba-se a
/// <c>InitialCreate</c> como aplicada, sem rodar o seu DDL. A partir dai
/// <c>Migrate()</c> aplica apenas o que vier depois. Toda a operacao usa os
/// servicos do EF, entao o SQL sai correto para SQLite e PostgreSQL.
/// </para>
/// </summary>
public static class DatabaseBootstrapper
{
    public static async Task MigrateAsync(
        PlatformDbContext db, ILogger log, CancellationToken ct = default)
    {
        var creator = (RelationalDatabaseCreator)db.GetService<IDatabaseCreator>();

        // Banco pre-existente sem historico de migrations = criado por
        // EnsureCreated. Adota-se a InitialCreate como baseline antes de migrar.
        if (await creator.ExistsAsync(ct))
        {
            var history = db.GetService<IHistoryRepository>();

            if (!await history.ExistsAsync(ct) && await creator.HasTablesAsync(ct))
            {
                var baseline = db.Database.GetMigrations().First(); // menor Id = InitialCreate
                var version = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "8.0.0";

                await db.Database.ExecuteSqlRawAsync(history.GetCreateScript(), ct);
                await db.Database.ExecuteSqlRawAsync(
                    history.GetInsertScript(new HistoryRow(baseline, version)), ct);

                log.LogWarning(
                    "Banco pre-existente (EnsureCreated) adotado como baseline na migration {Baseline}. " +
                    "As proximas migrations serao aplicadas normalmente.", baseline);
            }
        }

        await db.Database.MigrateAsync(ct);
    }
}
