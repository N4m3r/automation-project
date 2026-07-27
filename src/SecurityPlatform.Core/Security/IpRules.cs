using System.Net;
using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Core.Security;

/// <summary>
/// Casamento de endereços IP, usado em dois lugares: a faixa permitida por
/// usuário (no login) e os filtros de IP do servidor. Manter uma única
/// implementação evita que as duas divirjam.
/// </summary>
public static class IpRules
{
    public static bool IsLoopback(string ip)
        => IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr);

    /// <summary>
    /// Aceita IP exato ou CIDR, vários separados por ';'
    /// (ex.: <c>"192.168.1.0/24;10.0.0.5"</c>). Lista vazia libera todos.
    /// </summary>
    public static bool Matches(string ranges, string ip)
    {
        if (string.IsNullOrWhiteSpace(ranges)) return true;
        if (!IPAddress.TryParse(ip, out var addr)) return false;

        foreach (var raw in ranges.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();

            if (entry.Contains('/'))
            {
                if (InCidr(addr, entry)) return true;
            }
            else if (IPAddress.TryParse(entry, out var single) && single.Equals(addr))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Decide o acesso a partir das regras cadastradas.
    ///
    /// - o laço local nunca é bloqueado: sem essa saída, uma regra Allow mal
    ///   digitada trancaria o servidor e o próprio painel que a desfaria;
    /// - qualquer Deny que case bloqueia;
    /// - havendo ao menos um Allow, só os endereços listados entram.
    /// </summary>
    public static bool Allowed(IEnumerable<IpFilter> regras, string ip)
    {
        if (IsLoopback(ip)) return true;

        var ativas = regras.Where(r => r.Enabled).ToList();

        if (ativas.Any(r => r.Mode == IpFilterMode.Deny && Matches(r.Address, ip)))
            return false;

        var permitidos = ativas.Where(r => r.Mode == IpFilterMode.Allow).ToList();
        if (permitidos.Count == 0) return true;          // só havia regras Deny

        return permitidos.Any(r => Matches(r.Address, ip));
    }

    private static bool InCidr(IPAddress addr, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var network)
            || !int.TryParse(parts[1], out var prefix)) return false;

        var addrBytes = addr.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (addrBytes.Length != netBytes.Length) return false;
        if (prefix < 0 || prefix > addrBytes.Length * 8) return false;

        for (var i = 0; i < addrBytes.Length && prefix > 0; i++, prefix -= 8)
        {
            var mask = prefix >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - prefix));
            if ((addrBytes[i] & mask) != (netBytes[i] & mask)) return false;
        }
        return true;
    }
}
