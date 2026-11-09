using System.Globalization;
using ProjectIGI.Core.Configuration;
using ProjectIGI.Core.Diagnostics;
using ProjectIGI.Core.Graphics;
using ProjectIGI.Engine.Vfs;
using ProjectIGI.Formats.Ilff;
using ProjectIGI.Formats.Sprites;
using ProjectIGI.Formats.Textures;
using ProjectIGI.Game.Igi2.Config;
using ProjectIGI.Game.Player;

namespace ProjectIGI.Game.Igi2.Saves;

/// <summary>
/// The main menu's load list for I.G.I. 2: the active profile's valid saves, across every mission, read out of the same
/// <c>savegames</c> folder the running mission writes to; the slot a load arms for the next level, through the same session handoff
/// the retail load screen uses; and the mission briefing sprites for the slots' thumbnails.
/// </summary>
/// <remarks>
/// Only the active profile's own difficulty is listed — the saves of another difficulty are not offered, as the retail load list
/// does not offer them either. A save whose mission the catalogue no longer knows is still listed: the screen names it by its id.
/// </remarks>
/// <param name="config">The profiles, whose active one owns the saves.</param>
/// <param name="session">The menu's save session, which the load slot is armed on and which knows the slots' folder once a level has run.</param>
public sealed class Igi2LoadGame(Igi2GameConfig config, Igi2SaveSession session) : ITitleLoadGame
{
    private readonly Igi2GameConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly Igi2SaveSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly Dictionary<string, (Bgra32[] Pixels, int Width, int Height)> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private IresArchive? _sprites;
    private string? _localRoot;

    /// <summary>Gets or sets the file system the briefing sprites are read from; <see langword="null"/> until the tables load.</summary>
    /// <remarks>
    /// Set from the load module's <c>LoadTables</c>: registration has no file system, so a thumbnail asked for before then simply has
    /// none, and the slot lists without one.
    /// </remarks>
    public VirtualFileSystem? FileSystem { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<LoadGameSave> ListSaves()
    {
        if (_config.Active is not { } active)
        {
            return [];
        }

        int profile = _config.ActiveIndex;
        int difficulty = active.Difficulty;
        string folder = SaveFolder();
        string[] files;
        try
        {
            string saves = Path.Combine(folder, "savegames");
            files = Directory.Exists(saves) ? Directory.GetFiles(saves, "*.igi2save") : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var slots = new Igi2SaveSlots(folder);
        List<LoadGameSave> saves = [];
        foreach (string file in files)
        {
            if (!Igi2SaveSlots.TryParseSlotFileName(file, out Igi2SaveOwner owner, out int slot)
                || owner.Profile != profile || owner.Difficulty != difficulty)
            {
                continue;
            }

            Igi2SaveInfo info;
            DateTime written;
            try
            {
                info = slots.Check(owner, slot);
                if (!info.IsValid)
                {
                    continue;
                }

                written = File.GetLastWriteTime(slots.PathOf(Igi2SaveSlots.SlotFileName(owner, slot)));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            string when = string.Create(CultureInfo.CurrentCulture, $"({written:d} {written:T})");
            saves.Add(new LoadGameSave(owner.Mission, slot, slot == Igi2SaveSlots.QuickSave, Igi2SaveSlots.MissionTime(info.Ticks), when));
        }

        saves.Sort(static (a, b) => a.MissionId != b.MissionId ? a.MissionId.CompareTo(b.MissionId) : a.Slot.CompareTo(b.Slot));
        return saves;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only the slot is armed: the mission itself is the host's to start, and the armed slot is taken once, by the level that loads
    /// next, so arming here and starting there on the same thread leaves no window for a backdrop level to take it first.
    /// </remarks>
    public bool ArmLoad(int missionId, int slot)
    {
        if (_config.Active is not { } active || missionId <= 0 || slot < 0 || slot > Igi2SaveSlots.QuickSave)
        {
            return false;
        }

        var owner = new Igi2SaveOwner(_config.ActiveIndex, active.Difficulty, missionId);
        try
        {
            if (!new Igi2SaveSlots(SaveFolder()).Check(owner, slot).IsValid)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        _session.SetMenuLoadSlot(slot);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The sprite is read out of <c>missionsprites.res</c> and decoded the way the mission-select picture box draws it: the first
    /// frame, cropped to its own rectangle. A sprite that is missing, undecodable, or asked for before the file system arrives has no
    /// thumbnail, and the slot lists without one rather than failing the list.
    /// </remarks>
    public Bgra32[] MissionThumbnail(string spriteName, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(spriteName))
        {
            return [];
        }

        if (_thumbnails.TryGetValue(spriteName, out (Bgra32[] Pixels, int Width, int Height) cached))
        {
            width = cached.Width;
            height = cached.Height;
            return cached.Pixels;
        }

        Bgra32[] pixels = DecodeThumbnail(spriteName, out width, out height);
        _thumbnails[spriteName] = (pixels, width, height);
        return pixels;
    }

    // Where the slots are kept: the folder a level has already said, else the game's own folder on disk, else the fallback the
    // level path uses when there is no local device at all.
    private string SaveFolder()
    {
        if (_session.Slots is { } slots)
        {
            return slots.Directory;
        }

        if (_localRoot is null && FileSystem is { } fileSystem
            && fileSystem.TryGetDevice(LocalFileDevice.DeviceName, out IFileDevice? device)
            && device is LocalFileDevice local)
        {
            _localRoot = local.RootDirectory;
        }

        return _localRoot ?? Path.Combine(SettingsPaths.GetUserDirectory(), "igi2");
    }

    private Bgra32[] DecodeThumbnail(string spriteName, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            if (FileSystem is not { } fileSystem)
            {
                return [];
            }

            _sprites ??= fileSystem.ReadArchive("LOCAL:menusystem/missionsprites.res");
            if (_sprites.FindBody(spriteName) is not { } entry)
            {
                return [];
            }

            SpriteAsset asset = SpriteLoader.Load(entry.Data, spriteName);
            if (asset.Frames.Count > 0 && asset.Frames[0] is { IsEmpty: false } frame
                && (uint)frame.TextureIndex < (uint)asset.Textures.Count)
            {
                TextureImage image = asset.Textures[frame.TextureIndex];
                int x0 = (int)(frame.U0 * image.Width);
                int y0 = (int)(frame.V0 * image.Height);
                int w = Math.Min(frame.Width, image.Width - x0);
                int h = Math.Min(frame.Height, image.Height - y0);
                if (x0 < 0 || y0 < 0 || w <= 0 || h <= 0)
                {
                    return [];
                }

                Bgra32[] pixels = new Bgra32[w * h];
                ReadOnlySpan<Bgra32> source = image.Pixels;
                for (int y = 0; y < h; y++)
                {
                    source.Slice((y0 + y) * image.Width + x0, w).CopyTo(pixels.AsSpan(y * w, w));
                }

                width = w;
                height = h;
                return pixels;
            }

            if (asset.Textures.Count > 0)
            {
                TextureImage image = asset.Textures[0];
                width = image.Width;
                height = image.Height;
                return image.Pixels.ToArray();
            }

            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TextureFormatException or EngineFatalException)
        {
            return [];
        }
    }
}
