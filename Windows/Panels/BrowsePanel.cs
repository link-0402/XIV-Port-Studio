using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Stage 1 — find what to port. The canvas is the game browser (gear by slot, character
/// features by race and id); the inspector shows what the game ships for the highlighted row,
/// so it can be checked before it joins the modpack.
/// </summary>
internal sealed class BrowsePanel : IStagePanel
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;
    private readonly ItemBrowserPanel _browser;

    public BrowsePanel(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
        _browser = new ItemBrowserPanel(plugin, session);
    }

    public StageId         Id      => StageId.Browse;
    public string          Title   => "Browse";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Search;
    public string          Purpose => "Find gear or a character feature and add it to the modpack.";

    public void DrawCanvas() => _browser.Draw();

    public void DrawInspector()
    {
        var subject = _browser.Highlighted;
        if (subject == null)
        {
            Ui.HintWrapped("Click a row to see what the game ships for it. Press + on it, or drag it onto the list on the left, to add it to the modpack.");
            return;
        }

        bool inPack = _session.IsInModpack(subject.Key);
        Ui.SectionHeader(subject.DisplayName, inPack ? "Already in the modpack." : null);

        if (subject is GearSubject gear)
        {
            Ui.LabeledValue("Slot", SlotInfo.DisplayLabelMap[gear.Item.Slot]);
            Ui.LabeledValue("Model", gear.Item.ModelIdDisplay, "Model set id and variant.");
        }
        else if (subject is FeatureSubject feature)
        {
            Ui.LabeledValue("Race", feature.Race.DisplayName);
            Ui.LabeledValue("Id", feature.IdDisplay);
        }
        Ui.LabeledValue("Materials", subject.MaterialFolder(subject.BaseRace), "Every .mtrl a port of this writes goes here.");
        Ui.LabeledValue("Textures", subject.TextureFolder);

        ImGui.Spacing();
        if (subject.MultiRace)
        {
            var native = _plugin.GameData.GetNativeRaces(subject);
            ImGui.TextColored(Theme.Muted, subject is GearSubject ? "Races with their own model" : "Races that have this id");
            if (ImGui.BeginTable("##BrowseRaces", 2, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var rg in RaceInfo.AllRaces)
                {
                    ImGui.TableNextColumn();
                    bool has = native.Contains(rg);
                    Ui.Icon(has ? FontAwesomeIcon.Check : FontAwesomeIcon.Minus, has ? Theme.Good : Theme.Faint);
                    ImGui.SameLine();
                    ImGui.TextColored(has ? Theme.Muted : Theme.Faint, rg.DisplayName);
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();
        if (inPack)
        {
            if (Ui.IconTextButton(FontAwesomeIcon.ArrowRight, "Open in Details", "Show this item's set-up."))
                _session.Focus(new Target(TargetKind.Item, subject.Key));
        }
        else if (Ui.IconTextButton(FontAwesomeIcon.Plus, "Add to modpack"))
        {
            _session.AddItem(subject);
        }
    }
}
