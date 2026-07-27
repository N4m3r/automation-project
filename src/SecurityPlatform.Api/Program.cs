using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SecurityPlatform.Core.Data;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Core.Events;
using System.Security.Cryptography.X509Certificates;
using SecurityPlatform.Drivers.Hikvision;
using SecurityPlatform.Drivers.HttpIo;
using SecurityPlatform.Drivers.Onvif;
using SecurityPlatform.Drivers.Vendors;
using SecurityPlatform.Api;
using SecurityPlatform.Modules.Admin;
using SecurityPlatform.Modules.Security;
using SecurityPlatform.Modules.Vms;
// PlatformMetrics lives in Security module

var builder = WebApplication.CreateBuilder(args);

// Identificar o servidor e a versao so ajuda quem procura alvo.
// HTTPS nativo (Security:Https) quando certificado estiver configurado.
builder.WebHost.ConfigureKestrel((ctx, k) =>
{
    k.AddServerHeader = false;

    var https = ctx.Configuration.GetSection("Security:Https");
    if (!https.GetValue("Enabled", false)) return;

    var port = https.GetValue("Port", 8443);
    var certPath = https["CertificatePath"] ?? "";
    var keyPath = https["KeyPath"] ?? "";
    var password = https["CertificatePassword"] ?? "";

    X509Certificate2? cert = null;
    try
    {
        if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath))
        {
            if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            {
                // PEM (crt+key): reexporta PFX em memória — Kestrel no Windows
                // exige chave exportável com esse formato.
                using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                cert = new X509Certificate2(pem.Export(X509ContentType.Pfx));
            }
            else if (certPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                     certPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
            {
                cert = new X509Certificate2(certPath, password,
                    X509KeyStorageFlags.EphemeralKeySet);
            }
            else
            {
                using var pem = X509Certificate2.CreateFromPemFile(certPath);
                cert = new X509Certificate2(pem.Export(X509ContentType.Pfx));
            }
        }
    }
    catch (Exception e)
    {
        throw new InvalidOperationException(
            $"Security:Https habilitado mas o certificado falhou: {e.Message}", e);
    }

    if (cert is null)
        throw new InvalidOperationException(
            "Security:Https:Enabled=true exige CertificatePath (PEM/PFX) válido.");

    k.ListenAnyIP(port, l => l.UseHttps(cert));

    if (https.GetValue("AlsoListenHttp", true))
        k.ListenAnyIP(https.GetValue("HttpPort", 8080));
});

// --- Persistencia: SQLite (Windows/dev) ou PostgreSQL (nuvem), so muda a connection string.
// Paths relativos sempre no ContentRoot (não no CWD do shell).
var contentRootEarly = builder.Environment.ContentRootPath;
Directory.CreateDirectory(Path.Combine(contentRootEarly, "data"));
var cs = builder.Configuration.GetConnectionString("Default") ?? "Data Source=./data/platform.db";
if (!cs.Contains("Host=", StringComparison.OrdinalIgnoreCase))
{
    var m = System.Text.RegularExpressions.Regex.Match(cs, @"Data Source\s*=\s*([^;]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (m.Success)
    {
        var dbPath = m.Groups[1].Value.Trim().Trim('"');
        if (!Path.IsPathRooted(dbPath))
            dbPath = Path.GetFullPath(Path.Combine(contentRootEarly, dbPath));
        cs = "Data Source=" + dbPath;
    }
}
builder.Services.AddDbContext<PlatformDbContext>(o =>
{
    // Cada provider tem o seu proprio conjunto de migrations (DDL correto para
    // o banco): MigrationsAssembly aponta para o assembly certo em cada caso.
    if (cs.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        o.UseNpgsql(cs, npg => npg.MigrationsAssembly("SecurityPlatform.Migrations.Postgres"));
    else
        o.UseSqlite(cs, sqlite => sqlite.MigrationsAssembly("SecurityPlatform.Migrations.Sqlite"));
});

// --- Drivers: adicionar fabricante = adicionar uma linha aqui.
builder.Services.AddSingleton<IDeviceDriver, HikvisionDriver>();   // protocolo nativo (ISAPI)
builder.Services.AddSingleton<IDeviceDriver, OnvifDriver>();       // fallback generico
builder.Services.AddSingleton<IDeviceDriver, DahuaDriver>();
builder.Services.AddSingleton<IDeviceDriver, IntelbrasDriver>();
builder.Services.AddSingleton<IDeviceDriver, AxisDriver>();
builder.Services.AddSingleton<IDeviceDriver, UniviewDriver>();
builder.Services.AddSingleton<IDeviceDriver, BoschDriver>();
builder.Services.AddSingleton<IDeviceDriver, SamsungDriver>();
builder.Services.AddSingleton<IDeviceDriver, HttpIoDriver>();      // relés / I/O HTTP genérico
builder.Services.AddSingleton<IDeviceDriver, CommboxMioDriver>();  // Commbox Multi I/O nativo
builder.Services.AddSingleton<DriverRegistry>();

// --- Barramento de eventos: Redis se Vms:EventBus estiver configurado; senão in-memory.
{
    var eventBusCfg = builder.Configuration["Vms:EventBus"]
                      ?? builder.Configuration.GetSection("Vms")["EventBus"]
                      ?? "";
    if (EventBusRegistration.IsRedisConfigured(eventBusCfg))
    {
        // Options ainda não pós-configurados aqui — RedisEventBus lê IOptions no resolve.
        builder.Services.AddSingleton<IEventBus, RedisEventBus>();
    }
    else
    {
        builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
    }
}

// Overrides de Security/LDAP editaveis pelo admin (reload sem reiniciar processo).
var runtimeSecurityPath = Path.Combine(contentRootEarly, "data", "runtime-security.json");
builder.Configuration.AddJsonFile(runtimeSecurityPath, optional: true, reloadOnChange: true);

// --- Seguranca: autenticacao JWT, direitos por objeto e auditoria.
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));
builder.Services.PostConfigure<SecurityOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.KeyRingPath)) o.KeyRingPath = "./data/keys";
    if (!Path.IsPathRooted(o.KeyRingPath))
        o.KeyRingPath = Path.GetFullPath(Path.Combine(contentRootEarly, o.KeyRingPath));
});
builder.Services.AddSecurityModule();

// --- Endurecimento: criptografia de segredos, limite de tentativas por IP.
builder.Services.AddMemoryCache();
builder.Services.AddHardening(builder.Configuration, builder.Environment);

// --- CORS: o Cliente de Monitoramento pode ser servido por outro no e monitorar
// varios servidores ao mesmo tempo. So as origens listadas em configuracao passam;
// o token vai no cabecalho Authorization, entao nao ha credenciais de cookie.
var clientOrigins = builder.Configuration
    .GetSection($"{SecurityOptions.Section}:AllowedClientOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(o => o.AddPolicy("clients", p =>
{
    if (clientOrigins.Length == 0) p.WithOrigins();          // nenhuma origem externa
    else p.WithOrigins(clientOrigins)
          .WithHeaders("Authorization", "Content-Type")
          .WithMethods("GET", "POST", "PUT", "DELETE");
}));

// --- Modulos funcionais habilitados neste no.
builder.Services.Configure<VmsOptions>(builder.Configuration.GetSection(VmsOptions.Section));
// Paths relativos (./data/…) → ContentRoot da API (não o CWD do shell).
// Sem isto, `dotnet run` a partir da pasta pai grava/lê em outro lugar e o playback 500.
builder.Services.PostConfigure<VmsOptions>(o =>
{
    o.StoragePath = StoragePaths.ResolveRoot(o.StoragePath, contentRootEarly);
    if (!Path.IsPathRooted(o.ExportPath))
        o.ExportPath = Path.GetFullPath(Path.Combine(contentRootEarly, o.ExportPath));
    // Volumes extras absolutos
    if (o.StorageVolumes is { Length: > 0 })
    {
        o.StorageVolumes = o.StorageVolumes
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.IsPathRooted(v) ? Path.GetFullPath(v)
                : Path.GetFullPath(Path.Combine(contentRootEarly, v.Trim())))
            .ToArray();
        foreach (var v in o.StorageVolumes)
            Directory.CreateDirectory(v);
    }
    Directory.CreateDirectory(o.StoragePath);
    Directory.CreateDirectory(o.ExportPath);
});
builder.Services.AddVmsModule();
builder.Services.AddAdminModule();

// Enums como texto ("Online", "Deny") em vez de indices: contrato estavel
// mesmo que a ordem dos membros mude.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// A documentacao descreve toda a superficie da API. Fora de desenvolvimento
// ela so sobe se alguem habilitar explicitamente.
var swaggerHabilitado = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Security:EnableSwagger", false);

if (swaggerHabilitado)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new OpenApiInfo { Title = "Plataforma Unificada de Seguranca", Version = "v1" });
        o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Cole aqui o token retornado por POST /api/auth/login"
        });
        o.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            }] = []
        });
    });
}

var app = builder.Build();

// Recusa subir com a chave de assinatura de exemplo fora de desenvolvimento.
app.ValidateSecurityConfiguration();

// Leva o banco ao schema atual via EF Migrations. Bancos legados criados por
// EnsureCreated (sem __EFMigrationsHistory) sao adotados como baseline, sem
// recriar o schema. Funciona igual em SQLite e PostgreSQL.
using (var scope = app.Services.CreateScope())
{
    Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await DatabaseBootstrapper.MigrateAsync(db,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Schema"));
}
await app.Services.SeedSecurityAsync();
await app.Services.MigrateSecretsAsync();

// Lock de storage (cluster.uuid) — impede purga cruzada entre ambientes.
try
{
    app.Services.GetRequiredService<StorageClusterLock>().EnsureAndValidate();
}
catch (Exception e)
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StorageCluster");
    log.LogCritical(e, "Falha no lock de storage — abortando boot");
    throw;
}

// --- Pipeline. A ordem importa: erro generico primeiro, para cobrir todo o resto.
app.UseGenericErrors();
app.UseSecurityHeaders(app.Configuration["Vms:MediaPublicHost"] ?? "http://localhost");

// Filtro de IP no nivel do servidor, antes de servir qualquer conteudo.
app.UseMiddleware<IpFilterMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.UseCors("clients");
app.UseRateLimiter();

app.Use(async (ctx, next) =>
{
    try { ctx.RequestServices.GetService<PlatformMetrics>()?.IncHttp(); } catch { /* */ }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

if (swaggerHabilitado)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Sonda de disponibilidade: responde o minimo necessario, sem revelar
// versao, maquina ou horario interno.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Prometheus text exposition (scrape em rede confiada; restrinja no proxy em produção).
app.MapGet("/metrics", (PlatformMetrics m, VmsMetrics vms) =>
    Results.Text(m.RenderPrometheus() + vms.RenderPrometheus(),
        "text/plain; version=0.0.4; charset=utf-8"));

app.MapGet("/api/drivers", (DriverRegistry r) => r.List()).RequireAuthorization("admin");

app.MapMediaAuth();
app.MapSecurityModule();
app.MapVmsModule();
app.MapAdminModule();

// Eventos em tempo real para o painel do operador (token via querystring:
// a API de WebSocket do navegador nao permite cabecalho Authorization).
app.Map("/ws/events", async (HttpContext ctx, IEventBus bus) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    // Mesma convencao camelCase dos endpoints REST.
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await foreach (var evt in bus.SubscribeAsync(ctx.RequestAborted))
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(evt, jsonOptions);
        await ws.SendAsync(json, System.Net.WebSockets.WebSocketMessageType.Text,
            true, ctx.RequestAborted);
    }
}).RequireAuthorization();

app.Run();
