using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Core.Drivers;

/// <summary>
/// Resolve o driver de cada dispositivo. Todos os IDeviceDriver registrados no
/// container entram aqui automaticamente — adicionar fabricante nao exige
/// alterar o registro.
/// </summary>
public class DriverRegistry(IEnumerable<IDeviceDriver> drivers)
{
    private readonly Dictionary<string, IDeviceDriver> _drivers =
        drivers.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maturidade declarada para UI (certificado / parcial / experimental).
    /// </summary>
    private static readonly Dictionary<string, DriverCapability> Caps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision"] = new("certified", true, true, true, "ISAPI nativo — PTZ, eventos, edge, talk"),
        ["onvif"] = new("certified", true, true, true, "Profile S + PTZ SOAP; eventos parciais"),
        ["dahua"] = new("supported", true, true, true, "HTTP API + eventManager + PTZ"),
        ["intelbras"] = new("supported", true, true, true, "Base Dahua / Intelbras"),
        ["axis"] = new("partial", true, true, true, "VAPIX stream/PTZ/eventos parciais"),
        ["uniview"] = new("experimental", true, false, false, "Stream + heartbeat — beta"),
        ["bosch"] = new("experimental", true, false, false, "Stream + heartbeat — beta"),
        ["samsung"] = new("experimental", true, false, false, "Stream + heartbeat — beta"),
        ["http-io"] = new("supported", false, false, false, "Relés / I/O HTTP genérico"),
        ["commbox-mio"] = new("supported", false, false, false, "Commbox Multi I/O nativo"),
    };

    public IDeviceDriver Resolve(Device device)
        => _drivers.TryGetValue(device.Driver, out var d)
            ? d
            : throw new InvalidOperationException(
                $"Driver '{device.Driver}' nao registrado. Disponiveis: {string.Join(", ", _drivers.Keys)}");

    public IEnumerable<object> List() => _drivers.Values
        .Select(d =>
        {
            Caps.TryGetValue(d.Name, out var cap);
            cap ??= new("unknown", true, false, false, "");
            return new
            {
                name = d.Name,
                supports = d.Supports.Select(s => s.ToString()),
                maturity = cap.Maturity,
                stream = cap.Stream,
                ptz = cap.Ptz,
                events = cap.Events,
                note = cap.Note
            };
        });
}

public record DriverCapability(string Maturity, bool Stream, bool Ptz, bool Events, string Note);
