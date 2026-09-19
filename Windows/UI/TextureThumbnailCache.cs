using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Services.Sims;

namespace XIVPortStudio.Windows.UI;

/// <summary>
/// Small GPU previews of local texture files, shared by every window. Source images are
/// decoded and shrunk on a worker (a 4096² diffuse would otherwise become a 64 MB GPU
/// texture just to draw a 40-pixel square), cached by path + last-write time, and evicted
/// least-recently-drawn first. A miss draws a placeholder rather than blocking the frame.
/// </summary>
internal sealed class TextureThumbnailCache : IDisposable
{
    private const int MaxSide  = 160;
    private const int Capacity = 96;

    /// <summary>How often (in frames) a cached file's timestamp is re-checked for edits.</summary>
    private const int RecheckFrames = 90;

    private sealed class Entry
    {
        public required Task<IDalamudTextureWrap> Load;
        public DateTime Stamp;
        public long     LastUsed;
        public long     CheckedAt;
    }

    private sealed record FolderListing(string? FirstImage, int Count, long ListedAt);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderListing> _folders = new(StringComparer.OrdinalIgnoreCase);
    private long _frame;

    /// <summary>Advances the frame clock and trims the cache. Call once per frame.</summary>
    public void NewFrame()
    {
        _frame++;
        if (_entries.Count <= Capacity)
            return;

        foreach (var key in _entries.OrderBy(e => e.Value.LastUsed).Take(_entries.Count - Capacity).Select(e => e.Key).ToList())
        {
            Release(_entries[key]);
            _entries.Remove(key);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Drawing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the thumbnail for a texture slot in whichever mode it is in: a white swatch for
    /// a generated dummy, the first image (with a count) for a variant folder, or the file
    /// itself. Hovering shows a larger preview.
    /// </summary>
    public void Draw(TextureSlot tex, float side)
    {
        if (tex.UseWhiteDummy)
        {
            var option = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);
            if (option.IsGenerated)
            {
                Swatch(side, new Vector4(1, 1, 1, 1));
                Ui.Tooltip($"Generated white {tex.WhiteDummySize}×{tex.WhiteDummySize} texture");
            }
            else if (option.Key == DummyTextureLibrary.NullNormalKey)
            {
                // The "flat"/no-bump colour of a tangent-space normal map, so the swatch reads
                // correctly at a glance instead of looking like an arbitrary blue placeholder.
                Swatch(side, new Vector4(0.5f, 0.5f, 1f, 1f));
                Ui.Tooltip($"Bundled dummy texture — {option.Label}");
            }
            else
            {
                Placeholder(side, FontAwesomeIcon.LayerGroup, Theme.Accent, $"Bundled dummy texture — {option.Label}");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(tex.SourcePath))
        {
            Placeholder(side, FontAwesomeIcon.Image, Theme.Faint, "No source set");
            return;
        }

        if (tex.UseVariants)
        {
            var listing = ListFolder(tex.SourcePath);
            if (listing.FirstImage == null)
            {
                Placeholder(side, FontAwesomeIcon.FolderOpen, Theme.Bad, listing.Count < 0 ? "Folder not found" : "No images in folder");
                return;
            }

            DrawFile(listing.FirstImage, side, $"{listing.Count} variant image(s) — first shown");
            var min = ImGui.GetItemRectMin();
            var countText = listing.Count.ToString();
            var size = ImGui.CalcTextSize(countText) + new Vector2(Theme.S(6), Theme.S(2));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.75f)), Theme.S(3));
            draw.AddText(min + new Vector2(Theme.S(3), Theme.S(1)), ImGui.GetColorU32(Theme.Accent), countText);
            return;
        }

        DrawFile(tex.SourcePath, side, null);
    }

    private void DrawFile(string path, float side, string? caption)
    {
        if (!File.Exists(path))
        {
            Placeholder(side, FontAwesomeIcon.ExclamationTriangle, Theme.Bad, $"File not found:\n{path}");
            return;
        }

        var load = Get(path);
        if (load == null || !load.IsCompleted)
        {
            Placeholder(side, FontAwesomeIcon.Hourglass, Theme.Faint, "Loading preview…");
            return;
        }

        if (!load.IsCompletedSuccessfully)
        {
            var reason = load.Exception?.GetBaseException().Message ?? "unknown error";
            Placeholder(side, FontAwesomeIcon.ExclamationTriangle, Theme.Warn, $"No preview: {reason}");
            return;
        }

        var wrap = load.Result;
        ImGui.Image(wrap.Handle, Fit(wrap.Size, side));
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Image(wrap.Handle, Fit(wrap.Size, Theme.S(MaxSide)));
            if (caption != null) ImGui.TextColored(Theme.Muted, caption);
            ImGui.TextColored(Theme.Muted, Path.GetFileName(path));
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// Draws an already-encoded PNG (e.g. a Sims package thumbnail) cached under
    /// <paramref name="key"/>. Keys are namespaced so they never collide with file paths.
    /// </summary>
    public void DrawPng(string key, byte[] png, float side)
    {
        var cacheKey = MemoryPrefix + key;
        if (!_entries.TryGetValue(cacheKey, out var entry))
        {
            entry = new Entry { Load = Plugin.TextureProvider.CreateFromImageAsync(png), CheckedAt = long.MaxValue };
            _entries[cacheKey] = entry;
        }
        entry.LastUsed = _frame;

        if (!entry.Load.IsCompleted)
        {
            Placeholder(side, FontAwesomeIcon.Hourglass, Theme.Faint, "Loading preview…");
            return;
        }
        if (!entry.Load.IsCompletedSuccessfully)
        {
            Placeholder(side, FontAwesomeIcon.ExclamationTriangle, Theme.Warn, "No preview");
            return;
        }

        var wrap = entry.Load.Result;
        ImGui.Image(wrap.Handle, Fit(wrap.Size, side));
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Image(wrap.Handle, Fit(wrap.Size, Theme.S(MaxSide * 1.5f)));
            ImGui.TextColored(Theme.Muted, $"{wrap.Width}×{wrap.Height} preview");
            ImGui.EndTooltip();
        }
    }

    /// <summary>Drops every in-memory thumbnail, e.g. when a new package is scanned.</summary>
    public void ForgetMemoryEntries()
    {
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(MemoryPrefix, StringComparison.Ordinal)).ToList())
        {
            Release(_entries[key]);
            _entries.Remove(key);
        }
    }

    private const string MemoryPrefix = "mem://";

    private static Vector2 Fit(Vector2 size, float side)
        => size.X >= size.Y
            ? new Vector2(side, side * size.Y / Math.Max(size.X, 1f))
            : new Vector2(side * size.X / Math.Max(size.Y, 1f), side);

    private static void Swatch(float side, Vector4 color)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(side), ImGui.GetColorU32(color), Theme.S(3));
        draw.AddRect(pos, pos + new Vector2(side), ImGui.GetColorU32(Theme.Faint), Theme.S(3));
        ImGui.Dummy(new Vector2(side));
    }

    private static void Placeholder(float side, FontAwesomeIcon icon, Vector4 color, string tooltip)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRect(pos, pos + new Vector2(side), ImGui.GetColorU32(Theme.Faint), Theme.S(3));

        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var glyphSize = ImGui.CalcTextSize(glyph);
        draw.AddText(pos + (new Vector2(side) - glyphSize) * 0.5f, ImGui.GetColorU32(color), glyph);
        ImGui.PopFont();

        ImGui.Dummy(new Vector2(side));
        Ui.Tooltip(tooltip);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The (possibly still running) load for a file, restarted if the file changed on disk.</summary>
    private Task<IDalamudTextureWrap>? Get(string path)
    {
        if (_entries.TryGetValue(path, out var entry))
        {
            entry.LastUsed = _frame;
            if (_frame - entry.CheckedAt < RecheckFrames)
                return entry.Load;

            entry.CheckedAt = _frame;
            if (Stamp(path) == entry.Stamp)
                return entry.Load;

            Release(entry);
            _entries.Remove(path);
        }

        var created = new Entry
        {
            Load      = Task.Run(() => LoadAsync(path)),
            Stamp     = Stamp(path),
            LastUsed  = _frame,
            CheckedAt = _frame,
        };
        _entries[path] = created;
        return created.Load;
    }

    private static async Task<IDalamudTextureWrap> LoadAsync(string path)
    {
        var png = MakeThumbnailPng(path);
        return await Plugin.TextureProvider.CreateFromImageAsync(png).ConfigureAwait(false);
    }

    private static byte[] MakeThumbnailPng(string path)
    {
        var data = File.ReadAllBytes(path);
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            return SimsTextureDecoder.Decode(SimsResourceType.ImgDds, data).ToThumbnailPng(MaxSide);

        using var image = Image.Load<Rgba32>(data);
        if (image.Width > MaxSide || image.Height > MaxSide)
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxSide, MaxSide), Mode = ResizeMode.Max }));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static DateTime Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception) { return default; }
    }

    /// <summary>First image and image count of a variant folder, re-listed every few seconds. Count is -1 if missing.</summary>
    private FolderListing ListFolder(string folder)
    {
        if (_folders.TryGetValue(folder, out var cached) && _frame - cached.ListedAt < RecheckFrames * 2)
            return cached;

        FolderListing listing;
        try
        {
            if (!Directory.Exists(folder))
            {
                listing = new FolderListing(null, -1, _frame);
            }
            else
            {
                var images = ModBuilder.EnumerateImages(folder).ToList();
                listing = new FolderListing(images.FirstOrDefault(), images.Count, _frame);
            }
        }
        catch (Exception)
        {
            listing = new FolderListing(null, -1, _frame);
        }

        _folders[folder] = listing;
        return listing;
    }

    private static void Release(Entry entry)
    {
        // Dispose once the load finishes, whenever that is — the wrap may still be in flight.
        entry.Load.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                t.Result.Dispose();
        }, TaskScheduler.Default);
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
            Release(entry);
        _entries.Clear();
        _folders.Clear();
    }

    public void Dispose() => Clear();
}
