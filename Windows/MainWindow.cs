using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Services.Sims;
using XIVPortStudio.Windows.Panels;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows;

/// <summary>
/// The studio window. Three regions under a toolbar, above a status bar:
///   left   – item browser: the item the port replaces;
///   middle – stage tabs (Item → Models → Materials → Build) and the active stage's canvas;
///   right  – inspector for whatever the canvas has selected.
/// Both dividers are draggable. The window itself only lays out regions and runs the
/// per-frame ticks (saving, validation, build completion); the stages draw themselves.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan RevalidateEvery = TimeSpan.FromSeconds(2);
    private const string ConfirmResetId = "Reset?##XPSConfirmReset";

    private readonly Plugin _plugin;
    private readonly PortSession _session;
    private readonly BuildController _builds;
    private readonly ItemBrowserPanel _browser;
    private readonly IStagePanel[] _stages;

    private int _validatedRevision = -1;
    private readonly Stopwatch _sinceValidated = new();
    private bool _penumbraAvailable;

    internal MainWindow(Plugin plugin, PortSession session, BuildController builds, TextureThumbnailCache thumbnails)
        : base("XIV Port Studio###XPSMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin  = plugin;
        _session = session;
        _builds  = builds;

        Size          = new Vector2(1180, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(820, 460) };

        _browser = new ItemBrowserPanel(plugin, session);
        _stages  = new IStagePanel[]
        {
            new ItemStagePanel(plugin, session),
            new ModelsPanel(plugin, session),
            new MaterialsPanel(plugin, session, thumbnails),
            new BuildPanel(plugin, session, builds),
        };
    }

    public void Dispose() { }

    public override void OnClose() => _session.Flush();

    // ─────────────────────────────────────────────────────────────────────────
    // Frame
    // ─────────────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        _penumbraAvailable = _plugin.PenumbraIpc.IsAvailable;
        Revalidate();

        DrawToolbar();

        float statusH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        float bodyH   = ImGui.GetContentRegionAvail().Y - statusH;
        DrawBody(bodyH);
        DrawStatusBar();

        _stages[(int)_session.ActiveStage].DrawPopups();
        DrawResetPopup();
        IssueList.DrawPopup(_session);

        _session.FlushIfDue();
    }

    /// <summary>Re-runs the validator after edits, and every few seconds for files changing on disk.</summary>
    private void Revalidate()
    {
        if (_session.Revision == _validatedRevision && _sinceValidated.IsRunning && _sinceValidated.Elapsed < RevalidateEvery)
            return;

        _validatedRevision = _session.Revision;
        _sinceValidated.Restart();
        _session.Issues = PortValidator.Validate(_session, _plugin.GameData, _penumbraAvailable);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Toolbar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        if (Ui.IconTextButton(FontAwesomeIcon.FileImport, "Import Sims 4 package",
                "Pulls meshes and textures out of a Sims 4 .package and lays them out for the material editor."))
            _plugin.ToggleSimsImport();

        ImGui.SameLine();
        using (Ui.Disabled(_session.Subject == null))
        {
            if (Ui.IconTextButton(FontAwesomeIcon.Undo, "Reset",
                    "Clears all models, materials and the mod name configured for the selected item or feature."))
                _openReset = true;
        }

        float gear = ImGui.GetFrameHeight();
        Ui.AlignRight(gear);
        if (Ui.IconButton(FontAwesomeIcon.Cog, "Settings", "Settings"))
            _plugin.ToggleConfigUi();

        ImGui.Spacing();
    }

    private bool _openReset;

    private void DrawResetPopup()
    {
        if (_openReset)
        {
            _openReset = false;
            ImGui.OpenPopup(ConfirmResetId);
        }

        if (!ImGui.BeginPopupModal(ConfirmResetId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextUnformatted($"Reset all models, materials and the mod name for \"{_session.Subject?.DisplayName}\"?");
        ImGui.TextColored(Theme.Bad, "This cannot be undone.");
        ImGui.Spacing();
        if (ImGui.Button("Reset", new Vector2(Theme.S(100), 0)))
        {
            _session.ResetItem();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body: browser | stage canvas | inspector
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawBody(float height)
    {
        var cfg   = _plugin.Configuration;
        float total = ImGui.GetContentRegionAvail().X;
        float split = Theme.SplitterWidth;
        float minW  = Theme.S(Theme.MinPaneWidth);

        float browserW   = Math.Clamp(Theme.S(cfg.BrowserWidth),   minW, Math.Max(minW, total * 0.4f));
        float inspectorW = Math.Clamp(Theme.S(cfg.InspectorWidth), minW, Math.Max(minW, total - browserW - minW - split * 2));

        if (ImGui.BeginChild("##Browser", new Vector2(browserW, height), true))
            _browser.Draw();
        ImGui.EndChild();

        // While a divider is dragged its width is written through every frame (the next frame
        // reads it back from the config) and saved once the drag ends. Only while dragging:
        // otherwise shrinking the window would clamp and permanently overwrite the widths.
        bool released = Ui.VerticalSplitter("##SplitLeft", ref browserW, minW, total - inspectorW - minW - split * 2, height);
        if (ImGui.IsItemActive() || released)
            cfg.BrowserWidth = browserW / Theme.S(1);
        if (released)
            cfg.Save();

        float middleW = total - browserW - inspectorW - split * 2;
        ImGui.BeginGroup();
        DrawStageStrip(middleW);
        var active = _stages[(int)_session.ActiveStage];
        if (ImGui.BeginChild("##Canvas", new Vector2(middleW, height - ImGui.GetFrameHeightWithSpacing()), true))
            active.DrawCanvas();
        ImGui.EndChild();
        ImGui.EndGroup();

        released = Ui.VerticalSplitter("##SplitRight", ref inspectorW, minW, total - browserW - minW - split * 2, height, invert: true);
        if (ImGui.IsItemActive() || released)
            cfg.InspectorWidth = inspectorW / Theme.S(1);
        if (released)
            cfg.Save();

        if (ImGui.BeginChild("##Inspector", new Vector2(inspectorW, height), true))
            active.DrawInspector();
        ImGui.EndChild();
    }

    /// <summary>
    /// The stage tabs, drawn as a segmented strip so each can carry a coloured issue count.
    /// Numbered, because the stages are a workflow, not just sections.
    /// </summary>
    private void DrawStageStrip(float width)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float tabW = (width - spacing * (_stages.Length - 1)) / _stages.Length;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        for (int i = 0; i < _stages.Length; i++)
        {
            var stage = _stages[i];
            bool active = _session.ActiveStage == stage.Id;
            if (i > 0) ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button,        active ? Theme.Accent with { W = 0.45f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent with { W = active ? 0.55f : 0.25f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Accent with { W = 0.65f });
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            bool clicked = ImGui.Button($"{i + 1}  {stage.Title}##stage{i}", new Vector2(tabW, 0));
            ImGui.PopStyleColor(4);
            Ui.Tooltip(stage.Purpose);
            if (clicked)
                _session.GoToStage(stage.Id);

            DrawStageBadge(stage.Id);
        }
        ImGui.PopStyleVar();
    }

    /// <summary>A small count bubble in the top-right corner of the tab just drawn.</summary>
    private void DrawStageBadge(StageId stage)
    {
        int errors   = PortValidator.Count(_session.Issues, stage, Severity.Error);
        int warnings = PortValidator.Count(_session.Issues, stage, Severity.Warning);
        if (errors + warnings == 0)
            return;

        var color = errors > 0 ? Theme.Bad : Theme.Warn;
        var text  = (errors > 0 ? errors : warnings).ToString();
        var max   = ImGui.GetItemRectMax();
        var min   = ImGui.GetItemRectMin();
        var size  = ImGui.CalcTextSize(text);
        float r   = MathF.Max(size.X, size.Y) * 0.5f + Theme.S(3);
        var center = new Vector2(max.X - r - Theme.S(4), (min.Y + max.Y) * 0.5f);

        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, r, ImGui.GetColorU32(color));
        draw.AddText(center - size * 0.5f, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1f)), text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status bar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawStatusBar()
    {
        ImGui.Separator();

        // Penumbra
        Ui.Icon(FontAwesomeIcon.Circle, _penumbraAvailable ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(_penumbraAvailable ? Theme.Muted : Theme.Bad, _penumbraAvailable ? "Penumbra" : "Penumbra unavailable");
        Ui.Tooltip(_penumbraAvailable
            ? $"Connected. {(_plugin.GameData.IsItemCacheReady ? $"{_plugin.GameData.CacheCount} items cached." : "")}"
            : "The item browser still works, but mods cannot be built until Penumbra is loaded.");

        // Issues
        ImGui.SameLine(0, Theme.S(18));
        int errors = 0, warnings = 0;
        foreach (var issue in _session.Issues)
        {
            if (issue.Severity == Severity.Error) errors++;
            else if (issue.Severity == Severity.Warning) warnings++;
        }

        var issueColor = errors > 0 ? Theme.Bad : warnings > 0 ? Theme.Warn : Theme.Good;
        var issueIcon  = errors > 0 ? FontAwesomeIcon.TimesCircle : warnings > 0 ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.CheckCircle;
        var issueText  = _session.Subject == null ? "Nothing selected"
                       : _session.Models.Count == 0 && _session.Materials.Count == 0 ? "Nothing set up yet"
                       : errors + warnings == 0 ? "Ready to build"
                       : $"{errors} error(s), {warnings} warning(s)";

        ImGui.PushStyleColor(ImGuiCol.Text, issueColor);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        bool open = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(issueIcon, issueText);
        ImGui.PopStyleColor(2);
        Ui.Tooltip("Show all problems");
        if (open)
            ImGui.OpenPopup(IssueList.PopupId);

        float buildW = Theme.S(150);

        // Last action result, cut to the room left before the build button.
        var status = _session.LiveStatus;
        if (status != null)
        {
            ImGui.SameLine(0, Theme.S(18));
            float room = ImGui.GetContentRegionMax().X - buildW - ImGui.GetCursorPosX() - Theme.S(18);
            if (room > Theme.S(40))
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Ui.SeverityColor(status.Severity), Fit(status.Text, room));
                Ui.Tooltip(status.Text);
            }
        }

        // Primary action
        Ui.AlignRight(buildW);
        var job = _builds.Running;
        if (job != null)
        {
            ImGui.ProgressBar(job.Progress.Fraction, new Vector2(buildW, 0), "Building…");
            Ui.Tooltip(job.Progress.Current);
            return;
        }

        bool can = _builds.CanBuild(out var reason);
        using (Ui.Disabled(!can))
        {
            if (Ui.PrimaryButton(FontAwesomeIcon.Hammer, "Build mod", buildW, can ? "Build the mod into Penumbra's mod folder." : reason))
                _builds.Start();
        }
    }

    /// <summary>Shortens <paramref name="text"/> with an ellipsis until it fits <paramref name="width"/>.</summary>
    private static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;

        int len = text.Length;
        while (len > 1 && ImGui.CalcTextSize(text[..len] + "…").X > width)
            len = len * 9 / 10;
        return text[..len].TrimEnd() + "…";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sims 4 importer hand-off
    // ─────────────────────────────────────────────────────────────────────────

    internal string ApplySimsExtraction(SimsPackageInspector.ExtractionResult extraction)
        => _session.ApplySimsExtraction(extraction);
}
