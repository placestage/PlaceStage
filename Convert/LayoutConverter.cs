using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace PlaceStage.Convert;

internal sealed class LayoutConverter
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, ushort> indoor = new();
    private readonly Dictionary<uint, ushort> outdoor = new();
    private readonly Dictionary<(ushort Model, bool Outdoor), ModelPart[]> models = new();
    private bool sheetsLoaded;

    public LayoutConverter(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    public string Convert(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return "Choose a MakePlace file first.";

        if (!File.Exists(sourcePath))
            return "That file is not there.";

        try
        {
            this.LoadSheets();
            using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var root = document.RootElement;
            var inside = this.ReadFurniture(root, "interiorFurniture", outdoor: false);
            var outside = this.ReadFurniture(root, "exteriorFurniture", outdoor: true);
            if (inside.Pieces.Count == 0 && outside.Pieces.Count == 0)
                return inside.Skipped.Count + outside.Skipped.Count == 0
                    ? "That file has no furniture to convert."
                    : "None of the furniture had a model the game could load.";

            var stages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Stages");
            Directory.CreateDirectory(stages);
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var destination = UniquePath(stages, string.IsNullOrWhiteSpace(name) ? "MakePlace" : name);
            WriteStage(destination, Path.GetFileNameWithoutExtension(destination), inside.Pieces, outside.Pieces);

            var placed = inside.Items + outside.Items;
            var skipped = inside.Skipped.Concat(outside.Skipped).ToList();
            var message = new StringBuilder();
            message.Append(CultureInfo.InvariantCulture, $"Saved {Path.GetFileName(destination)}. {placed} pieces.");
            if (skipped.Count > 0)
            {
                message.Append(CultureInfo.InvariantCulture, $" {skipped.Count} had no model");
                var shown = skipped.Take(6).ToList();
                message.Append(": ");
                message.Append(string.Join(", ", shown));
                if (skipped.Count > shown.Count)
                    message.Append(CultureInfo.InvariantCulture, $", and {skipped.Count - shown.Count} more");
                message.Append('.');
            }

            message.Append(" Positions and rotations come from the MakePlace file. Open it inside that house. Walls and floors are left out.");
            this.log.Information("Converted {Source} to {Destination}.", sourcePath, destination);
            return message.ToString();
        }
        catch (JsonException)
        {
            return "That file is not a MakePlace layout.";
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Could not convert {Source}.", sourcePath);
            return "Could not convert that file.";
        }
    }

    private void LoadSheets()
    {
        if (this.sheetsLoaded)
            return;

        foreach (var row in this.data.GetExcelSheet<HousingFurniture>())
        {
            if (row.ModelKey == 0 || row.Item.RowId == 0)
                continue;

            this.indoor.TryAdd(row.Item.RowId, row.ModelKey);
        }

        foreach (var row in this.data.GetExcelSheet<HousingYardObject>())
        {
            if (row.ModelKey == 0 || row.Item.RowId == 0)
                continue;

            this.outdoor.TryAdd(row.Item.RowId, row.ModelKey);
        }

        this.sheetsLoaded = true;
    }

    private FurnitureList ReadFurniture(JsonElement root, string propertyName, bool outdoor)
    {
        var list = new FurnitureList();
        if (!root.TryGetProperty(propertyName, out var furniture) || furniture.ValueKind != JsonValueKind.Array)
            return list;

        var layoutScale = ReadScale(root, outdoor ? "exteriorScale" : "interiorScale");
        foreach (var piece in furniture.EnumerateArray())
            this.AddPiece(list, piece, outdoor, layoutScale);

        return list;
    }

    private void AddPiece(FurnitureList list, JsonElement piece, bool outdoor, float layoutScale)
    {
        this.AddOne(list, piece, outdoor, layoutScale);
        if (piece.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in attachments.EnumerateArray())
                this.AddPiece(list, child, outdoor, layoutScale);
        }
    }

    private void AddOne(FurnitureList list, JsonElement piece, bool outdoor, float layoutScale)
    {
        var name = piece.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        if (!piece.TryGetProperty("itemId", out var idElement) || !TryItemId(idElement, out var itemId))
            return;

        if (string.IsNullOrWhiteSpace(name))
            name = "Item " + itemId.ToString(CultureInfo.InvariantCulture);

        if (!piece.TryGetProperty("transform", out var transform)
            || !transform.TryGetProperty("location", out var location)
            || !TryVector(location, out var makePlaceLocation))
        {
            list.Skipped.Add(name);
            return;
        }

        var parts = this.PartsFor(itemId, outdoor);
        if (parts.Length == 0)
        {
            list.Skipped.Add(name);
            return;
        }

        var rotation = Quaternion.Identity;
        if (transform.TryGetProperty("rotation", out var rotationElement) && TryQuaternion(rotationElement, out var makePlaceRotation))
            rotation = ToGame(makePlaceRotation);

        var scale = Vector3.One;
        if (transform.TryGetProperty("scale", out var scaleElement) && TryScale(scaleElement, out var makePlaceScale))
            scale = new Vector3(makePlaceScale.X, makePlaceScale.Z, makePlaceScale.Y);

        var furniture = new ModelPart(
            string.Empty,
            new Vector3(makePlaceLocation.X, makePlaceLocation.Z, makePlaceLocation.Y) / layoutScale,
            rotation,
            scale,
            false);
        var displayName = name.Trim();
        foreach (var part in parts)
        {
            var placed = Compose(furniture, part);
            list.Pieces.Add(new Piece(
                displayName,
                placed.Position,
                ToDegrees(placed.Rotation),
                placed.Scale,
                part.Model,
                part.Effect));
        }

        list.Items++;
    }

    private ModelPart[] PartsFor(uint itemId, bool preferOutdoor)
    {
        if (!this.TryModelKey(itemId, preferOutdoor, out var model, out var outdoor))
            return [];

        var key = (model, outdoor);
        if (!this.models.TryGetValue(key, out var parts))
        {
            parts = this.LoadParts(model, outdoor);
            this.models[key] = parts;
        }

        return parts;
    }

    private bool TryModelKey(uint itemId, bool preferOutdoor, out ushort model, out bool outdoor)
    {
        if (preferOutdoor && this.outdoor.TryGetValue(itemId, out model))
        {
            outdoor = true;
            return true;
        }

        if (!preferOutdoor && this.indoor.TryGetValue(itemId, out model))
        {
            outdoor = false;
            return true;
        }

        if (this.indoor.TryGetValue(itemId, out model))
        {
            outdoor = false;
            return true;
        }

        if (this.outdoor.TryGetValue(itemId, out model))
        {
            outdoor = true;
            return true;
        }

        model = 0;
        outdoor = preferOutdoor;
        return false;
    }

    private ModelPart[] LoadParts(ushort model, bool outdoor)
    {
        var id = model.ToString("D4", CultureInfo.InvariantCulture);
        var folder = outdoor ? "outdoor" : "indoor";
        var prefix = outdoor ? "gar_b0_m" : "fun_b0_m";
        var sgbPath = $"bgcommon/hou/{folder}/general/{id}/asset/{prefix}{id}.sgb";
        if (this.data.FileExists(sgbPath))
        {
            try
            {
                var sgb = this.data.GetFile<SgbFile>(sgbPath);
                var found = new List<ModelPart>();
                if (sgb != null)
                    this.Collect(sgb, ModelPart.Identity, found, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                if (found.Count > 0)
                    return found.ToArray();
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, "Could not read {Path}.", sgbPath);
            }
        }

        var single = SingleModel(this.data, folder, prefix, id);
        return single == null ? [] : [ModelPart.At(single)];
    }

    private void Collect(SgbFile sgb, ModelPart parent, List<ModelPart> found, int depth, HashSet<string> seen)
    {
        if (sgb.LayerGroups == null || depth > 5)
            return;

        foreach (var group in sgb.LayerGroups)
        {
            if (group.Layers == null)
                continue;

            foreach (var layer in group.Layers)
            {
                if (layer.InstanceObjects == null)
                    continue;

                foreach (var instance in layer.InstanceObjects)
                {
                    var placed = Compose(parent, FromLayer(instance.Transform));
                    switch (instance.Object)
                    {
                        case LayerCommon.BGInstanceObject bg:
                            this.AddModel(bg.AssetPath, placed, found);
                            break;
                        case LayerCommon.VFXInstanceObject vfx:
                            this.AddEffect(vfx.AssetPath, placed, found);
                            break;
                        case LayerCommon.SharedGroupInstanceObject shared:
                            this.AddShared(shared.AssetPath, placed, found, depth, seen);
                            break;
                    }
                }
            }
        }
    }

    private void AddModel(string? assetPath, ModelPart placed, List<ModelPart> found)
    {
        var path = Normalize(assetPath);
        if (!path.EndsWith(".mdl", StringComparison.Ordinal) || path.Contains("collision", StringComparison.Ordinal))
            return;

        if (!this.data.FileExists(path))
            return;

        found.Add(placed with { Model = path, Effect = false });
    }

    private void AddEffect(string? assetPath, ModelPart placed, List<ModelPart> found)
    {
        var path = Normalize(assetPath);
        if (!path.EndsWith(".avfx", StringComparison.Ordinal) || !this.data.FileExists(path))
            return;

        found.Add(placed with { Model = path, Effect = true });
    }

    private void AddShared(string? assetPath, ModelPart placed, List<ModelPart> found, int depth, HashSet<string> seen)
    {
        var path = Normalize(assetPath);
        if (!path.EndsWith(".sgb", StringComparison.Ordinal) || !seen.Add(path) || !this.data.FileExists(path))
            return;

        try
        {
            var nested = this.data.GetFile<SgbFile>(path);
            if (nested != null)
                this.Collect(nested, placed, found, depth + 1, seen);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Could not read {Path}.", path);
        }
    }

    private static string? SingleModel(IDataManager data, string folder, string prefix, string id)
    {
        var stem = $"bgcommon/hou/{folder}/general/{id}/bgparts/{prefix}{id}";
        if (data.FileExists(stem + ".mdl"))
            return stem + ".mdl";

        foreach (var suffix in new[] { "a", "b", "c", "d" })
        {
            var lettered = stem + suffix + ".mdl";
            if (data.FileExists(lettered))
                return lettered;
        }

        return null;
    }

    private static ModelPart FromLayer(Common.Transformation transform)
    {
        var scale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);
        if (scale.LengthSquared() < 0.0001f)
            scale = Vector3.One;

        return new ModelPart(
            string.Empty,
            new Vector3(transform.Translation.X, transform.Translation.Y, transform.Translation.Z),
            Quaternion.CreateFromYawPitchRoll(transform.Rotation.Y, transform.Rotation.X, transform.Rotation.Z),
            scale,
            false);
    }

    private static ModelPart Compose(ModelPart parent, ModelPart local)
    {
        return new ModelPart(
            local.Model,
            parent.Position + Vector3.Transform(local.Position * parent.Scale, parent.Rotation),
            Quaternion.Normalize(parent.Rotation * local.Rotation),
            parent.Scale * local.Scale,
            local.Effect);
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
    }

    private static void WriteStage(string path, string name, List<Piece> inside, List<Piece> outside)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, IndentSize = 4 });
        writer.WriteStartObject();
        writer.WriteNumber("FormatVersion", 2);
        writer.WritePropertyName("Info");
        writer.WriteStartObject();
        writer.WriteString("Name", name);
        writer.WriteString("AuthorName", "");
        writer.WriteString("VersionString", "");
        writer.WriteString("Description", "Converted from a MakePlace layout.");
        writer.WriteNumber("IntendedTerritoryType", 0);
        writer.WriteEndObject();
        writer.WritePropertyName("Objects");
        writer.WriteStartObject();
        if (inside.Count > 0)
            WriteGroup(writer, "inside", "Inside", inside);
        if (outside.Count > 0)
            WriteGroup(writer, "outside", "Outside", outside);
        writer.WriteEndObject();
        writer.WritePropertyName("EmbeddedModpacks");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteGroup(Utf8JsonWriter writer, string id, string name, List<Piece> pieces)
    {
        writer.WritePropertyName(id);
        writer.WriteStartObject();
        writer.WriteString("Type", "Group");
        writer.WriteString("DisplayName", name);
        writer.WriteBoolean("IsDisabled", false);
        WriteVector3(writer, "Position", Vector3.Zero);
        // The group stays on the house axes. Piece rotations come from the MakePlace file.
        WriteVector3(writer, "RotationPitchYawRollDegrees", Vector3.Zero);
        WriteVector3(writer, "Scale", Vector3.One);
        writer.WriteString("ModpackId", "");
        writer.WritePropertyName("Objects");
        writer.WriteStartObject();
        for (var i = 0; i < pieces.Count; i++)
            WritePiece(writer, (i + 1).ToString(CultureInfo.InvariantCulture), pieces[i]);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WritePiece(Utf8JsonWriter writer, string id, Piece piece)
    {
        writer.WritePropertyName(id);
        writer.WriteStartObject();
        writer.WriteString("Type", piece.Effect ? "VfxObject" : "BgObject");
        writer.WriteString("DisplayName", piece.Name);
        writer.WriteBoolean("IsDisabled", false);
        WriteVector3(writer, "Position", piece.Position);
        WriteVector3(writer, "RotationPitchYawRollDegrees", piece.RotationDegrees);
        WriteVector3(writer, "Scale", piece.Scale);
        writer.WriteString("ModpackId", "");
        if (piece.Effect)
        {
            writer.WriteString("VfxGamePath", piece.Model);
            WriteColor(writer, "Color");
        }
        else
        {
            writer.WriteString("ModelGamePath", piece.Model);
            writer.WriteNumber("Opacity", 1);
            // Zero tells Stagehand not to stain the model. A stain was turning blue floats white
            // and hiding pieces whose blue channel was low.
            WriteColor(writer, "DyeColor");
        }

        writer.WriteEndObject();
    }

    private static void WriteColor(Utf8JsonWriter writer, string name)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("X", 0);
        writer.WriteNumber("Y", 0);
        writer.WriteNumber("Z", 0);
        writer.WriteNumber("W", 0);
        writer.WriteEndObject();
    }

    private static void WriteVector3(Utf8JsonWriter writer, string name, Vector3 value)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("X", Clean(value.X));
        writer.WriteNumber("Y", Clean(value.Y));
        writer.WriteNumber("Z", Clean(value.Z));
        writer.WriteEndObject();
    }

    private static float ReadScale(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var scale) && scale.TryGetSingle(out var value) && value > 0.0001f)
            return value;

        return 1f;
    }

    // MakePlace stores a Z-up layout. Game space, and Stagehand, are Y-up.
    private static Quaternion ToGame(Quaternion makePlace)
    {
        Vector3 Map(Vector3 value) => new(value.X, value.Z, value.Y);

        Vector3 Apply(Vector3 game)
        {
            var rotated = Vector3.Transform(Map(game), makePlace);
            return Map(rotated);
        }

        var x = Apply(Vector3.UnitX);
        var y = Apply(Vector3.UnitY);
        var z = Apply(Vector3.UnitZ);
        var matrix = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
    }

    // Same pitch, yaw, and roll split Stagehand uses when it saves a stage.
    private static Vector3 ToDegrees(Quaternion value)
    {
        var x = value.Z;
        var y = value.X;
        var z = value.Y;
        var w = value.W;
        var roll = MathF.Atan2((2 * w * x) + (2 * y * z), 1 - (2 * x * x) - (2 * y * y));
        var pitch = MathF.Asin(Math.Clamp((2 * w * y) - (2 * z * x), -1f, 1f));
        var yaw = MathF.Atan2((2 * w * z) + (2 * x * y), 1 - (2 * y * y) - (2 * z * z));
        return new Vector3(RadiansToDegrees(pitch), RadiansToDegrees(yaw), RadiansToDegrees(roll));
    }

    private static float RadiansToDegrees(float radians) => radians * 180f / MathF.PI;

    private static bool TryItemId(JsonElement element, out uint itemId)
    {
        if (element.TryGetUInt32(out itemId))
            return itemId != 0;

        if (element.TryGetInt32(out var signed) && signed > 0)
        {
            itemId = (uint)signed;
            return true;
        }

        itemId = 0;
        return false;
    }

    private static bool TryVector(JsonElement element, out Vector3 value)
    {
        value = Vector3.Zero;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var numbers = element.EnumerateArray().Take(3).ToList();
        if (numbers.Count < 3 || !numbers.TrueForAll(number => number.TryGetSingle(out _)))
            return false;

        value = new Vector3(numbers[0].GetSingle(), numbers[1].GetSingle(), numbers[2].GetSingle());
        return true;
    }

    private static bool TryQuaternion(JsonElement element, out Quaternion value)
    {
        value = Quaternion.Identity;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var numbers = element.EnumerateArray().Take(4).ToList();
        if (numbers.Count < 4 || !numbers.TrueForAll(number => number.TryGetSingle(out _)))
            return false;

        value = new Quaternion(numbers[0].GetSingle(), numbers[1].GetSingle(), numbers[2].GetSingle(), numbers[3].GetSingle());
        return true;
    }

    private static bool TryScale(JsonElement element, out Vector3 value)
    {
        value = Vector3.One;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var numbers = element.EnumerateArray().Take(3).ToList();
        if (numbers.Count == 0 || !numbers.TrueForAll(number => number.TryGetSingle(out _)))
            return false;

        if (numbers.Count == 1)
        {
            var uniform = numbers[0].GetSingle();
            value = new Vector3(uniform, uniform, uniform);
            return true;
        }

        if (numbers.Count < 3)
            return false;

        value = new Vector3(numbers[0].GetSingle(), numbers[1].GetSingle(), numbers[2].GetSingle());
        return true;
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".json");
        var copy = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{name} {copy}.json");
            copy++;
        }

        return path;
    }

    private static float Clean(float value) => float.IsFinite(value) ? value : 0f;

    private sealed class FurnitureList
    {
        public List<Piece> Pieces { get; } = new();

        public List<string> Skipped { get; } = new();

        public int Items { get; set; }
    }

    private readonly record struct ModelPart(string Model, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool Effect)
    {
        public static ModelPart Identity { get; } = new(string.Empty, Vector3.Zero, Quaternion.Identity, Vector3.One, false);

        public static ModelPart At(string model) => new(model, Vector3.Zero, Quaternion.Identity, Vector3.One, false);
    }

    private readonly record struct Piece(
        string Name,
        Vector3 Position,
        Vector3 RotationDegrees,
        Vector3 Scale,
        string Model,
        bool Effect);
}
