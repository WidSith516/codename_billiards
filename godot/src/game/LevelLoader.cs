using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// Loads level JSON from <c>res://levels/</c> into the engine-free <see cref="LevelData"/> model.
/// This is the only place where level data meets Godot's file API.
/// </summary>
public static class LevelLoader
{
    public const string LevelDirectory = "res://levels";

    public static LevelData Load(string resPath)
    {
        using var file = FileAccess.Open(resPath, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            throw new System.IO.FileNotFoundException(
                $"cannot open level '{resPath}' (FileAccess error {FileAccess.GetOpenError()})");
        }

        var text = file.GetAsText();
        var level = LevelData.FromJsonText(text);
        return level;
    }

    public static LevelData LoadById(string id)
    {
        return Load($"{LevelDirectory}/{id}.json");
    }

    /// <summary>Level ids discovered on disk (sorted, so iteration order is deterministic).</summary>
    public static string[] ListLevelIds()
    {
        var ids = new System.Collections.Generic.List<string>();
        using var dir = DirAccess.Open(LevelDirectory);
        if (dir == null) return ids.ToArray();
        foreach (var name in dir.GetFiles())
        {
            if (name.EndsWith(".json")) ids.Add(name.Substring(0, name.Length - 5));
        }

        ids.Sort(System.StringComparer.Ordinal);
        return ids.ToArray();
    }
}
