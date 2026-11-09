using ProjectIGI.Game.Igi2.Humans;
using ProjectIGI.Game.Igi2.Weapons;

namespace ProjectIGI.Game.Igi2.Player;

/// <content>The binoculars and the thermal goggles, which their own keys raise and lower over whatever is in hand.</content>
public sealed partial class Igi2HumanPlayer
{
    /// <summary>The binoculars' weapon id.</summary>
    public const string BinocularsWeaponId = "WEAPON_ID_BINOCULARS";

    /// <summary>The thermal goggles' weapon id.</summary>
    public const string ThermalWeaponId = "WEAPON_ID_THERMICAL";

    /// <summary>Gets or sets the weapon the binoculars or goggles were raised over, <c>ptPrevWeaponType</c> (<c>HumanPlayer+0xD8C</c>).</summary>
    public Igi2WeaponTypeTask? PreviousWeaponType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the view sees in infrared, <c>isInfraEnabled</c>: the goggles' key turns it
    /// on, and each tick's end sets it from whether the weapon in hand is a special one (<c>HumanPlayer_RunDelayedHandler</c>,
    /// <c>igi2 0x4B8C72</c>), or from a thermo-sight rifle with its scope up (<see cref="IsThermoSightScoped"/>).
    /// </summary>
    public bool IsInfraEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the binoculars are up as the touch night-vision goggles: the green
    /// wash the host draws over the plain binoculars' view. Set by the host's Night Vision button when it raises the
    /// binoculars; cleared here when the optics come down or the thermal goggles go up. Not a retail feature — retail
    /// IGI 2 has no night-vision goggles, only the thermal imaging device.
    /// </summary>
    public bool IsNightVisionEnabled { get; set; }

    /// <summary>
    /// Gets a value indicating whether the weapon in hand is a thermo-sight rifle with its scope up: the sight is on and
    /// the overlay is showing, so the view looks through the thermo-chemical glass. The flag the sight's own fire type
    /// sets counts too, for a weapon whose data fires <c>ThermoSight</c>. Lowering the scope, or putting the rifle away,
    /// takes the overlay down and the flag with it, so this goes false again.
    /// </summary>
    public bool IsThermoSightScoped =>
        Weapon.Firing is { OverlayStatus: Igi2WeaponOverlayStatus.WithOverlay } firing
        && (Weapon.ActiveHasThermoSight || firing.IsThermoSightOn);

    /// <summary>
    /// Raises or lowers the binoculars or the goggles on their key, <c>HumanPlayer_CheckBinoculars</c> (<c>igi2 0x4B3950</c>)
    /// in the single-player game.
    /// </summary>
    /// <param name="forceChangeWeapon">Whether lowering them puts the weapon they were raised over straight back in hand.</param>
    /// <remarks>
    /// <para>
    /// Only with binoculars that may be put in hand, and only on a key's press. With the binoculars already shown and their
    /// key held, or the goggles and theirs, they are lowered: the weapon they were raised over goes back in hand, at once
    /// when forced and otherwise as a change asked for, and the zoom comes down.
    /// </para>
    /// <para>
    /// Otherwise the goggles' key (with the binoculars' up) turns infrared on and puts the goggles in hand, and the
    /// binoculars' puts them in hand. Anything else that was shown is kept to go back to; the new one's zoom is fired at once,
    /// its trigger let go at the end of the tick, and the weapon left ready. Either way the weapon's precision is set again.
    /// </para>
    /// </remarks>
    private void CheckBinoculars(bool forceChangeWeapon)
    {
        if (Weapon.Inventory is not { } inventory || inventory.FindWeapon(BinocularsWeaponId) is not { } binocularItem
            || !inventory.IsSelectable(binocularItem))
        {
            return;
        }

        Igi2WeaponTypeTask? shown = inventory.StatusDisplay?.WeaponType;
        bool binocularsKey = Channels[Igi2HumanChannel.Binoculars] != 0f;
        bool thermalKey = Channels[Igi2HumanChannel.Thermal] != 0f;
        if (!Channels.WasPressed(Igi2HumanChannel.Binoculars) && !Channels.WasPressed(Igi2HumanChannel.Thermal))
        {
            return;
        }

        Igi2WeaponTypeTask binoculars = binocularItem.WeaponType;
        if (inventory.FindWeapon(ThermalWeaponId) is not { } thermalItem)
        {
            return;
        }

        Igi2WeaponTypeTask thermal = thermalItem.WeaponType;
        if ((binoculars == shown && binocularsKey) || (thermal == shown && thermalKey))
        {
            if (PreviousWeaponType is { } previous && inventory.FindWeapon(previous.WeaponId) is { } previousItem)
            {
                if (forceChangeWeapon)
                {
                    ActivateWeapon(previousItem);
                }
                else
                {
                    ChangeWeaponId = previousItem.Number;
                }
            }

            PreviousWeaponType = null;
            IsNightVisionEnabled = false;
            Weapon.ClearOverlay();
            SetWeaponPrecision();
            return;
        }

        if (!binocularsKey)
        {
            IsInfraEnabled = true;
            IsNightVisionEnabled = false;
            ActivateWeapon(thermalItem);
        }
        else
        {
            ActivateWeapon(binocularItem);
        }

        if (shown != binoculars && shown != thermal)
        {
            PreviousWeaponType = shown;
        }

        Weapon.Fire(0);
        if (Weapon.Firing is { } firing)
        {
            firing.FireTypeState(0).IsReleaseFire = true;
            inventory.Status = Igi2WeaponStatus.Ready;
        }

        SetWeaponPrecision();
    }

    /// <summary>
    /// Puts a weapon in hand as <c>Weapon_Activate</c> does for the player: the player's weapon status says it was
    /// activated (<c>Weapon_SingleActivateHandler</c>, <c>igi2 0x51D7A2</c>).
    /// </summary>
    private void ActivateWeapon(Igi2WeaponItem item)
    {
        if (Weapon.Activate(item))
        {
            WeaponStatus = Igi2PlayerWeaponStatus.Activated;
        }
    }
}
