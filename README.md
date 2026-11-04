# Place Stage

Place Stage turns a MakePlace layout into a Stagehand stage for Final Fantasy XIV. You pick a MakePlace `.json`, and it writes a stage you can open in the house.

The plugin is named Place Stage. The plugin file is `PlaceStage.dll`. Open it with `/placestage`.

You need to be logged into a character. The furniture models come from the game.

## The link to put in Dalamud

Put this link in **Dalamud Settings**, on the **Experimental** tab, in **Custom Plugin Repositories**:

https://raw.githubusercontent.com/placestage/PlaceStage/main/pluginmaster.json

That is the link Dalamud reads. Place Stage then shows up in the plugin installer.

## Convert a layout

1. Open Place Stage with `/placestage`.
2. Press **Browse** and pick a MakePlace `.json`, or paste the path.
3. Press **Convert**.

The stage keeps the positions and rotations from that MakePlace file. It is not turned to face your character. Open the stage inside the house those coordinates belong to. Walls and floors are left out.

Colors stay on the model. Place Stage does not stain the furniture, so a piece keeps the look it was built with.

The stage is saved in your Documents folder, under `Stages`, using the name of the MakePlace file. If that name is already there, the new file gets a number, such as `NCL New 2.json`.

## Build

This plugin targets Dalamud API 15.

```powershell
dotnet build
```

The files Dalamud needs are `bin\Debug\PlaceStage.dll` and `bin\Debug\PlaceStage.json`. In Dalamud settings, open **Dev Plugins** and add the `bin\Debug` folder. Enable Place Stage in the plugin list. After a new build, reload the plugin in game.
