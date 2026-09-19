using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Draws validation issues as clickable rows; clicking one opens the stage that owns the
/// offending object and selects it. Used by the status-bar popup and the Build stage.
/// </summary>
internal static class IssueList
{
    public const string PopupId = "##XPSIssues";

    /// <summary>Returns true when a row was clicked (so a popup can close itself).</summary>
    public static bool Draw(PortSession session, IReadOnlyList<PortIssue> issues, bool includeInfo = true)
    {
        var shown = includeInfo ? issues : issues.Where(i => i.Severity != Severity.Info).ToList();
        if (shown.Count == 0)
        {
            Ui.Icon(FontAwesomeIcon.CheckCircle, Theme.Good);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Good, "No problems found.");
            return false;
        }

        bool clicked = false;
        for (int i = 0; i < shown.Count; i++)
        {
            var issue = shown[i];
            ImGui.PushID(i);

            Ui.Icon(IconFor(issue.Severity), Ui.SeverityColor(issue.Severity));
            ImGui.SameLine();

            var stageName = issue.Target.Stage.ToString();
            if (ImGui.Selectable($"{issue.Message}##issue", false, ImGuiSelectableFlags.None))
            {
                session.Focus(issue.Target);
                clicked = true;
            }
            Ui.Tooltip($"Go to {stageName}");

            ImGui.PopID();
        }
        return clicked;
    }

    /// <summary>The popup opened from the status bar's issue badge.</summary>
    public static void DrawPopup(PortSession session)
    {
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(Theme.S(360), 0),
            new System.Numerics.Vector2(Theme.S(640), Theme.S(420)));
        if (!ImGui.BeginPopup(PopupId))
            return;

        Ui.SectionHeader("Problems", "Checked continuously against your set-up and the files on disk.");
        if (Draw(session, session.Issues))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    public static FontAwesomeIcon IconFor(Severity severity) => severity switch
    {
        Severity.Error   => FontAwesomeIcon.TimesCircle,
        Severity.Warning => FontAwesomeIcon.ExclamationTriangle,
        _                => FontAwesomeIcon.InfoCircle,
    };

    /// <summary>
    /// Draws a severity marker for the given target, if it has an issue; otherwise an
    /// invisible spacer of the same width so rows stay aligned.
    /// </summary>
    public static void Marker(PortSession session, Target target)
    {
        var worst = PortValidator.Worst(session.Issues, target);
        if (worst == null)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Dummy(ImGui.CalcTextSize(FontAwesomeIcon.TimesCircle.ToIconString()));
            ImGui.PopFont();
            return;
        }

        Ui.Icon(IconFor(worst.Value.Severity), Ui.SeverityColor(worst.Value.Severity));
        var all = session.Issues.Where(i => i.Target == target).Select(i => i.Message);
        Ui.Tooltip(string.Join("\n", all));
    }
}
