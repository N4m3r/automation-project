using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Core.Drivers;

/// <summary>
/// Contrato unico de driver — a camada que torna a plataforma agnostica.
/// Para suportar um novo fabricante basta implementar esta interface e
/// registra-la no <see cref="DriverRegistry"/>. Nada no core muda.
/// </summary>
public interface IDeviceDriver
{
    /// <summary>Chave usada no campo Device.Driver ("onvif", "hikvision"...).</summary>
    string Name { get; }

    /// <summary>Tipos de dispositivo que este driver atende.</summary>
    DeviceKind[] Supports { get; }

    /// <summary>Valida alcance/credenciais. True = online.</summary>
    Task<bool> ConnectAsync(Device device, CancellationToken ct = default);

    /// <summary>URL RTSP pronta para o gateway de midia consumir.</summary>
    Task<string> GetStreamUrlAsync(Device device, CancellationToken ct = default);

    /// <summary>
    /// Comandos: snapshot, open_door, arm, relay_on, device_info, reboot...
    ///
    /// Vocabulario PTZ comum a todos os drivers:
    /// <list type="bullet">
    /// <item><c>ptz_move</c> — <c>pan</c>, <c>tilt</c> e <c>zoom</c> como
    /// velocidade <b>normalizada em -1..1</b> (0 = parado) e <c>timeout</c> em
    /// segundos. Cada driver converte para a escala do fabricante; normalizar
    /// aqui e o que permite ao cliente falar com qualquer camera do mesmo jeito.</item>
    /// <item><c>ptz_stop</c> — interrompe o movimento continuo.</item>
    /// <item><c>ptz_preset</c> — vai para o preset em <c>preset</c>.</item>
    /// <item><c>ptz_preset_set</c> — grava a posicao atual em <c>preset</c>,
    /// com rotulo opcional em <c>name</c>.</item>
    /// <item><c>ptz_preset_list</c> — devolve os presets como id → nome.</item>
    /// </list>
    /// </summary>
    Task<DriverResult> CommandAsync(Device device, string action,
        IDictionary<string, string>? parameters = null, CancellationToken ct = default);

    /// <summary>Stream de eventos vindos do hardware.</summary>
    IAsyncEnumerable<DeviceEvent> StreamEventsAsync(Device device, CancellationToken ct = default);
}

public record DriverResult(bool Ok, string? Error = null, IDictionary<string, string>? Data = null)
{
    public static DriverResult Success(IDictionary<string, string>? data = null) => new(true, null, data);
    public static DriverResult Fail(string error) => new(false, error);
}
