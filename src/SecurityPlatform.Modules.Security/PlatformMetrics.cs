using System.Collections.Concurrent;
using System.Text;

namespace SecurityPlatform.Modules.Security;

/// <summary>Contadores Prometheus em texto (sem pacote externo).</summary>
public sealed class PlatformMetrics
{
    private long _httpRequests;
    private long _loginOk;
    private long _loginFail;
    private long _exports;
    private long _unlocks;
    private long _eventsIn;
    private readonly ConcurrentDictionary<string, long> _custom = new(StringComparer.Ordinal);

    public void IncHttp() => Interlocked.Increment(ref _httpRequests);
    public void IncLogin(bool ok)
    {
        if (ok) Interlocked.Increment(ref _loginOk);
        else Interlocked.Increment(ref _loginFail);
    }
    public void IncExport() => Interlocked.Increment(ref _exports);
    public void IncUnlock() => Interlocked.Increment(ref _unlocks);
    public void IncEvent() => Interlocked.Increment(ref _eventsIn);
    public void Inc(string name, long delta = 1) =>
        _custom.AddOrUpdate(name, delta, (_, v) => v + delta);

    public string RenderPrometheus()
    {
        var sb = new StringBuilder(1024);
        void G(string name, string help, long val)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" counter\n");
            sb.Append(name).Append(' ').Append(val).Append('\n');
        }

        G("sp_http_requests_total", "Requisições HTTP processadas", Interlocked.Read(ref _httpRequests));
        G("sp_login_ok_total", "Logins bem-sucedidos", Interlocked.Read(ref _loginOk));
        G("sp_login_fail_total", "Logins falhos", Interlocked.Read(ref _loginFail));
        G("sp_exports_total", "Exports de gravação", Interlocked.Read(ref _exports));
        G("sp_access_unlocks_total", "Tentativas de unlock SCA", Interlocked.Read(ref _unlocks));
        G("sp_events_ingested_total", "Eventos de dispositivo/MQTT", Interlocked.Read(ref _eventsIn));

        foreach (var (k, v) in _custom.OrderBy(x => x.Key))
        {
            var name = "sp_" + k.Replace('-', '_').Replace('.', '_');
            G(name, "custom " + k, v);
        }

        sb.Append("# HELP sp_process_uptime_seconds Uptime do processo\n");
        sb.Append("# TYPE sp_process_uptime_seconds gauge\n");
        sb.Append("sp_process_uptime_seconds ")
            .Append((DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds.ToString("F0"))
            .Append('\n');

        return sb.ToString();
    }
}
