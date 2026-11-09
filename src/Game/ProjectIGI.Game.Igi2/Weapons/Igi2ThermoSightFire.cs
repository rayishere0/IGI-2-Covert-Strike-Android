namespace ProjectIGI.Game.Igi2.Weapons;

/// <summary>
/// A weapon's thermo-chemical imaging sight, <c>thermosight.c</c>: firing it switches the sight on, putting its
/// weapon in hand arms it, and putting its weapon away switches it off again
/// (<c>ThermoSight_WeaponDeactivateHandler</c>).
/// </summary>
/// <remarks>
/// The sight only shows while the weapon's scope is up: the player's tick-end infrared check reads the sight together
/// with the overlay, so merely carrying the rifle -- or firing its sight while the scope is down -- is not infrared.
/// That is also why the flag is cleared whenever the overlay comes down, not only when the weapon is put away.
/// </remarks>
internal sealed class Igi2ThermoSightFire : IIgi2FireHandler, IIgi2FireActivateHandler, IIgi2FireDeactivateHandler
{
    /// <inheritdoc/>
    public bool HasRelease => false;

    /// <inheritdoc/>
    public Igi2FireResult Fire(in Igi2FireRequest request)
    {
        request.Weapon.IsThermoSightOn = true;
        return Igi2FireResult.Nothing;
    }

    /// <inheritdoc/>
    public Igi2FireResult Release(in Igi2FireRequest request) => default;

    /// <inheritdoc/>
    public void Activate(Igi2WeaponFiring weapon, Igi2WeaponItem item)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        weapon.IsThermoSightOn = true;
    }

    /// <inheritdoc/>
    public void Deactivate(Igi2WeaponFiring weapon, Igi2WeaponItem item)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        weapon.IsThermoSightOn = false;
    }
}
