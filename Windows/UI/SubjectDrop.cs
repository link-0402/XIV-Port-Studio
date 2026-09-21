using System;
using Dalamud.Bindings.ImGui;
using XIVPortStudio.Models;

namespace XIVPortStudio.Windows.UI;

/// <summary>
/// Dragging a piece of gear or a character feature from the item browser onto the modpack
/// list (or the File Setup tab) to add it to the modpack.
/// </summary>
internal static class SubjectDrop
{
    private const string Label = "XPS_SUBJECT";

    /// <summary>What the drag carries. ImGui payloads are bytes; the subject stays here.</summary>
    private static PortSubject? _dragging;

    /// <summary>Makes the last drawn item draggable, carrying <paramref name="subject"/>.</summary>
    public static void Source(PortSubject subject, bool alreadyInPack)
    {
        if (!ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            return;

        _dragging = subject;
        ImGui.SetDragDropPayload(Label, ReadOnlySpan<byte>.Empty, ImGuiCond.None);
        ImGui.TextUnformatted(subject.DisplayName);
        ImGui.TextColored(Theme.Muted, alreadyInPack ? "Already in the modpack." : "Drop onto the modpack list to add it.");
        ImGui.EndDragDropSource();
    }

    /// <summary>Whether a subject is being dragged right now.</summary>
    public static bool IsDragging
    {
        get
        {
            var payload = ImGui.GetDragDropPayload();
            return !payload.IsNull && payload.IsDataType(Label) && _dragging != null;
        }
    }

    /// <summary>A drop target on the last drawn item. Returns the subject on the frame it lands, else null.</summary>
    public static PortSubject? Target()
    {
        if (!IsDragging || !ImGui.BeginDragDropTarget())
            return null;

        PortSubject? landed = null;
        if (!ImGui.AcceptDragDropPayload(Label, ImGuiDragDropFlags.None).IsNull)
        {
            landed = _dragging;
            _dragging = null;
        }
        ImGui.EndDragDropTarget();
        return landed;
    }
}
