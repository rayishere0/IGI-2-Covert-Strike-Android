namespace ProjectIGI.Game.Weapons;

/// <summary>The weapons that carry a thermo-chemical imaging sight, whose glass shows only body heat when looked through.</summary>
/// <remarks>
/// <b>Not retail</b>: neither game marks a thermo sight in its weapon data -- a sight is the weapon's own model -- so the
/// rifle is named here: I.G.I. 2's Barrett M82A1 with the Thermo-Chemical imaging scope ("M82A1-T", mission 16
/// "The Ancient Temple"). The other scoped rifles (Dragunov, PSG-1/PSG-1SD, plain Barrett) have ordinary scopes;
/// retail gives them no thermal.
/// </remarks>
public static class ThermoSightWeapons
{
    private static readonly HashSet<string> ThermoSight = new(StringComparer.OrdinalIgnoreCase)
    {
        "WEAPON_ID_BARRETTHERMO",
    };

    /// <summary>Whether a weapon carries a thermo-chemical imaging sight.</summary>
    /// <param name="weaponId">The weapon's script id, such as <c>WEAPON_ID_BARRETTHERMO</c>.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsThermoSight(string? weaponId) => weaponId is { Length: > 0 } && ThermoSight.Contains(weaponId);
}
