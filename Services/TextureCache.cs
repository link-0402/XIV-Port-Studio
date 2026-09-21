using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace XIVPortStudio.Services;

/// <summary>
/// Keeps the .tex files the build produced, so a rebuild does not compress the same images again.
/// Compressing is by far the slowest part of a build — BC7 on a 4096×4096 image takes the better
/// part of a minute even across every core — and a port is usually built many times over while its
/// materials and metadata are worked out, from the same handful of source images.
///
/// An entry is keyed by everything that decides the bytes: the source file (its path, size and
/// timestamp) and the settings used. Editing the image changes its timestamp, so the next build
/// converts it again; nothing has to be invalidated by hand. Entries live in the plugin's own
/// config folder and are pruned oldest-first once they pass <see cref="MaxBytes"/>.
///
/// Used from the build worker, which is the only thread that touches it during a build.
/// </summary>
internal sealed class TextureCache
{
    /// <summary>How much converted texture data to keep before pruning the least recently used.</summary>
    private const long MaxBytes = 1024L * 1024 * 1024;

    private readonly string? _directory;

    public TextureCache(string? directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    /// <summary>Identifies one conversion: the source as it was, and everything that shaped the result.</summary>
    public static string KeyFor(string sourcePath, params object[] settings)
    {
        string stamp;
        try
        {
            var info = new FileInfo(sourcePath);
            stamp = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            stamp = sourcePath;
        }

        return Hash($"{stamp}|{string.Join('|', settings)}");
    }

    /// <summary>Identifies a conversion with no source file of its own, such as a generated placeholder.</summary>
    public static string KeyForGenerated(params object[] settings) => Hash($"generated|{string.Join('|', settings)}");

    /// <summary>The bytes this conversion produced last time, or null when it has to be done again.</summary>
    public byte[]? Get(string key)
    {
        var path = PathFor(key);
        if (path == null || !File.Exists(path))
            return null;

        try
        {
            var data = File.ReadAllBytes(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // keeps what is in use from being pruned
            return data;
        }
        catch (Exception)
        {
            return null;   // a cache that cannot be read is simply a miss
        }
    }

    public void Store(string key, byte[] data)
    {
        var path = PathFor(key);
        if (path == null)
            return;

        try
        {
            Directory.CreateDirectory(_directory!);
            File.WriteAllBytes(path, data);
        }
        catch (Exception)
        {
            // Nothing to do about it: the build has the bytes either way.
        }
    }

    /// <summary>Drops the oldest entries once the folder grows past its limit. Called after a build.</summary>
    public void Prune()
    {
        if (_directory == null || !Directory.Exists(_directory))
            return;

        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("*.tex");
            long total = files.Sum(f => f.Length);
            if (total <= MaxBytes)
                return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                file.Delete();
                total -= file.Length;
                if (total <= MaxBytes)
                    return;
            }
        }
        catch (Exception)
        {
            // Pruning is housekeeping; a failure here must not fail a build.
        }
    }

    private string? PathFor(string key)
        => _directory == null ? null : Path.Combine(_directory, $"{key}.tex");

    private static string Hash(string value)
        => System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
}
