namespace SecurityPlatform.Core.Domain;

/// <summary>
/// Planta/sinótico 2D ou 3D (perspectiva). O fundo pode ser imagem de planta
/// (upload) ou cor sólida. Marcadores apontam para câmeras/dispositivos.
/// </summary>
public class SynopticMap
{
    public int Id { get; set; }
    public int TenantId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary><c>2d</c> (planta plana) ou <c>3d</c> (perspectiva CSS isométrica).</summary>
    public string Mode { get; set; } = "2d";

    /// <summary>URL relativa do fundo, ex.: <c>/maps/bg/3.png</c>.</summary>
    public string? BackgroundUrl { get; set; }

    /// <summary>Cor de fundo se não houver imagem (#0a0e14).</summary>
    public string BackgroundColor { get; set; } = "#0a0e14";

    /// <summary>Largura lógica do mapa em px (coordenadas dos marcadores 0..Width).</summary>
    public int Width { get; set; } = 1280;

    /// <summary>Altura lógica do mapa em px.</summary>
    public int Height { get; set; } = 720;

    /// <summary>Ângulo de perspectiva no modo 3D (graus).</summary>
    public double PerspectiveDeg { get; set; } = 55;

    /// <summary>Ordem de exibição na lista.</summary>
    public int SortOrder { get; set; }

    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<MapMarker> Markers { get; set; } = [];
}

/// <summary>
/// Ícone no sinótico. Coordenadas normalizadas em % (0–100) sobre a área do mapa
/// para o layout sobreviver a redimensionamento; X/Y/Z em % e altitude relativa.
/// </summary>
public class MapMarker
{
    public int Id { get; set; }
    public int MapId { get; set; }
    public SynopticMap? Map { get; set; }

    /// <summary>Dispositivo vinculado (câmera, I/O, etc.). Opcional para zona livre.</summary>
    public int? DeviceId { get; set; }

    public string Label { get; set; } = "";

    /// <summary>camera | zone | door | alarm | custom</summary>
    public string Kind { get; set; } = "camera";

    /// <summary>Ícone: camera, door, bell, pin, square…</summary>
    public string Icon { get; set; } = "camera";

    /// <summary>Posição X em % da largura (0–100).</summary>
    public double X { get; set; }

    /// <summary>Posição Y em % da altura (0–100).</summary>
    public double Y { get; set; }

    /// <summary>Altitude / camada no modo 3D (0–100, default 0).</summary>
    public double Z { get; set; }

    /// <summary>Rotação do ícone em graus (ex.: direção da câmera).</summary>
    public double Rotation { get; set; }

    /// <summary>Cor de destaque (#3fb950). Vazio = automático por status.</summary>
    public string? Color { get; set; }

    /// <summary>Metadados JSON livres (zona, POP, etc.).</summary>
    public string MetaJson { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
