using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecurityPlatform.Core.Data;

namespace SecurityPlatform.Migrations.Sqlite;

/// <summary>
/// Fabrica de tempo de projeto para gerar as migrations SQLite com
/// <c>dotnet ef</c>. As migrations vivem neste assembly (ver
/// <c>MigrationsAssembly</c>), separadas das do PostgreSQL — cada provider tem
/// o DDL correto para o seu banco.
///
/// <para>
/// A connection string abaixo nunca e aberta: <c>migrations add</c> nao toca o
/// banco, so precisa do provider para traduzir o modelo em DDL.
/// </para>
/// </summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(
                "Data Source=./data/platform.db",
                sqlite => sqlite.MigrationsAssembly(typeof(SqliteDesignTimeFactory).Assembly.GetName().Name))
            .Options;

        return new PlatformDbContext(options);
    }
}
