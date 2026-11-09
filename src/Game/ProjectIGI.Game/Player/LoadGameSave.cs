namespace ProjectIGI.Game.Player;

/// <summary>A save game the main menu can load: which mission it holds, which slot, and what it holds.</summary>
/// <param name="MissionId">The mission the save holds.</param>
/// <param name="Slot">The slot, as the save file names it.</param>
/// <param name="IsQuicksave">Whether this is the dedicated quicksave slot.</param>
/// <param name="MissionTime">The mission time the save holds, <c>h:mm:ss</c>.</param>
/// <param name="SavedAt">When the save was written, as the load list words it.</param>
public sealed record LoadGameSave(int MissionId, int Slot, bool IsQuicksave, string MissionTime, string SavedAt);
