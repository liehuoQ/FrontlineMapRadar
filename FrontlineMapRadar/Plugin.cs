using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FrontlineMapRadar;

public sealed class Plugin : IDalamudPlugin
{
    public const string PluginName = "Frontline Map Radar";
    private const string CommandName = "/flradar";
    private const string RosterCommandName = "/flroster";

    [PluginService] public IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public IClientState ClientState { get; private set; } = null!;
    [PluginService] public IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public IDataManager DataManager { get; private set; } = null!;
    [PluginService] public IGameGui GameGui { get; private set; } = null!;
    [PluginService] public IPluginLog Log { get; private set; } = null!;
    [PluginService] public IFramework Framework { get; private set; } = null!;
    [PluginService] public IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] public ITargetManager TargetManager { get; private set; } = null!;

    public Configuration Config { get; private set; } = null!;
    public PlayerRosterService Roster { get; private set; } = null!;
    public RadarFeedService RadarFeed { get; private set; } = null!;
    public RadarHttpServer RadarHttp { get; private set; } = null!;
    public FrontlineLbTracker LbTracker { get; private set; } = null!;
    public WindowSystem WindowSystem { get; } = new("FrontlineMapRadar");

    private ConfigWindow configWindow = null!;
    private RosterWindow rosterWindow = null!;
    private MapOverlay mapOverlay = null!;
    private FocusLbHud focusLbHud = null!;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(this);

        FrontlineLbCatalog.InitializeFromSheets(DataManager, Log);
        LbTracker = new FrontlineLbTracker(this);

        Roster = new PlayerRosterService(this);
        RadarFeed = new RadarFeedService(this);
        RadarHttp = new RadarHttpServer(this, RadarFeed);

        configWindow = new ConfigWindow(this);
        rosterWindow = new RosterWindow(this);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(rosterWindow);

        mapOverlay = new MapOverlay(this);
        focusLbHud = new FocusLbHud(this);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开设置；/flradar roster 名册；/flradar remote 远程雷达",
        });
        CommandManager.AddHandler(RosterCommandName, new CommandInfo((_, _) => ToggleRoster())
        {
            HelpMessage = "打开纷争前线本场玩家名册",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleRoster;

        RadarHttp.ApplyConfig();
        Log.Information($"{PluginName} loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleRoster;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(RosterCommandName);

        RadarHttp.Dispose();
        RadarFeed.Dispose();
        focusLbHud.Dispose();
        mapOverlay.Dispose();
        LbTracker.Dispose();
        Roster.Dispose();
        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        rosterWindow.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var a = args.Trim();
        if (a.StartsWith("roster", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("list", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("名册", StringComparison.OrdinalIgnoreCase))
        {
            ToggleRoster();
            return;
        }

        if (a.StartsWith("remote", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("web", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("平板", StringComparison.OrdinalIgnoreCase))
        {
            Config.RemoteRadarEnabled = !Config.RemoteRadarEnabled;
            Config.Save();
            RadarHttp.ApplyConfig();
            ToggleConfigUi();
            return;
        }

        ToggleConfigUi();
    }

    public void ToggleConfigUi() => configWindow.Toggle();

    public void ToggleRoster() => rosterWindow.Toggle();
}
