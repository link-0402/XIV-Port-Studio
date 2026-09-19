using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Stage 4 — build the mod. The canvas shows the running build's progress, then the
/// report: every file grouped by kind, with its outcome, and clicking a row jumps to the
/// set-up that produced it. The inspector is the pre-flight check.
/// </summary>
internal sealed class BuildPanel : IStagePanel
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;
    private readonly BuildController _builds;
    private bool _problemsOnly;

    public BuildPanel(Plugin plugin, PortSession session, BuildController builds)
    {
        _plugin  = plugin;
        _session = session;
        _builds  = builds;
    }

    public StageId         Id      => StageId.Build;
    public string          Title   => "Build";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Hammer;
    public string          Purpose => "Write the mod into Penumbra's mod folder and see what was produced.";

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        var subject = _session.Subject;
        if (subject == null)
        {
            Ui.Hint("Select an item or feature on the left first.");
            return;
        }

        Ui.SectionHeader("Build");
        var modName = PortSession.SanitizeFolderName(string.IsNullOrWhiteSpace(_session.ModName)
            ? PortSession.DefaultModName(subject) : _session.ModName.Trim());
        Ui.Label("Mod");
        ImGui.TextUnformatted(modName);
        ImGui.SameLine();
        if (ImGui.SmallButton("Edit##modname"))
            _session.GoToStage(StageId.Item);

        var root = _plugin.PenumbraIpc.IsAvailable ? _plugin.PenumbraIpc.GetModDirectory() : null;
        Ui.Label("Folder");
        if (string.IsNullOrEmpty(root))
            ImGui.TextColored(Theme.Bad, "Penumbra not available");
        else
            ImGui.TextColored(Theme.Muted, Path.Combine(root, modName));

        ImGui.Spacing();
        DrawBuildControls();

        ImGui.Spacing();
        ImGui.Spacing();

        var report = _builds.LastReport;
        if (report == null || _builds.LastReportItem != subject.Key)
        {
            Ui.HintWrapped("No build yet for this item or feature. Building converts every texture, writes the materials and models, " +
                           "and registers the mod with Penumbra. Rebuilding overwrites the same mod folder and keeps its id, " +
                           "so Penumbra remembers your settings for it.");
            return;
        }

        DrawReport(report);
    }

    private void DrawBuildControls()
    {
        var job = _builds.Running;
        if (job != null)
        {
            var p = job.Progress;
            ImGui.ProgressBar(p.Fraction, new Vector2(-(Theme.S(90) + ImGui.GetStyle().ItemSpacing.X), 0),
                p.Total > 0 ? $"{p.Done}/{p.Total}" : "");
            ImGui.SameLine();
            if (Ui.IconTextButton(FontAwesomeIcon.Stop, "Cancel", "Stops after the current file.", Theme.S(90)))
                job.Cancel();
            ImGui.TextColored(Theme.Muted, p.Current);
            return;
        }

        bool can = _builds.CanBuild(out var reason);
        using (Ui.Disabled(!can))
        {
            if (Ui.PrimaryButton(FontAwesomeIcon.Hammer, "Build mod", Theme.S(160)))
                _builds.Start();
        }
        if (!can)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, reason);
        }
        else
        {
            int errors = _session.Issues.Count(i => i.Severity == Severity.Error);
            if (errors > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Bad, $"{errors} problem(s) will make parts of the build fail — see the checklist.");
            }
        }
    }

    private void DrawReport(BuildReport report)
    {
        var color = report.FatalError != null || report.Failed > 0 ? Theme.Bad
                  : report.Cancelled || report.Skipped > 0         ? Theme.Warn
                  : Theme.Good;
        var icon  = color == Theme.Good ? FontAwesomeIcon.CheckCircle
                  : color == Theme.Warn ? FontAwesomeIcon.ExclamationTriangle
                  : FontAwesomeIcon.TimesCircle;

        Ui.Icon(icon, color);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(report.Summary());
        ImGui.PopStyleColor();

        if (report.AddModResult != null)
        {
            if (report.RegisteredWithPenumbra)
                ImGui.TextColored(Theme.Muted, "Registered with Penumbra — enable it in your collection and redraw.");
            else
                ImGui.TextColored(Theme.Warn, $"Penumbra did not pick it up automatically ({report.AddModResult}) — run a mod rediscovery.");
        }

        ImGui.Spacing();
        if (Ui.IconTextButton(FontAwesomeIcon.Clipboard, "Copy report"))
        {
            ImGui.SetClipboardText(report.ToClipboardText());
            _session.Notify("Build report copied to the clipboard.", Severity.Info);
        }
        ImGui.SameLine();
        using (Ui.Disabled(!Directory.Exists(report.ModPath)))
        {
            if (Ui.IconTextButton(FontAwesomeIcon.FolderOpen, "Open mod folder"))
                OpenFolder(report.ModPath);
        }
        ImGui.SameLine();
        Ui.AlignRight(ImGui.GetFrameHeight() + ImGui.CalcTextSize("Problems only").X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.Checkbox("Problems only", ref _problemsOnly);

        ImGui.Spacing();

        var entries = _problemsOnly ? report.Entries.Where(e => e.Outcome != BuildOutcome.Ok).ToList() : report.Entries;
        if (entries.Count == 0)
        {
            Ui.Hint(_problemsOnly ? "Nothing was skipped or failed." : "The build produced no files.");
            return;
        }

        if (!ImGui.BeginTable("##Report", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY
                                          | ImGuiTableFlags.PadOuterX, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##outcome", ImGuiTableColumnFlags.WidthFixed, Theme.S(20));
        ImGui.TableSetupColumn("File",    ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        BuildCategory? lastCategory = null;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Category != lastCategory)
            {
                lastCategory = e.Category;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Accent, CategoryTitle(e.Category));
            }

            ImGui.TableNextRow();
            ImGui.PushID(i);

            ImGui.TableNextColumn();
            Ui.Icon(e.Outcome switch
            {
                BuildOutcome.Ok      => FontAwesomeIcon.Check,
                BuildOutcome.Skipped => FontAwesomeIcon.Minus,
                _                    => FontAwesomeIcon.Times,
            }, e.Outcome switch
            {
                BuildOutcome.Ok      => Theme.Good,
                BuildOutcome.Skipped => Theme.Warn,
                _                    => Theme.Bad,
            });

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{e.Label}##entry", false, ImGuiSelectableFlags.SpanAllColumns) && e.Source != null)
                _session.Focus(e.Source.Value);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip((e.GamePath ?? e.Label) + (e.Source != null ? "\n\nClick to open its set-up." : ""));

            ImGui.TableNextColumn();
            if (!string.IsNullOrEmpty(e.Detail))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, e.Outcome == BuildOutcome.Failed ? Theme.Bad : Theme.Muted);
                ImGui.TextWrapped(e.Detail);
                ImGui.PopStyleColor();
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static string CategoryTitle(BuildCategory category) => category switch
    {
        BuildCategory.Model    => "Models",
        BuildCategory.Texture  => "Textures",
        BuildCategory.Variant  => "Texture variants",
        BuildCategory.Material => "Materials",
        _                      => "Mod metadata",
    };

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _session.Notify($"Could not open the folder: {ex.Message}", Severity.Error);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — pre-flight checklist
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        if (_session.Subject == null)
        {
            Ui.Hint("Nothing selected.");
            return;
        }

        Ui.SectionHeader("Pre-flight", "Checked continuously against your set-up and the files on disk. Click a line to go to it.");

        int errors   = _session.Issues.Count(i => i.Severity == Severity.Error);
        int warnings = _session.Issues.Count(i => i.Severity == Severity.Warning);
        if (errors + warnings > 0)
        {
            ImGui.TextColored(errors > 0 ? Theme.Bad : Theme.Warn,
                $"{errors} error(s), {warnings} warning(s)");
            ImGui.Spacing();
        }

        IssueList.Draw(_session, _session.Issues);

        ImGui.Spacing();
        ImGui.Spacing();
        Ui.SectionHeader("What gets built");
        int textures = _session.Materials.Sum(m => m.Textures.Count);
        int variants = _session.Materials.Sum(m => m.Textures.Count(t => t.UseVariants && !t.UseWhiteDummy));
        Row(FontAwesomeIcon.Cube,    $"{_session.Models.Count} race model(s)");
        Row(FontAwesomeIcon.Palette, $"{_session.ModelMaterialSlots.Count} material file(s) from {_session.Materials.Count} material(s)");
        Row(FontAwesomeIcon.Image,   $"{textures} texture slot(s){(variants > 0 ? $", {variants} with variants" : "")}");
    }

    private static void Row(FontAwesomeIcon icon, string text)
    {
        Ui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);
    }
}
