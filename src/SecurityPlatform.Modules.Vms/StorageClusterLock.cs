using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SecurityPlatform.Modules.Vms;

/// <summary>
/// Impede dois ambientes com bancos diferentes de compartilhar o mesmo
/// <see cref="VmsOptions.StoragePath"/> (risco de purga cruzada na retenção).
/// Grava <c>cluster.uuid</c> na raiz do storage na primeira subida.
/// </summary>
public sealed class StorageClusterLock(IOptions<VmsOptions> options, ILogger<StorageClusterLock> log)
{
    public const string FileName = "cluster.uuid";
    private readonly VmsOptions _opt = options.Value;

    public string EnsureAndValidate()
    {
        var root = _opt.StoragePath;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);

        var configured = (_opt.ClusterId ?? "").Trim();
        if (string.IsNullOrEmpty(configured))
            configured = Guid.NewGuid().ToString("D");

        if (!File.Exists(path))
        {
            File.WriteAllText(path, configured + Environment.NewLine);
            log.LogInformation("Storage cluster id gravado em {Path}: {Id}", path, configured);
            _opt.ClusterId = configured;
            return configured;
        }

        var onDisk = File.ReadAllText(path).Trim();
        if (string.IsNullOrEmpty(onDisk))
        {
            File.WriteAllText(path, configured + Environment.NewLine);
            _opt.ClusterId = configured;
            return configured;
        }

        // Se o operador fixou ClusterId e diverge do disco → aborta (protege produção).
        if (!string.IsNullOrWhiteSpace(_opt.ClusterId)
            && !string.Equals(onDisk, _opt.ClusterId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"StoragePath '{root}' pertence ao cluster '{onDisk}', " +
                $"mas Vms:ClusterId='{_opt.ClusterId}'. " +
                "Dois ambientes no mesmo volume causam purga cruzada. " +
                "Use pastas separadas ou alinhe o ClusterId.");
        }

        _opt.ClusterId = onDisk;
        log.LogInformation("Storage cluster id: {Id}", onDisk);
        return onDisk;
    }

    public static string? Read(string storageRoot)
    {
        var path = Path.Combine(storageRoot, FileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
