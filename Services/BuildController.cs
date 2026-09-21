using System.Linq;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

/// <summary>
/// Starts builds, tracks the running one, and finishes it on the render thread: once the
/// worker is done, the mod is registered with Penumbra here rather than from the worker,
/// and the result is kept for the Build stage to show.
/// </summary>
internal sealed class BuildController
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;

    public BuildController(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
    }

    /// <summary>The build in progress, or null.</summary>
    public BuildJob? Running { get; private set; }

    /// <summary>The most recent finished build — one mod made from every item that was in the pack.</summary>
    public BuildReport? LastReport { get; private set; }

    public bool IsRunning => Running != null;

    /// <summary>Whether a build can start now, and if not, why.</summary>
    public bool CanBuild(out string reason)
    {
        var items = _session.Items;
        if (Running != null)                     { reason = "A build is already running.";                             return false; }
        if (items.Count == 0)                    { reason = "Add at least one item to the modpack first.";              return false; }
        if (!items.Any(i => i.HasWork))          { reason = "Add a race model or a material to at least one item.";     return false; }
        if (!_plugin.PenumbraIpc.IsAvailable)    { reason = "Penumbra is not available — cannot resolve the mod directory."; return false; }
        reason = string.Empty;
        return true;
    }

    public void Start()
    {
        if (!CanBuild(out var reason))
        {
            _session.Notify(reason, Severity.Warning);
            return;
        }

        var modRoot = _plugin.PenumbraIpc.GetModDirectory();
        if (string.IsNullOrEmpty(modRoot))
        {
            _session.Notify("Penumbra did not report a mod directory.", Severity.Error);
            return;
        }

        // Save first, so a crash mid-build cannot lose the edits being built.
        _session.Flush();
        var request = _session.CreateBuildRequest(modRoot);
        Running = new BuildJob(request);
        _session.GoToStage(StageId.Build);
    }

    public void Cancel() => Running?.Cancel();

    /// <summary>Called once per frame from the render thread.</summary>
    public void Update()
    {
        var job = Running;
        if (job == null || !job.IsDone)
            return;

        Running = null;
        BuildReport report;
        if (job.Task.IsCompletedSuccessfully)
        {
            report = job.Task.Result;
        }
        else
        {
            var itemsSummary = job.Request.Items.Count switch
            {
                0 => "(no items)",
                1 => job.Request.Items[0].Subject.DisplayName,
                _ => $"{job.Request.Items.Count} items",
            };
            report = new BuildReport { ModName = job.Request.ModName, ModPath = job.Request.ModRoot, ItemsSummary = itemsSummary };
            report.FatalError = job.Task.Exception?.GetBaseException().Message ?? "unknown error";
        }

        // Register the new mod folder with Penumbra directly, so it shows up without
        // the user having to run a full mod-directory rediscovery.
        if (report.FatalError == null && !report.Cancelled)
            report.AddModResult = _plugin.PenumbraIpc.AddMod(report.ModName);

        LastReport = report;

        _session.Notify(report.Summary(), report.Succeeded ? Severity.Info : report.Failed > 0 || report.FatalError != null
            ? Severity.Error
            : Severity.Warning);
    }
}
