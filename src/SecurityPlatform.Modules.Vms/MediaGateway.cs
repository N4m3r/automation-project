using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Publica o RTSP da camera como WebRTC/HLS via MediaMTX.
/// Paths: <c>cam{id}</c> principal (pull permanente — preferencialmente o único
/// RTSP nativo), <c>cam{id}s</c> sub (opcional), <c>cam{id}tc</c> transcoder H.264
/// (publisher local, sem sessão extra na câmera).
/// </summary>
public class MediaGateway(HttpClient http, IOptions<VmsOptions> options, ILogger<MediaGateway> log)
{
    private readonly VmsOptions _opt = options.Value;

    /// <summary>
    /// Cache local do que já registramos — evita PATCH a cada motion/reconcile
    /// (reload em massa do MediaMTX no Windows tem sido instável).
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _registered = new(StringComparer.Ordinal);

    private readonly object _listLock = new();
    private HashSet<string> _knownPaths = new(StringComparer.Ordinal);
    private DateTime _knownPathsAt = DateTime.MinValue;

    public static string PathName(int deviceId, bool substream = false)
        => substream ? $"cam{deviceId}s" : $"cam{deviceId}";

    /// <summary>
    /// Cria ou atualiza o path no MediaMTX (idempotente).
    /// <para>
    /// Principal: pull <b>permanente</b> na câmera (1 sessão RTSP). Live e
    /// gravador consomem o path local — evita SETUP 500 por esgotar streams
    /// da Hikvision (gravador + WHEP + HLS + sub + retries).
    /// </para>
    /// Substream: ainda on-demand (só quando o grid pede).
    /// </summary>
    public async Task<bool> RegisterAsync(
        int deviceId, string rtspUrl, bool substream = false, CancellationToken ct = default)
    {
        var name = PathName(deviceId, substream);
        var signature = $"{(substream ? "sub" : "main")}|{rtspUrl}";

        // Cache local: só confia se o path ainda existe no MediaMTX.
        // Após restart do MediaMTX o cache mentia e o live ficava forever offline.
        if (_registered.TryGetValue(name, out var prev) && prev == signature)
        {
            if (await PathConfigExistsAsync(name, ct))
                return true;
            _registered.TryRemove(name, out _);
        }

        // Main: sempre puxando — uma sessão estável na câmera.
        // Sub: on-demand com closeAfter maior (evita flapping SETUP 500).
        var body = substream
            ? (object)new
            {
                source = rtspUrl,
                sourceOnDemand = true,
                sourceOnDemandStartTimeout = "15s",
                sourceOnDemandCloseAfter = "60s",
                rtspTransport = "tcp"
            }
            : new
            {
                source = rtspUrl,
                sourceOnDemand = false,
                sourceOnDemandStartTimeout = "20s",
                sourceOnDemandCloseAfter = "30s",
                rtspTransport = "tcp"
            };

        try
        {
            var add = await http.PostAsJsonAsync(
                $"{_opt.MediaMtxApi}/v3/config/paths/add/{name}", body, ct);
            if (add.IsSuccessStatusCode)
            {
                _registered[name] = signature;
                RememberPath(name);
                return true;
            }

            // Path já existe: se a assinatura (URL) é a mesma, NÃO faz patch —
            // cada patch recarrega o source e reabre RTSP na câmera.
            if (_registered.TryGetValue(name, out var same) && same == signature)
            {
                RememberPath(name);
                return true;
            }

            var patch = await http.PatchAsJsonAsync(
                $"{_opt.MediaMtxApi}/v3/config/paths/patch/{name}", body, ct);
            if (patch.IsSuccessStatusCode)
            {
                _registered[name] = signature;
                RememberPath(name);
                return true;
            }

            // Add 400 + patch falhou, mas path pode existir com a URL certa
            // (ex.: após restart da API com MediaMTX ainda de pé).
            if ((int)add.StatusCode is 400 or 409)
            {
                _registered[name] = signature;
                RememberPath(name);
                return true;
            }

            log.LogWarning("MediaMTX recusou o path {Path}: add={Add} patch={Patch}",
                name, (int)add.StatusCode, (int)patch.StatusCode);
            _registered.TryRemove(name, out _);
            return false;
        }
        catch (HttpRequestException e)
        {
            log.LogWarning("MediaMTX indisponivel: {Message}", e.Message);
            InvalidateCache();
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("MediaMTX timeout ao registrar {Path}", name);
            InvalidateCache();
            return false;
        }
    }

    public async Task<bool> RegisterPublisherAsync(string pathName, CancellationToken ct = default)
    {
        if (_registered.TryGetValue(pathName, out var prev) && prev == "publisher")
            return true;

        var body = new { source = "publisher", sourceOnDemand = false };
        try
        {
            var add = await http.PostAsJsonAsync(
                $"{_opt.MediaMtxApi}/v3/config/paths/add/{pathName}", body, ct);
            if (add.IsSuccessStatusCode)
            {
                _registered[pathName] = "publisher";
                RememberPath(pathName);
                return true;
            }
            if (_registered.TryGetValue(pathName, out var same) && same == "publisher")
            {
                RememberPath(pathName);
                return true;
            }
            var patch = await http.PatchAsJsonAsync(
                $"{_opt.MediaMtxApi}/v3/config/paths/patch/{pathName}", body, ct);
            if (patch.IsSuccessStatusCode || (int)add.StatusCode is 400 or 409)
            {
                _registered[pathName] = "publisher";
                RememberPath(pathName);
                return true;
            }
            _registered.TryRemove(pathName, out _);
            return false;
        }
        catch (HttpRequestException e)
        {
            log.LogWarning("MediaMTX indisponivel (publisher): {Message}", e.Message);
            InvalidateCache();
            return false;
        }
    }

    /// <summary>Após crash/restart do MediaMTX, o cache local mente — limpa tudo.</summary>
    public void InvalidateCache()
    {
        _registered.Clear();
        lock (_listLock)
        {
            _knownPaths = new HashSet<string>(StringComparer.Ordinal);
            _knownPathsAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Confere se o path ainda existe no MediaMTX (lista com cache 5s).
    /// Não usa GET individual — versões do MediaMTX diferem no código de “não existe”
    /// e um falso negativo gerava PATCH em loop (reinício do RTSP = multi-acesso).
    /// </summary>
    private async Task<bool> PathConfigExistsAsync(string name, CancellationToken ct)
    {
        lock (_listLock)
        {
            if ((DateTime.UtcNow - _knownPathsAt).TotalSeconds < 5 && _knownPaths.Count > 0)
                return _knownPaths.Contains(name);
        }

        var names = await ListPathNamesAsync(ct);
        lock (_listLock)
        {
            _knownPaths = names.ToHashSet(StringComparer.Ordinal);
            _knownPathsAt = DateTime.UtcNow;
            return _knownPaths.Contains(name);
        }
    }

    public Task RemoveAsync(int deviceId, CancellationToken ct = default)
        => Task.WhenAll(
            RemovePathAsync(PathName(deviceId, substream: false), ct),
            RemovePathAsync(PathName(deviceId, substream: true), ct));

    public async Task RemovePathAsync(string name, CancellationToken ct = default)
    {
        _registered.TryRemove(name, out _);
        lock (_listLock) _knownPaths.Remove(name);
        try
        {
            var res = await http.DeleteAsync(
                $"{_opt.MediaMtxApi}/v3/config/paths/delete/{name}", ct);
            if (!res.IsSuccessStatusCode && (int)res.StatusCode != 404)
                log.LogWarning("Falha ao remover path {Path}: HTTP {Status}", name, (int)res.StatusCode);
        }
        catch (HttpRequestException) { /* path pode nao existir */ }
    }

    private void RememberPath(string name)
    {
        lock (_listLock)
        {
            _knownPaths.Add(name);
            if (_knownPathsAt == DateTime.MinValue)
                _knownPathsAt = DateTime.UtcNow;
        }
    }

    /// <summary>Sonda leve: lista paths (ou falha se API caído).</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var res = await http.GetAsync($"{_opt.MediaMtxApi}/v3/config/paths/list", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> ListPathNamesAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await http.GetFromJsonAsync<PathList>(
                $"{_opt.MediaMtxApi}/v3/config/paths/list", ct);
            return res?.Items.Select(i => i.Name).ToList() ?? [];
        }
        catch (HttpRequestException e)
        {
            log.LogWarning("MediaMTX indisponivel ao listar paths: {Message}", e.Message);
            return [];
        }
    }

    public Task<bool> IsReadyAsync(int deviceId, bool substream = false, CancellationToken ct = default)
        => IsPathReadyAsync(PathName(deviceId, substream), ct);

    public async Task<bool> IsPathReadyAsync(string pathName, CancellationToken ct = default)
    {
        try
        {
            var res = await http.GetFromJsonAsync<PathState>(
                $"{_opt.MediaMtxApi}/v3/paths/get/{pathName}", ct);
            return res?.Ready ?? false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private record PathList(List<PathItem> Items);
    private record PathItem(string Name);
    private record PathState(string Name, bool Ready);

    public StreamUrls UrlsFor(int deviceId, string rtspUrl, string? publicHost = null, bool substream = false)
    {
        var name = PathName(deviceId, substream);
        var host = (publicHost ?? _opt.MediaPublicHost).TrimEnd('/');
        return new StreamUrls(
            Rtsp: rtspUrl,
            Hls: $"{host}:{_opt.HlsPort}/{name}/index.m3u8",
            WebRtc: $"{host}:{_opt.WebRtcPort}/{name}");
    }

    /// <summary>
    /// RTSP local no MediaMTX (All-in-One). Gravador e ferramentas no servidor
    /// leem daqui em vez de abrir segunda sessão na câmera.
    /// </summary>
    public string LocalRtspUrl(int deviceId, bool substream = false)
    {
        var host = string.IsNullOrWhiteSpace(_opt.MediaMtxRtspHost)
            ? "127.0.0.1"
            : _opt.MediaMtxRtspHost.Trim();
        var port = _opt.MediaMtxRtspPort > 0 ? _opt.MediaMtxRtspPort : 8554;
        return $"rtsp://{host}:{port}/{PathName(deviceId, substream)}";
    }
}

public record StreamUrls(string Rtsp, string Hls, string WebRtc);
