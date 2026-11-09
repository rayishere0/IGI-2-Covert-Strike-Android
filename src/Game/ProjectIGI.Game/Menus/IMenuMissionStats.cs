namespace ProjectIGI.Game.Menus;

/// <summary>The mission details a stats screen shows, from the menu's configuration.</summary>
/// <remarks>
/// <para>
/// The stats screen's task carries no data of its own: the id it shows comes from
/// <c>MenuStatsScreen_SetActiveMissionID</c>, which records it on the configuration, and the mission
/// itself lives in the title's catalogue, which only the configuration can reach. So the renderer
/// asks the configuration — through this, rather than naming a title's class — and a configuration
/// that does not answer it (Project I.G.I.'s has no stats screen) leaves the box empty.
/// </para>
/// <para>
/// The name and description come back already localised: a descriptor's two strings are language
/// keys, not text, and the configuration resolves them through the menu's string table.
/// </para>
/// <para>
/// The mission-select screen itself shows a table, not the name and description: see
/// <see cref="MissionStatsTable"/>.
/// </para>
/// </remarks>
public interface IMenuMissionStats
{
    /// <summary>Gets the name and description of the mission the stats screen is showing.</summary>
    /// <param name="name">Receives the mission's localised name.</param>
    /// <param name="description">Receives the mission's localised description.</param>
    /// <returns><see langword="true"/> when there is a mission to show.</returns>
    bool TryGetActiveMissionStats(out string name, out string description);

    /// <summary>Gets the stats table of the mission the stats screen is showing.</summary>
    /// <param name="table">Receives the table.</param>
    /// <returns><see langword="true"/> when there is a mission to show.</returns>
    bool TryGetActiveMissionTable(out MissionStatsTable table);
}

/// <summary>The stats table a mission-select screen shows for the mission its list sits on.</summary>
/// <remarks>
/// <para>
/// Retail screen 914 shows a table, not the mission's description: one row per stat, with an
/// Achieved column for the player's best run and a Target column for the mission's par. The
/// description lives on the loading screen instead, which still reads
/// <see cref="IMenuMissionStats.TryGetActiveMissionStats"/>.
/// </para>
/// <para>
/// The Category row has no source anywhere, so the renderer draws it with both cells blank. Every
/// Achieved cell is a default until per-mission stats are recorded, and only mission 1's Target
/// cells are known — the rest stay blank until the GOPlayerMissionStats layout is reverse-engineered.
/// </para>
/// </remarks>
public readonly record struct MissionStatsTable(
    string TimeAchieved,
    string TimeTarget,
    string TimesSpottedAchieved,
    string TimesSpottedTarget,
    string HealthAchieved,
    string HealthTarget,
    string AccuracyAchieved,
    string AccuracyTarget,
    string RankAchieved);
