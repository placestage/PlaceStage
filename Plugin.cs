using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PlaceStage.Ui;

namespace PlaceStage;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/placestage";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("PlaceStage");
    private readonly MainWindow window;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.window = new MainWindow(clientState, dataManager, log);
        this.windowSystem.AddWindow(this.window);

        if (commandManager.Commands.ContainsKey(CommandName))
            commandManager.RemoveHandler(CommandName);

        commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open Place Stage.",
        });

        pluginInterface.UiBuilder.Draw += this.DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += this.Open;
        pluginInterface.UiBuilder.OpenConfigUi += this.Open;
        log.Information("Place Stage is ready. Type {Command} to open it.", CommandName);
    }

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.Open;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.Open;
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
        this.commandManager.RemoveHandler(CommandName);
        this.windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string args) => this.Open();

    private void Open() => this.window.IsOpen = true;

    private void DrawUi()
    {
        this.windowSystem.Draw();
        this.window.DrawDialogs();
    }
}
