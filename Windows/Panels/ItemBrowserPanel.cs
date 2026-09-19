using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// The left pane: choose what the port replaces. Gear is picked by slot and item name;
/// character features (hair, face, tail, ears) by race and id. Everything configured in
/// the other stages is attached to the subject picked here.
///
/// Lists draw only the rows in view (a slot can hold thousands of items), and the
/// search waits for typing to pause before re-filtering.
/// </summary>
internal sealed class ItemBrowserPanel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private static readonly SubjectKind[] Kinds = { SubjectKind.Gear, SubjectKind.Hair, SubjectKind.Face, SubjectKind.Tail, SubjectKind.Ear };
    private static readonly string[] KindLabels = { "Gear", "Hair", "Face", "Tail", "Ears" };

    private readonly Plugin _plugin;
    private readonly PortSession _session;

    private SubjectKind _kind;

    // ── Gear ─────────────────────────────────────────────────────────────────
    private readonly EquipSlot[] _allSlots = SlotInfo.AllSlots.ToArray();
    private readonly string[]    _slotLabels;
    private int _slotIndex;
    private List<GameDataService.GameItem> _allItems = new();
    private List<GameDataService.GameItem> _filtered = new();

    // ── Features ─────────────────────────────────────────────────────────────
    private string _featureRace;
    private List<ushort> _featureIds = new();
    private object? _filteredFrom;

    // ── Shared ───────────────────────────────────────────────────────────────
    private string _search;
    private bool _configuredOnly;
    private readonly Stopwatch _sinceTyped = new();
    private bool _filterPending;
    private bool _loaded;
    private bool _scrollToSelection;

    public ItemBrowserPanel(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
        _slotLabels = _allSlots.Select(s => SlotInfo.DisplayLabelMap[s]).ToArray();

        var cfg = plugin.Configuration;
        _kind        = Kinds[Math.Clamp(cfg.LastBrowserKind, 0, Kinds.Length - 1)];
        _slotIndex   = Math.Clamp(cfg.LastSlotIndex, 0, _allSlots.Length - 1);
        _search      = cfg.LastItemSearch;
        _featureRace = cfg.LastFeatureRace;
    }

    public void Draw()
    {
        if (!_plugin.GameData.IsItemCacheReady)
        {
            Ui.SectionHeader("Replace");
            Waiting("Reading the game's item list…");
            return;
        }

        if (!_loaded)
            FirstLoad();

        Ui.SectionHeader("Replace", "What your port replaces. Everything you set up is saved per item or feature.");
        DrawKindSwitch();
        ImGui.Spacing();

        bool listReady = _kind == SubjectKind.Gear ? DrawGearFilters() : DrawFeatureFilters();
        if (!listReady)
            return;

        DrawSearchRow();
        ImGui.Spacing();

        float footerH = ImGui.GetTextLineHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (ImGui.BeginChild("##SubjectList", new Vector2(-1, -footerH), true))
        {
            if (_kind == SubjectKind.Gear) DrawGearList();
            else                           DrawFeatureList();
        }
        ImGui.EndChild();

        int shown = _kind == SubjectKind.Gear ? _filtered.Count : _featureIds.Count;
        int total = _kind == SubjectKind.Gear ? _allItems.Count : AllIdsForRace().Count;
        Ui.Hint(shown == total ? $"{total} {Noun(total)}" : $"{shown} of {total} {Noun(total)}");
        DrawSelectionSummary();
    }

    private string Noun(int n) => _kind == SubjectKind.Gear ? "items" : $"{FeatureNaming.KindLabel(_kind).ToLowerInvariant()} ids";

    private static void Waiting(string text)
    {
        Ui.Icon(FontAwesomeIcon.Hourglass, Theme.Muted);
        ImGui.SameLine();
        Ui.Hint(text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Kind switch and filters
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawKindSwitch()
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float w = (ImGui.GetContentRegionAvail().X - spacing * (Kinds.Length - 1)) / Kinds.Length;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        for (int i = 0; i < Kinds.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            bool active = _kind == Kinds[i];
            ImGui.PushStyleColor(ImGuiCol.Button, active ? Theme.Accent with { W = 0.45f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.Text,   active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            if (ImGui.Button($"{KindLabels[i]}##kind", new Vector2(w, 0)) && !active)
            {
                _kind = Kinds[i];
                Reload();
                SaveBrowserState();
            }
            ImGui.PopStyleColor(2);
        }
        ImGui.PopStyleVar();
    }

    private bool DrawGearFilters()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##Slot", ref _slotIndex, _slotLabels, _slotLabels.Length))
        {
            Reload();
            SaveBrowserState();
        }
        Ui.Tooltip("Equipment slot");
        return true;
    }

    /// <summary>Race picker for features. Returns false while the game's files are still being scanned.</summary>
    private bool DrawFeatureFilters()
    {
        var ids = _plugin.GameData.GetFeatureIds(_kind);
        if (ids == null)
        {
            Waiting($"Finding every {FeatureNaming.KindLabel(_kind).ToLowerInvariant()} in the game…");
            return false;
        }
        if (!ReferenceEquals(ids, _filteredFrom))
        {
            // The scan finished (or the kind changed) since the list was last filtered.
            _filteredFrom = ids;
            ApplyFilter();
        }
        if (ids.Count == 0)
        {
            Ui.HintWrapped($"No {FeatureNaming.KindLabel(_kind).ToLowerInvariant()} models were found in the game files.");
            return false;
        }

        var races = ids.Keys.OrderBy(rg => rg.RaceCode, StringComparer.Ordinal).ToList();
        int raceIdx = races.FindIndex(rg => rg.RaceCode == _featureRace);
        if (raceIdx < 0)
        {
            raceIdx = 0;
            _featureRace = races[0].RaceCode;
            ApplyFilter();
        }

        var labels = races.Select(rg => rg.DisplayName).ToArray();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##FeatureRace", ref raceIdx, labels, labels.Length))
        {
            _featureRace = races[raceIdx].RaceCode;
            ApplyFilter();
            SaveBrowserState();
        }
        Ui.Tooltip(_kind == SubjectKind.Hair
            ? "Race the hairstyle is opened for. More races can be added on the Models stage."
            : "Race and gender this feature belongs to.");
        return true;
    }

    private void DrawSearchRow()
    {
        var hint = _kind == SubjectKind.Gear ? "Search name or model id…" : "Search id…";
        ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
        if (ImGui.InputTextWithHint("##Search", hint, ref _search, 256))
        {
            _filterPending = true;
            _sinceTyped.Restart();
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, _configuredOnly ? Theme.Accent : Theme.Muted);
        if (Ui.IconButton(FontAwesomeIcon.Star, "ConfiguredOnly",
                _configuredOnly ? "Showing only what you have set up — click to show all" : "Show only what you have set up"))
        {
            _configuredOnly = !_configuredOnly;
            ApplyFilter();
        }
        ImGui.PopStyleColor();

        if (_filterPending && _sinceTyped.Elapsed >= SearchDelay)
        {
            _filterPending = false;
            ApplyFilter();
            SaveBrowserState();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lists
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawGearList()
    {
        if (_filtered.Count == 0)
        {
            Ui.Hint(_configuredOnly ? "(no set-up items in this slot)" : "(no items match)");
            return;
        }

        var cfg = _plugin.Configuration;
        int selected = _scrollToSelection ? _filtered.FindIndex(i => i.RowId == _session.SubjectKey) : -1;
        DrawVirtualRows(_filtered.Count, selected, i =>
        {
            var item = _filtered[i];
            DrawRow(item.RowId, item.Name, item.ModelIdDisplay, HasWork(cfg, item.RowId),
                () => _session.SelectSubject(new GearSubject(item)),
                $"{item.Name}\nModel {item.ModelIdDisplay}");
        });
    }

    private void DrawFeatureList()
    {
        if (_featureIds.Count == 0)
        {
            Ui.Hint(_configuredOnly ? "(nothing set up for this race)" : "(no ids match)");
            return;
        }

        var race = CurrentRace();
        if (race == null) return;

        var cfg = _plugin.Configuration;
        var label = FeatureNaming.KindLabel(_kind);
        int selected = -1;
        if (_scrollToSelection)
            selected = _featureIds.FindIndex(id => new FeatureSubject(_kind, race.Value, id).Key == _session.SubjectKey);

        DrawVirtualRows(_featureIds.Count, selected, i =>
        {
            var subject = new FeatureSubject(_kind, race.Value, _featureIds[i]);
            DrawRow(subject.Key, $"{label} {subject.Id}", subject.IdDisplay, HasWork(cfg, subject.Key),
                () => _session.SelectSubject(subject),
                $"{subject.DisplayName}\n{subject.ModelGamePath(race.Value)}");
        });
    }

    /// <summary>
    /// Submits only the rows in view — every row is exactly one line tall, so the visible
    /// range follows from the scroll offset.
    /// </summary>
    private void DrawVirtualRows(int count, int scrollTo, Action<int> drawRow)
    {
        float rowH   = ImGui.GetTextLineHeightWithSpacing();
        float startY = ImGui.GetCursorPosY();

        if (_scrollToSelection)
        {
            _scrollToSelection = false;
            if (scrollTo >= 0)
                ImGui.SetScrollY(Math.Max(0, scrollTo * rowH - ImGui.GetWindowHeight() * 0.4f));
        }

        int first = Math.Max(0, (int)((ImGui.GetScrollY() - startY) / rowH) - 1);
        int last  = Math.Min(count, first + (int)(ImGui.GetWindowHeight() / rowH) + 3);

        ImGui.SetCursorPosY(startY + first * rowH);
        for (int i = first; i < last; i++)
            drawRow(i);

        ImGui.SetCursorPosY(startY + count * rowH);
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawRow(uint key, string name, string id, bool configured, Action select, string tooltip)
    {
        bool sel = key == _session.SubjectKey;
        if (configured) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        if (ImGui.Selectable($"{(configured ? "● " : "   ")}{name}##k{key}", sel))
            select();
        if (configured) ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(configured ? $"{tooltip}\n\nYou have a set-up saved for this." : tooltip);

        ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(id).X);
        ImGui.TextColored(Theme.Faint, id);
    }

    private void DrawSelectionSummary()
    {
        var subject = _session.Subject;
        if (subject == null)
        {
            Ui.Hint("Nothing selected.");
            return;
        }

        Ui.Icon(subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User, Theme.Accent);
        ImGui.SameLine();
        ImGui.TextUnformatted(subject.DisplayName);
        Ui.Tooltip(subject is GearSubject gear
            ? $"{SlotInfo.DisplayLabelMap[gear.Item.Slot]} · model {gear.Item.ModelIdDisplay}"
            : subject.IdDisplay);
    }

    private static bool HasWork(Configuration cfg, uint key)
        => (cfg.MaterialsByItem.TryGetValue(key, out var m) && m.Count > 0)
        || (cfg.ModelsByItem.TryGetValue(key, out var r) && r.Any(x => !string.IsNullOrWhiteSpace(x.SourcePath)));

    // ─────────────────────────────────────────────────────────────────────────
    // Loading / filtering
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Restores the last subject once the item cache is ready, switching kind, slot or race to show it.</summary>
    private void FirstLoad()
    {
        _loaded = true;
        var subject = PortSubject.FromKey(_plugin.Configuration.LastItemRowId, _plugin.GameData);
        switch (subject)
        {
            case GearSubject gear:
                _kind = SubjectKind.Gear;
                int slotIdx = Array.IndexOf(_allSlots, gear.Item.Slot);
                if (slotIdx >= 0) _slotIndex = slotIdx;
                break;
            case FeatureSubject feature:
                _kind = feature.Kind;
                _featureRace = feature.Race.RaceCode;
                break;
        }

        Reload();
        if (subject != null)
        {
            _session.SelectSubject(subject);
            _scrollToSelection = true;
        }
    }

    private void Reload()
    {
        if (_kind == SubjectKind.Gear)
            _allItems = _plugin.GameData.GetAllItemsForSlot(_allSlots[_slotIndex]);
        ApplyFilter();
    }

    private RaceGender? CurrentRace()
    {
        foreach (var rg in RaceInfo.AllRaces)
            if (rg.RaceCode == _featureRace)
                return rg;
        return null;
    }

    private IReadOnlyList<ushort> AllIdsForRace()
    {
        var ids = _plugin.GameData.GetFeatureIds(_kind);
        var race = CurrentRace();
        return ids != null && race != null && ids.TryGetValue(race.Value, out var list) ? list : Array.Empty<ushort>();
    }

    private void ApplyFilter()
    {
        var cfg = _plugin.Configuration;
        var q = _search.Trim();

        if (_kind != SubjectKind.Gear)
        {
            var race = CurrentRace();
            IEnumerable<ushort> ids = AllIdsForRace();
            if (race != null && _configuredOnly)
                ids = ids.Where(id => HasWork(cfg, new FeatureSubject(_kind, race.Value, id).Key));
            if (q.Length > 0)
                ids = ids.Where(id => id.ToString().Contains(q, StringComparison.Ordinal) || id.ToString("D4").Contains(q, StringComparison.Ordinal));
            _featureIds = ids.ToList();
            return;
        }

        IEnumerable<GameDataService.GameItem> source = _allItems;
        if (_configuredOnly)
            source = source.Where(i => HasWork(cfg, i.RowId));

        if (q.Length == 0)
        {
            _filtered = source as List<GameDataService.GameItem> ?? source.ToList();
            return;
        }

        bool StartsWithQ(GameDataService.GameItem i) =>
            i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdPadded.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdDisplay.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        bool ContainsQ(GameDataService.GameItem i) =>
            i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdPadded.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdDisplay.Contains(q, StringComparison.OrdinalIgnoreCase);

        var list = source.ToList();
        var starts   = list.Where(StartsWithQ).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var contains = list.Where(i => !StartsWithQ(i) && ContainsQ(i)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        _filtered = starts.Concat(contains).ToList();
    }

    private void SaveBrowserState()
    {
        var cfg = _plugin.Configuration;
        cfg.LastBrowserKind = Array.IndexOf(Kinds, _kind);
        cfg.LastSlotIndex   = _slotIndex;
        cfg.LastItemSearch  = _search;
        cfg.LastFeatureRace = _featureRace;
        cfg.Save();
    }
}
