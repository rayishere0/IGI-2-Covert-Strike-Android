namespace ProjectIGI.Rendering;

/// <summary>
/// Everything a device needs to allocate a texture: its size, its mip count and how it is sampled.
/// </summary>
/// <param name="Width">The width of mip level 0 in pixels.</param>
/// <param name="Height">The height of mip level 0 in pixels.</param>
/// <param name="MipLevels">The number of mip levels, at least one.</param>
/// <param name="Filter">How the texture is sampled between texels.</param>
/// <param name="AddressMode">How coordinates outside the unit square are resolved.</param>
/// <remarks>
/// Pixels always arrive as <see cref="ProjectIGI.Core.Graphics.Bgra32"/>: the file's own format code is
/// decoded once at load time, so the device never sees a palette or a packed 16-bit pixel.
/// </remarks>
public readonly record struct TextureDescription(
    int Width,
    int Height,
    int MipLevels,
    TextureFilter Filter,
    TextureAddressMode AddressMode)
{
    /// <summary>Initializes a new instance of the <see cref="TextureDescription"/> struct with one mip level and the defaults used by 2D drawing.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public TextureDescription(int width, int height)
        : this(width, height, 1, TextureFilter.Linear, TextureAddressMode.Clamp)
    {
    }

    /// <summary>Gets a value indicating whether the texels are numbers rather than colours.</summary>
    /// <remarks>
    /// A device that stores colour textures as sRGB decodes every read of them, which is right for a
    /// picture and ruins packed data: the shelter map's two height bytes come back gamma-decoded, every
    /// roof reads lower than it stands, and water pools on the floors under them. Such a texture is
    /// stored linear, so a load returns the bytes that were uploaded.
    /// </remarks>
    public bool HoldsData { get; init; }

    /// <summary>Gets a value indicating whether the texture is a single panel or sign that must never wrap.</summary>
    /// <remarks>
    /// World textures repeat by design - the ground, every wall, every crate tiles - so the caches
    /// ask for <see cref="TextureAddressMode.Repeat"/> for all of them. A sign is authored as one
    /// rectangular graphic, and a repeating sampler shows it twice across its panel. Flagging it here
    /// makes the texture table clamp it instead; see <see cref="IsNonTilingName"/>.
    /// </remarks>
    public bool IsNonTiling { get; init; }

    /// <summary>Whether a texture name is a single-panel sign or banner that must never wrap.</summary>
    /// <param name="name">The texture's name, bare or qualified.</param>
    /// <returns><see langword="true"/> when the name is a sign or banner panel.</returns>
    /// <remarks>
    /// The banner has no creation site of its own: world textures all load through the same caches,
    /// so the name is the only place the panel can be recognised. "priboi" is the warehouse the
    /// mission 13 sign belongs to; "banner" is a panel by definition; "sign" only as a whole token,
    /// so "design" and "signal" keep tiling.
    /// </remarks>
    public static bool IsNonTilingName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Contains("priboi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("banner", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string token in name.Split('_', '-', '.', ' ', '/'))
        {
            if (token.Equals("sign", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the number of pixels in mip level 0.</summary>
    public int PixelCount => Width * Height;

    /// <summary>Gets the size of the mip level at <paramref name="level"/>.</summary>
    /// <param name="level">The zero-based mip level.</param>
    /// <returns>The width and height of that level.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative or at least <see cref="MipLevels"/>.</exception>
    /// <remarks>
    /// Both axes halve together and neither is clamped: the original's chain stops as soon as
    /// <em>either</em> axis would reach zero, so a 128×64 texture has seven levels ending at 2×1, not
    /// eight ending at 1×1. Backends must cap their maximum level accordingly rather than assume a
    /// chain that runs to 1×1.
    /// </remarks>
    public (int Width, int Height) GetMipSize(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, MipLevels);
        return (Width >> level, Height >> level);
    }
}
