using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.DragDrop;

namespace XIVPortStudio.Windows.UI;

/// <summary>Files and folders being dragged, or just dropped.</summary>
internal sealed record DroppedPaths(IReadOnlyList<string> Files, IReadOnlyList<string> Folders)
{
    public static readonly DroppedPaths Empty = new(Array.Empty<string>(), Array.Empty<string>());

    public static DroppedPaths File(string path)   => new(new[] { path }, Array.Empty<string>());
    public static DroppedPaths Folder(string path) => new(Array.Empty<string>(), new[] { path });

    public bool IsEmpty => Files.Count == 0 && Folders.Count == 0;

    public string Describe()
    {
        if (Files.Count == 1 && Folders.Count == 0) return Path.GetFileName(Files[0]);
        if (Folders.Count == 1 && Files.Count == 0) return $"{Path.GetFileName(Folders[0].TrimEnd('\\', '/'))}{Path.DirectorySeparatorChar}";
        var parts = new List<string>();
        if (Files.Count > 0)   parts.Add($"{Files.Count} file(s)");
        if (Folders.Count > 0) parts.Add($"{Folders.Count} folder(s)");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Drag and drop of file paths onto inputs. Two kinds of source feed the same targets:
/// the Sims importer's result list (an ImGui drag inside the game, see <see cref="Source"/>),
/// and files or folders dragged in from Windows Explorer (through Dalamud's drag-drop service,
/// see <see cref="BeginExternalSource"/>).
///
/// A target sits on the input drawn just before it. While something it would accept hovers,
/// the input is outlined in the accent colour; while something it would refuse hovers, the
/// outline is red and a tooltip says why.
/// </summary>
internal static class FileDrop
{
    private const string InternalLabel = "XPS_PATHS";
    private const string ExternalLabel = "XPS_EXTERNAL";

    /// <summary>What the in-game drag carries. ImGui payloads are bytes; the paths stay here.</summary>
    private static DroppedPaths? _internal;

    private static IDragDropManager? _external;

    public static void Initialize(IDragDropManager manager) => _external = manager;

    /// <summary>
    /// Registers Explorer drags as an ImGui drag source for this frame. Called once per frame by
    /// each window that has drop targets, before drawing them.
    /// </summary>
    public static void BeginExternalSource()
    {
        if (_external is not { ServiceAvailable: true })
            return;
        _external.CreateImGuiSource(ExternalLabel, m => m.Files.Count > 0 || m.Directories.Count > 0, m =>
        {
            ImGui.TextUnformatted(new DroppedPaths(m.Files.ToList(), m.Directories.ToList()).Describe());
            return true;
        });
    }

    /// <summary>Makes the last drawn item draggable, carrying <paramref name="paths"/>.</summary>
    public static void Source(DroppedPaths paths, string label)
    {
        if (!ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            return;

        _internal = paths;
        ImGui.SetDragDropPayload(InternalLabel, ReadOnlySpan<byte>.Empty, ImGuiCond.None);
        ImGui.TextUnformatted(label);
        ImGui.TextColored(Theme.Muted, "Drop onto a texture or model input in the main window.");
        ImGui.EndDragDropSource();
    }

    /// <summary>What is being dragged right now, from either source, or null.</summary>
    private static DroppedPaths? Dragging()
    {
        var payload = ImGui.GetDragDropPayload();
        if (payload.IsNull)
            return null;
        if (payload.IsDataType(InternalLabel))
            return _internal;
        if (payload.IsDataType(ExternalLabel) && _external is { IsDragging: true } ext)
            return new DroppedPaths(ext.Files.ToList(), ext.Directories.ToList());
        return null;
    }

    /// <summary>
    /// A drop target on the last drawn item. <paramref name="reject"/> returns why the paths
    /// cannot be used here, or null when they can. Returns true on the frame an accepted drop lands.
    /// </summary>
    public static bool Target(Func<DroppedPaths, string?> reject, out DroppedPaths dropped)
    {
        dropped = DroppedPaths.Empty;
        var hovering = Dragging();
        if (hovering == null)
            return false;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (!ImGui.BeginDragDropTarget())
            return false;

        var reason = reject(hovering);
        ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(reason == null ? Theme.Accent : Theme.Bad),
            ImGui.GetStyle().FrameRounding, ImDrawFlags.None, 2f);

        bool landed = false;
        bool external = ImGui.GetDragDropPayload().IsDataType(ExternalLabel);
        if (reason == null && !external
            && !ImGui.AcceptDragDropPayload(InternalLabel, ImGuiDragDropFlags.AcceptNoDrawDefaultRect).IsNull && _internal != null)
        {
            dropped = _internal;
            _internal = null;
            landed = true;
        }
        ImGui.EndDragDropTarget();

        // Dalamud's target opens its own BeginDragDropTarget on the last item, so it must run
        // outside ours (ImGui does not allow them to nest).
        if (reason == null && external && _external != null && _external.CreateImGuiTarget(ExternalLabel, out var files, out var dirs))
        {
            dropped = new DroppedPaths(files, dirs);
            landed = true;
        }

        // Last: a tooltip opens a window of its own, which would change what "the last item" is.
        if (reason != null)
            ImGui.SetTooltip(reason);
        return landed;
    }

    // ── Acceptance rules shared by the inputs ─────────────────────────────────

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".dds" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsModel(string path) => Path.GetExtension(path).Equals(".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why paths cannot fill a texture slot: one image, or one folder of images.</summary>
    public static string? RejectForTexture(DroppedPaths p)
    {
        if (p.Files.Count == 1 && p.Folders.Count == 0)
            return IsImage(p.Files[0]) ? null : $"{Path.GetFileName(p.Files[0])} is not a png, jpeg or dds image.";
        if (p.Folders.Count == 1 && p.Files.Count == 0)
            return SafeFiles(p.Folders[0]).Any(IsImage) ? null : "That folder has no png, jpeg or dds images in it.";
        return "Drop one image, or one folder of images for variants.";
    }

    /// <summary>Why paths cannot fill a race model: .mdl files, or folders holding them.</summary>
    public static string? RejectForModel(DroppedPaths p)
    {
        var models = ModelFiles(p);
        if (models.Count > 0)
            return null;

        var foreign = p.Files.FirstOrDefault(f => !IsModel(f));
        if (foreign != null)
        {
            var ext = Path.GetExtension(foreign).ToLowerInvariant();
            return ext is ".glb" or ".gltf" or ".fbx" or ".obj"
                ? $"{Path.GetFileName(foreign)} is a {ext} mesh — convert it to .mdl first (Blender with the Meddle / TexTools plugins, or TexTools)."
                : $"{Path.GetFileName(foreign)} is not a .mdl model.";
        }
        return p.Folders.Count > 0 ? "No .mdl files in that folder." : "Drop a .mdl file.";
    }

    /// <summary>Every .mdl among the dropped files, plus those directly inside dropped folders, in name order.</summary>
    public static List<string> ModelFiles(DroppedPaths p)
        => p.Files.Where(IsModel)
            .Concat(p.Folders.SelectMany(SafeFiles).Where(IsModel).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
