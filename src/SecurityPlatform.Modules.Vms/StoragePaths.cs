namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Resolve caminhos de gravação de forma estável.
/// <c>dotnet run</c> sem launch-profile deixa o CWD na pasta de onde o comando
/// foi lançado, enquanto o ContentRoot fica no projeto da API — paths relativos
/// como <c>./data/recordings</c> quebram o playback (FileNotFound) e a retenção.
/// </summary>
public static class StoragePaths
{
    /// <summary>Torna o StoragePath absoluto com base no content root da app.</summary>
    public static string ResolveRoot(string storagePath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            storagePath = "./data/recordings";
        if (Path.IsPathRooted(storagePath))
            return Path.GetFullPath(storagePath);
        return Path.GetFullPath(Path.Combine(contentRoot, storagePath));
    }

    /// <summary>
    /// Localiza o arquivo de um registro no banco (path relativo ou absoluto).
    /// Aceita legados <c>./data/recordings\3\c_….mp4</c> e absolutos.
    /// </summary>
    public static string? ResolveExisting(string? storedPath, string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;

        // 1) Como está (absoluto ou relativo ao CWD).
        try
        {
            var asIs = Path.GetFullPath(storedPath);
            if (File.Exists(asIs)) return asIs;
        }
        catch (Exception) when (IsPathException())
        { /* tenta próximos */ }

        var norm = storedPath.Replace('\\', '/').Trim();

        // 2) Trecho após "recordings/" → storageRoot/…
        var marker = "recordings/";
        var idx = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rel = norm[(idx + marker.Length)..].TrimStart('/');
            var candidate = Path.GetFullPath(Path.Combine(storageRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(candidate)) return candidate;
        }

        // 3) Combinar com storage root (ignora ./)
        var trimmed = norm.TrimStart('.', '/');
        if (trimmed.StartsWith("data/recordings/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["data/recordings/".Length..];
        var under = Path.GetFullPath(Path.Combine(storageRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(under)) return under;

        // 4) Só o nome do arquivo sob pastas de câmera
        var fileName = Path.GetFileName(storedPath);
        if (!string.IsNullOrEmpty(fileName) && Directory.Exists(storageRoot))
        {
            try
            {
                foreach (var hit in Directory.EnumerateFiles(storageRoot, fileName, SearchOption.AllDirectories))
                    return hit;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>Path absoluto para gravar um novo segmento.</summary>
    public static string DeviceDir(string storageRoot, int deviceId)
        => Path.Combine(storageRoot, deviceId.ToString());

    /// <summary>
    /// Escolhe o volume com mais espaço livre entre primary + extras.
    /// Volumes inexistentes são criados; sem info de disco, usa primary.
    /// </summary>
    public static string PickVolume(string primary, IReadOnlyList<string>? extras)
    {
        var candidates = new List<string> { Path.GetFullPath(primary) };
        if (extras is not null)
        {
            foreach (var e in extras)
            {
                if (string.IsNullOrWhiteSpace(e)) continue;
                try
                {
                    var full = Path.GetFullPath(e.Trim());
                    if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(full);
                }
                catch { /* path inválido */ }
            }
        }

        string? best = null;
        long bestFree = -1;
        foreach (var root in candidates)
        {
            try
            {
                Directory.CreateDirectory(root);
                var free = GetFreeBytes(root);
                if (free > bestFree)
                {
                    bestFree = free;
                    best = root;
                }
            }
            catch
            {
                if (best is null) best = root;
            }
        }
        return best ?? Path.GetFullPath(primary);
    }

    public static long GetFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;
            var di = new DriveInfo(root);
            return di.IsReady ? di.AvailableFreeSpace : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Confina path à raiz de storage (case-insensitive no Windows).</summary>
    public static bool IsInside(string path, string root)
    {
        try
        {
            var raiz = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(raiz, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathException() => true;
}
