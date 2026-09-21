using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.Panels;

namespace XIVPortStudio.Windows.UI;

/// <summary>
/// The left pane: everything in the modpack, one row each. Clicking a row opens it on the
/// Details stage; the pane is also where gear dragged out of the Browse stage is dropped.
/// </summary>
internal sealed class ModpackList
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;

    public ModpackList(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
    }

    public void Draw()
    {
        Ui.SectionHeader($"In this modpack ({_session.Items.Count})",
            "One build makes one mod out of all of these. Click one to set it up on the Details stage.");

        if (!_session.Loaded)
        {
            Ui.Icon(FontAwesomeIcon.Hourglass, Theme.Muted);
            ImGui.SameLine();
            Ui.Hint("Reading the game's item list…");
            return;
        }

        // The whole pane takes drops from the browser, so there is somewhere to aim at
        // even when the pack is still empty.
        if (ImGui.BeginChild("##PackList", new Vector2(-1, -ImGui.GetFrameHeightWithSpacing()), false))
            DrawRows();
        ImGui.EndChild();
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);

        if (Ui.IconTextButton(FontAwesomeIcon.Plus, "Add gear or a feature", "Opens the Browse stage.", ImGui.GetContentRegionAvail().X))
            _session.GoToStage(StageId.Browse);
    }

    private void DrawRows()
    {
        if (_session.Items.Count == 0)
        {
            Ui.HintWrapped(SubjectDrop.IsDragging
                ? "Drop it here to add it to the modpack."
                : "Nothing here yet. Add gear or a character feature from the Browse stage.");
            return;
        }

        foreach (var item in _session.Items.ToList())
        {
            var target = new Target(TargetKind.Item, item.Key);
            ImGui.PushID((int)item.Key);

            var worst = PortValidator.WorstUnder(_session.Issues, target);
            float markerW = worst != null ? ImGui.GetFrameHeight() : 0;
            float removeW = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;

            Ui.Icon(item.Subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User,
                item.HasWork ? Theme.Accent : Theme.Muted);
            ImGui.SameLine();

            bool selected = _session.Selection is { } s && s.Item == item.Key;
            var size = new Vector2(ImGui.GetContentRegionAvail().X - removeW - markerW, 0);
            if (ImGui.Selectable($"{item.Subject.DisplayName}##row", selected, ImGuiSelectableFlags.None, size))
                _session.Focus(target);
            Ui.Tooltip($"{item.Subject.KindLabel} · {item.Subject.IdDisplay}\n" +
                       $"{item.Models.Count} race model(s), {item.Materials.Count} material(s)");

            if (worst != null)
            {
                ImGui.SameLine();
                Ui.Icon(IssueList.IconFor(worst.Value), Ui.SeverityColor(worst.Value));
                Ui.Tooltip(string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(target)).Select(i => i.Message)));
            }

            ImGui.SameLine();
            Ui.AlignRight(ImGui.GetFrameHeight());
            if (Ui.IconButton(FontAwesomeIcon.Trash, "remove", "Remove from the modpack (its set-up is kept)", danger: true))
                _session.RemoveItem(item.Key);

            ImGui.PopID();
        }
    }
}
