using ProjectIGI.Core.Graphics;
using ProjectIGI.Game.Player;

namespace ProjectIGI.Host;

/// <summary>The main menu's load view, as the host answers it: the title's saves, and a load that leaves the menu into its mission.</summary>
public sealed partial class GameHost
{
    // The title's load list, or none when the running title keeps none: the shell reads it as it builds the load view.
    private IReadOnlyList<LoadGameSave> ListLoadGameSaves() =>
        _title.Services.TryGet(out ITitleLoadGame? loads) ? loads.ListSaves() : [];

    // A load, in the same order the retail load screen takes it: arm the slot for the level that loads next, leave the menu, start the
    // mission. The slot is armed and the mission started on this same call, so no backdrop level built between them can take the slot
    // first — and the showcase is never promoted here, because promoting it would enter the level as it already stands, without the save.
    private bool LoadSaveGame(int missionId, int slot)
    {
        if (!_missions.TryGet(missionId, out _))
        {
            return false;
        }

        if (!_title.Services.TryGet(out ITitleLoadGame? loads) || !loads.ArmLoad(missionId, slot))
        {
            return false;
        }

        _menuFlow.MenuMode = false;
        _menuFlow.PauseMenu = false;
        ApplyGameplayMotionBlur();
        StartMission(missionId);
        return true;
    }

    // A mission's briefing sprite for a save-slot thumbnail, or nothing when the title has none.
    private Bgra32[] MissionThumbnail(string spriteName, out int width, out int height)
    {
        if (_title.Services.TryGet(out ITitleLoadGame? loads))
        {
            return loads.MissionThumbnail(spriteName, out width, out height);
        }

        width = 0;
        height = 0;
        return [];
    }
}
