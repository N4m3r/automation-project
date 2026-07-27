using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Ajusta a URL RTSP do driver para o canal do perfil de mídia
/// (principal vs substream).
/// </summary>
public static class StreamUrlBuilder
{
    public enum Quality { Main, Sub }

    /// <summary>Canal típico Hikvision: 101 principal, 102 sub.</summary>
    public const int DefaultMainChannel = 101;
    public const int DefaultSubChannel = 102;

    public static string ApplyChannel(string rtspUrl, int? channel)
    {
        if (channel is null or <= 0) return rtspUrl;
        if (string.IsNullOrWhiteSpace(rtspUrl)) return rtspUrl;

        // Hikvision / ISAPI: /Streaming/Channels/101 → /Streaming/Channels/{channel}
        var hik = System.Text.RegularExpressions.Regex.Replace(
            rtspUrl,
            @"(/Streaming/Channels/)\d+",
            $"${{1}}{channel}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (hik != rtspUrl) return hik;

        // Intelbras / Dahua: subtype=0 main, subtype=1 sub (canal …2 → sub)
        var subtype = channel % 10 == 2 ? 1 : 0;
        var dahua = System.Text.RegularExpressions.Regex.Replace(
            rtspUrl,
            @"(subtype=)\d+",
            $"${{1}}{subtype}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (dahua != rtspUrl) return dahua;

        // Axis: streamprofile= ou resolution — se não casar, devolve original
        return rtspUrl;
    }

    /// <summary>
    /// Força substream mesmo sem perfil: 102 (Hik) ou subtype=1 (Dahua).
    /// </summary>
    public static string ApplyQuality(string rtspUrl, Quality quality, int? explicitChannel)
    {
        if (quality == Quality.Main)
            return ApplyChannel(rtspUrl, explicitChannel);

        // Sub: canal explícito do perfil, senão defaults por fabricante na URL
        if (explicitChannel is int ch && ch > 0)
            return ApplyChannel(rtspUrl, ch);

        // Já tem Channels/ — troca para 102
        if (System.Text.RegularExpressions.Regex.IsMatch(rtspUrl, @"/Streaming/Channels/\d+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return ApplyChannel(rtspUrl, DefaultSubChannel);

        // subtype=
        if (rtspUrl.Contains("subtype=", StringComparison.OrdinalIgnoreCase))
            return ApplyChannel(rtspUrl, DefaultSubChannel);

        // URL genérica: tenta anexar subtype se for path realmonitor
        if (rtspUrl.Contains("realmonitor", StringComparison.OrdinalIgnoreCase)
            && !rtspUrl.Contains("subtype=", StringComparison.OrdinalIgnoreCase))
            return rtspUrl + (rtspUrl.Contains('?') ? "&" : "?") + "subtype=1";

        return ApplyChannel(rtspUrl, DefaultSubChannel);
    }

    /// <summary>
    /// Canal do perfil: gravação = principal; live profile = sub preferencial.
    /// </summary>
    public static int? ResolveChannel(
        Device cam,
        IReadOnlyDictionary<int, MediaProfile> profiles,
        bool forLive)
        => ResolveChannel(cam, profiles, forLive ? Quality.Sub : Quality.Main);

    public static int? ResolveChannel(
        Device cam,
        IReadOnlyDictionary<int, MediaProfile> profiles,
        Quality quality)
    {
        if (quality == Quality.Sub)
        {
            // Perfil ao vivo = substream; se não houver, null → ApplyQuality usa 102
            if (cam.LiveProfileId is int liveId && profiles.TryGetValue(liveId, out var live))
                return live.Channel;
            return null;
        }

        // Principal: perfil de gravação, senão live, senão default do driver
        if (cam.RecordingProfileId is int recId && profiles.TryGetValue(recId, out var rec))
            return rec.Channel;
        if (cam.LiveProfileId is int lid && profiles.TryGetValue(lid, out var lp)
            && lp.Channel % 10 != 2) // live profile que não seja sub
            return lp.Channel;
        return null;
    }

    public static Quality ParseQuality(string? q)
        => string.Equals(q, "main", StringComparison.OrdinalIgnoreCase)
            || string.Equals(q, "principal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(q, "primary", StringComparison.OrdinalIgnoreCase)
            ? Quality.Main
            : Quality.Sub;
}
