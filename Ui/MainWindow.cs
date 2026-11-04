using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using PlaceStage.Convert;

namespace PlaceStage.Ui;

internal sealed class MainWindow : Window
{
    private readonly IClientState clientState;
    private readonly LayoutConverter converter;
    private readonly FileDialogManager dialogs = new();
    private string filePath = string.Empty;
    private string message = string.Empty;

    public MainWindow(IClientState clientState, IDataManager dataManager, IPluginLog log)
        : base("Place Stage")
    {
        this.clientState = clientState;
        this.converter = new LayoutConverter(dataManager, log);
        this.Size = new Vector2(520, 230);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 180),
            MaximumSize = new Vector2(900, 500),
        };
    }

    public void DrawDialogs() => this.dialogs.Draw();

    public override void Draw()
    {
        ImGui.TextWrapped("Pick a MakePlace .json. Positions and rotations stay as they are in that file, not turned to face your character. Open the stage inside the house.");
        ImGui.Spacing();

        var buttonWidth = 88f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputText("##path", ref this.filePath, 1024);
        ImGui.SameLine();
        if (ImGui.Button("Browse", new Vector2(buttonWidth, 0)))
        {
            this.dialogs.OpenFileDialog(
                "Open a MakePlace layout",
                "MakePlace layout{.json}",
                this.OnFileChosen);
        }

        ImGui.Spacing();
        if (ImGui.Button("Convert"))
            this.message = this.Convert();

        if (this.message.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(this.message);
        }
    }

    private void OnFileChosen(bool selected, string path)
    {
        if (!selected || string.IsNullOrWhiteSpace(path))
            return;

        this.filePath = path;
        this.message = string.Empty;
    }

    private string Convert()
    {
        if (!this.clientState.IsLoggedIn)
            return "Log into a character first. The furniture models come from the game.";

        return this.converter.Convert(this.filePath.Trim().Trim('"'));
    }
}
