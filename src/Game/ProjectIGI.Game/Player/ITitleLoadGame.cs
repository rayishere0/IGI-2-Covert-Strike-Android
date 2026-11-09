using ProjectIGI.Core.Graphics;

namespace ProjectIGI.Game.Player;

/// <summary>
/// The save games a title keeps across its missions, as the main menu offers them: the saves the active profile holds, the slot a
/// load arms for the next level, and the mission pictures the slots show. The host's main menu offers them; the title decides what
/// they hold and where they are kept. The running mission's own saves are <see cref="ITitleSaveGames"/> instead.
/// </summary>
public interface ITitleLoadGame
{
    /// <summary>Gets the active profile's valid saves, across every mission, in mission then slot order.</summary>
    IReadOnlyList<LoadGameSave> ListSaves();

    /// <summary>Arms a save for the next level to load, as the menu's load choice does.</summary>
    /// <param name="missionId">The mission the save holds.</param>
    /// <param name="slot">The slot.</param>
    /// <returns><see langword="true"/> once armed; <see langword="false"/> when the slot holds no save of that mission.</returns>
    bool ArmLoad(int missionId, int slot);

    /// <summary>Decodes a mission's briefing sprite for a save-slot thumbnail.</summary>
    /// <param name="spriteName">The sprite's resource name, for example <c>mission1.spr</c>.</param>
    /// <param name="width">Receives the image width.</param>
    /// <param name="height">Receives the image height.</param>
    /// <returns>The decoded pixels, or an empty array when the sprite cannot be shown.</returns>
    Bgra32[] MissionThumbnail(string spriteName, out int width, out int height);
}
