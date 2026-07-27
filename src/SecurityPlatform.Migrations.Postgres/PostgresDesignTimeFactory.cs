using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecurityPlatform.Core.Data;

namespace SecurityPlatform.Migrations.Postgres;

/// <summary>
/// Fabrica de tempo de projeto para gerar as migrations PostgreSQL com
/// <c>dotnet ef</c>. As migrations vivem neste assembly (ver
/// <c>MigrationsAssembly</c>), com identidade e tipos corretos para o Postgres.
///
/// <para>
/// A connection string abaixo nunca e aberta: <c>migrations add</c> nao toca o
/// banco, so precisa do provider para traduzir o modelo em DDL.
/// </para>
/// </summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=securityplatform;Username=postgres",
                npg => npg.MigrationsAssembly(typeof(PostgresDesignTimeFactory).Assembly.GetName().Name))
            .Options;

        return new PlatformDbContext(options);
    }
}
