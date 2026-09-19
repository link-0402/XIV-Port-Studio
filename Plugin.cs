using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio;

public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud services ──────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static ITextureProvider         TextureProvider { get; private set; } = null!;

    // ── Plugin internals ──────────────────────────────────────────────────────
    internal Configuration        Configuration  { get; }
    internal PenumbraIpcService   PenumbraIpc    { get; }
    internal GameDataService      GameData       { get; }

    /// <summary>The set-up of the selected item: what every stage of the main window edits.</summary>
    internal PortSession           Session    { get; }
    internal BuildController       Builds     { get; }
    internal TextureThumbnailCache Thumbnails { get; }

    public   readonly WindowSystem WindowSystem = new("XIVPortStudio");
    private  ConfigWindow          ConfigWindow     { get; }
    internal MainWindow            MainWindow       { get; }
    private  SimsImportWindow      SimsImportWindow { get; }

    private const string CommandName   = "/xps";
    private const string CommandConfig = "/xpsconfig";
    private const string CommandImport = "/xpsimport";

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        PenumbraIpc = new PenumbraIpcService(PluginInterface, Log);
        GameData    = new GameDataService(DataManager, Log);

        // Reading the whole Item sheet takes a moment; do it off the render thread so
        // loading the plugin does not hitch the game. The item browser shows a wait state.
        _ = GameData.WarmUpAsync();

        Session    = new PortSession(this);
        Builds     = new BuildController(this, Session);
        Thumbnails = new TextureThumbnailCache();

        // ── Windows ───────────────────────────────────────────────────────────
        ConfigWindow     = new ConfigWindow(this);
        MainWindow       = new MainWindow(this, Session, Builds, Thumbnails);
        SimsImportWindow = new SimsImportWindow(this, Thumbnails);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(SimsImportWindow);

        // ── Commands ──────────────────────────────────────────────────────────
        CommandManager.AddHandler(CommandName, new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open the XIV Port Studio window."
        });
        CommandManager.AddHandler(CommandConfig, new CommandInfo(OnConfigCommand)
        {
            HelpMessage = "Open the XIV Port Studio configuration."
        });
        CommandManager.AddHandler(CommandImport, new CommandInfo(OnImportCommand)
        {
            HelpMessage = "Open the Sims 4 package importer."
        });

        // ── UI hooks ──────────────────────────────────────────────────────────
        PluginInterface.UiBuilder.Draw          += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi  += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi    += ToggleMainUi;

        // ── Penumbra lifecycle ────────────────────────────────────────────────
        PenumbraIpc.PenumbraInitialized += OnPenumbraInitialized;
        PenumbraIpc.PenumbraDisposed    += OnPenumbraDisposed;

        Log.Information("[XPS] XIV Port Studio loaded.");
    }

    public void Dispose()
    {
        // Edits are saved in batches; write whatever is still pending before anything goes away.
        Session.Flush();
        Builds.Cancel();

        PluginInterface.UiBuilder.Draw         -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;

        PenumbraIpc.PenumbraInitialized -= OnPenumbraInitialized;
        PenumbraIpc.PenumbraDisposed    -= OnPenumbraDisposed;

        PenumbraIpc.Dispose();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
        SimsImportWindow.Dispose();
        Thumbnails.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandConfig);
        CommandManager.RemoveHandler(CommandImport);
    }

    private void DrawUi()
    {
        Thumbnails.NewFrame();
        Builds.Update();   // finishes a build even while the main window is closed
        WindowSystem.Draw();

        // One shared file dialog, drawn once after every window.
        Ui.Dialogs.Draw();
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    private void OnMainCommand   (string cmd, string args) => MainWindow.Toggle();
    private void OnConfigCommand (string cmd, string args) => ConfigWindow.Toggle();
    private void OnImportCommand (string cmd, string args) => SimsImportWindow.Toggle();

    public void ToggleMainUi()     => MainWindow.Toggle();
    public void ToggleConfigUi()   => ConfigWindow.Toggle();
    public void ToggleSimsImport() => SimsImportWindow.Toggle();

    // ── Penumbra lifecycle callbacks ──────────────────────────────────────────

    private void OnPenumbraInitialized() => Log.Information("[XPS] Penumbra became available.");

    private void OnPenumbraDisposed() => Log.Warning("[XPS] Penumbra became unavailable.");
}
