using SecurityPlatform.Core.Domain;

namespace SecurityPlatform.Modules.Vms;

public class VmsOptions
{
    public const string Section = "Vms";

    /// <summary>API de controle do MediaMTX (registro dinamico de paths).</summary>
    public string MediaMtxApi { get; set; } = "http://localhost:9997";

    /// <summary>Host publico do no de midia, visto pelo navegador.</summary>
    public string MediaPublicHost { get; set; } = "http://localhost";

    public int HlsPort { get; set; } = 8888;
    public int WebRtcPort { get; set; } = 8889;

    /// <summary>Host RTSP do MediaMTX visto pelo gravador (loopback no All-in-One).</summary>
    public string MediaMtxRtspHost { get; set; } = "127.0.0.1";

    /// <summary>Porta RTSP do MediaMTX (padrão 8554).</summary>
    public int MediaMtxRtspPort { get; set; } = 8554;

    /// <summary>
    /// Se true, o FFmpeg grava de <c>rtsp://MediaMTX/camN</c> (1 pull na câmera).
    /// Se false, grava direto na câmera (comportamento antigo — pode esgotar sessões).
    /// </summary>
    public bool RecordFromMediaGateway { get; set; } = true;

    /// <summary>
    /// Se true (padrão), a plataforma mantém no máximo <b>1 pull RTSP nativo</b>
    /// por câmera (path main no MediaMTX). Live, gravador e transcoder leem do
    /// gateway. Substream nativo (<c>camNs</c>) não é aberto — evita
    /// “acesso negado / multi-connection” em Hikvision e similares.
    /// </summary>
    public bool SingleCameraRtspPull { get; set; } = true;

    /// <summary>
    /// Se false (padrão), o gravador <b>não</b> abre RTSP direto na câmera
    /// quando o MediaMTX ainda não está ready — tenta de novo no próximo ciclo.
    /// Só ligue se o gateway estiver permanentemente indisponível.
    /// </summary>
    public bool AllowDirectCameraRecord { get; set; }

    /// <summary>
    /// Segundos máximos aguardando o path MediaMTX ficar ready antes de gravar.
    /// </summary>
    public int MediaGatewayReadyTimeoutSeconds { get; set; } = 25;

    /// <summary>Raiz das gravacoes (pasta local ou volume montado).</summary>
    public string StoragePath { get; set; } = "./data/recordings";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Duracao de cada arquivo de gravacao, em segundos.</summary>
    public int SegmentSeconds { get; set; } = 600;

    public bool RecorderEnabled { get; set; } = true;

    /// <summary>
    /// Sharding horizontal: com N nos, cada um assume as cameras cujo
    /// Id % ShardCount == ShardIndex. Escalar = subir mais instancias.
    ///
    /// Vale para gravacao, assinatura de eventos e retencao. Sem isso, dois nos
    /// gravariam a mesma camera, duplicariam evento no banco e disputariam a
    /// exclusao dos mesmos arquivos.
    /// </summary>
    public int ShardIndex { get; set; } = 0;
    public int ShardCount { get; set; } = 1;

    /// <summary>
    /// Teto global de disco, em GB. 0 = sem limite. Aplicado depois da cota por
    /// camera: mesmo com prazos curtos, o volume pode encher.
    /// </summary>
    public int MaxStorageGb { get; set; }

    /// <summary>Pasta dos vídeos exportados (recortes de intervalo).</summary>
    public string ExportPath { get; set; } = "./data/exports";

    /// <summary>Teto de duracao de uma exportacao, em minutos.</summary>
    public int MaxExportMinutes { get; set; } = 60;

    /// <summary>Porta UDP da receptora SIA-DC09 / Contact ID (0 = desliga).</summary>
    public int SiaUdpPort { get; set; } = 9999;

    /// <summary>
    /// Grava trilha de áudio quando a câmera envia e
    /// <see cref="Device.RecordAudio"/> está ligado. Desligar aqui corta o
    /// áudio de todas as câmeras deste nó (economia de disco).
    /// </summary>
    public bool RecordAudio { get; set; } = true;

    /// <summary>
    /// Minutos sem novo segmento em modo contínuo antes de gerar
    /// <c>recording_stalled</c>. Deve ser &gt; <see cref="SegmentSeconds"/>/60.
    /// </summary>
    public int SilentRecordingMinutes { get; set; } = 15;

    /// <summary>
    /// Identidade deste nó no cluster HA. Vazio = hostname da máquina.
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// HA do gravador: só o nó com lease no banco grava. Sharding continua
    /// valendo; o lease evita dois nós na mesma fatia gravarem em split-brain.
    /// </summary>
    public bool HaEnabled { get; set; }

    /// <summary>TTL do lease de gravador, em segundos.</summary>
    public int LeaseSeconds { get; set; } = 30;

    /// <summary>
    /// Live H.265→H.264 via FFmpeg republish no MediaMTX (browser-friendly).
    /// Consome CPU; desligado por padrão.
    /// </summary>
    public bool TranscodeLive { get; set; }

    /// <summary>
    /// Grava em H.264 + AAC (MP4 progressivo) para o player web reproduzir sem
    /// conversão. Se false, copia o codec da câmera (HEVC comum) e a normalização
    /// fica a cargo do RetentionService / endpoint de playback.
    /// </summary>
    public bool RecordBrowserCompatible { get; set; } = true;

    /// <summary>
    /// Gap mínimo (minutos) na gravação contínua para acionar edge pull.
    /// </summary>
    public int EdgePullGapMinutes { get; set; } = 5;

    /// <summary>
    /// Pré-alarme padrão (segundos) quando <see cref="Device.PreEventSeconds"/>
    /// não sobrescreve. 0 = desliga pre-buffer globalmente se device também for 0.
    /// </summary>
    public int PreEventSeconds { get; set; } = 15;

    /// <summary>
    /// UUID do cluster gravado em <c>{StoragePath}/cluster.uuid</c>.
    /// Vazio = gera e persiste no primeiro boot. Impede purga cruzada entre ambientes.
    /// </summary>
    public string ClusterId { get; set; } = "";

    /// <summary>
    /// Volumes extras de gravação (além de <see cref="StoragePath"/>).
    /// Novos segmentos vão para o volume com mais espaço livre.
    /// </summary>
    public string[] StorageVolumes { get; set; } = [];

    /// <summary>
    /// Gap mínimo (minutos) entre blocos contínuos para emitir <c>recording_gap</c>.
    /// </summary>
    public int GapAlertMinutes { get; set; } = 5;

    /// <summary>
    /// Gera thumbnails de timeline (FFmpeg) a cada N minutos de segmento.
    /// 0 = desliga.
    /// </summary>
    public int ThumbnailIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Event bus distribuído: vazio = in-memory.
    /// Ex.: <c>redis://localhost:6379</c> ou path de arquivo/named-pipe futuro.
    /// </summary>
    public string EventBus { get; set; } = "";

    /// <summary>
    /// Esta instancia cuida da camera informada? Usado por gravacao, eventos e
    /// retencao para nao pisarem umas nas outras em topologia distribuida.
    /// </summary>
    public bool OwnsDevice(int deviceId)
        => ShardCount <= 1 || deviceId % ShardCount == ShardIndex;

    public string ResolveNodeId()
        => string.IsNullOrWhiteSpace(NodeId)
            ? Environment.MachineName
            : NodeId.Trim();

    /// <summary>Bridge MQTT → eventos da plataforma (IoT / sensores).</summary>
    public MqttOptions Mqtt { get; set; } = new();

    /// <summary>Resolve segundos de pré-alarme efetivos para a câmera.</summary>
    public int EffectivePreEventSeconds(Device cam)
    {
        var d = cam.PreEventSeconds;
        if (d < 0) d = PreEventSeconds;
        return Math.Clamp(d, 0, 300);
    }
}

public class MqttOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Tópicos (wildcard MQTT permitido). Padrão: platform/iot/#</summary>
    public string[] Topics { get; set; } = ["platform/iot/#"];
    public int TenantId { get; set; } = 1;
}
