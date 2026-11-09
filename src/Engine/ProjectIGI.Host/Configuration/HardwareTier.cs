using ProjectIGI.Platform;
using ProjectIGI.Rendering;

namespace ProjectIGI.Host.Configuration;

/// <summary>A device's performance tier: which starting settings it gets on a first run.</summary>
/// <remarks>
/// Classified from the adapter-name string, which is the only GPU identity that reaches the app
/// layer (<see cref="ProjectIGI.Rendering.Rhi.IRhiDevice"/> exposes the name, not the vendor id or
/// API version), so the table works for both the Vulkan and the WebGPU backend with no RHI change.
/// Unknown hardware falls back to <see cref="Medium"/>, which is today's middle-of-the-road
/// starting point. Exposing the vendor id / API version on the RHI device and classifying on that
/// is a hardening follow-up, not this change.
/// </remarks>
public enum HardwareTier
{
    /// <summary>Low-end mobile GPUs: 540p, low textures, near view distance, dynamic resolution on.</summary>
    Low,

    /// <summary>Anything unrecognised, and the middle of the range: 720p, medium textures, far view distance, dynamic resolution on.</summary>
    Medium,

    /// <summary>Flagship mobile GPUs: 900p, high textures, far view distance, dynamic resolution off.</summary>
    High,
}

/// <summary>Classifies a device into a <see cref="HardwareTier"/> and applies that tier's starting settings.</summary>
/// <remarks>
/// Named <c>HardwareTiering</c> rather than <c>HardwareTier</c> because the tier itself is the
/// <see cref="HardwareTier"/> enum: a type and a class cannot share the name in one namespace.
/// </remarks>
public static class HardwareTiering
{
    private sealed record TierValues(
        int RenderScaleMode,
        int RenderScaleHeight,
        int TextureQuality,
        float ViewDistance,
        bool DynamicResolution,
        int DynamicTargetFps,
        int FrameLimit);

    // The per-tier starting values. RenderScaleMode is the Android window tier (0 = 540p,
    // 1 = 720p, 2 = 900p); RenderScaleHeight is the same tier as a height, turned into r_scale
    // against the window height at apply time. Native (mode 3) is never auto-applied.
    // r_frame_limit stays 0 (uncapped) on every tier. r_sun_brightness stays 1 and r_gamma is
    // untouched on every tier: the sun is not a performance lever, and gamma has no live consumer
    // on the Android vanilla path.
    private static readonly IReadOnlyDictionary<HardwareTier, TierValues> Table =
        new Dictionary<HardwareTier, TierValues>
        {
            [HardwareTier.Low] = new(
                RenderScaleMode: 0,
                RenderScaleHeight: 540,
                TextureQuality: 0,
                ViewDistance: 0.025f,
                DynamicResolution: true,
                DynamicTargetFps: 30,
                FrameLimit: 0),
            [HardwareTier.Medium] = new(
                RenderScaleMode: 1,
                RenderScaleHeight: 720,
                TextureQuality: 1,
                ViewDistance: 1.0f,
                DynamicResolution: true,
                DynamicTargetFps: 30,
                FrameLimit: 0),
            [HardwareTier.High] = new(
                RenderScaleMode: 2,
                RenderScaleHeight: 900,
                TextureQuality: 2,
                ViewDistance: 1.0f,
                DynamicResolution: false,
                DynamicTargetFps: 30,
                FrameLimit: 0),
        };

    // High is checked before Low: "Mali-G72" is a substring of "Mali-G720", so checking Low
    // first would misclassify a Mali-G720 as low-end. The Low list covers the known fleet
    // (Helio G81 / Mali-G52, Poco M6 Pro / Mali-G57 -- the Poco's level-load crash is a
    // separate, unknown cause; tiering does not fix it).
    private static readonly string[] HighSubstrings =
    [
        "Adreno 7", "Adreno 8",
        "Mali-G610", "Mali-G615", "Mali-G68", "Mali-G78", "Mali-G715", "Mali-G720",
        "Immortalis",
    ];

    private static readonly string[] LowSubstrings =
    [
        "Mali-G52", "Mali-G57", "Mali-G51", "Mali-G31",
        "Mali-G71", "Mali-G72", "Mali-G76", "Mali-T",
        "Adreno 5", "Adreno 6",
        "PowerVR",
    ];

    /// <summary>Classifies a device from its adapter name. Unknown names are <see cref="HardwareTier.Medium"/>.</summary>
    /// <param name="adapterName">The adapter name string (<c>IRhiDevice.AdapterName</c>).</param>
    /// <param name="apiName">
    /// The rendering API name (<c>IRhiDevice.ApiName</c>). Reserved for a future signal -- a device
    /// API version below 1.2, for example -- and not read by this version, which classifies on the
    /// name alone.
    /// </param>
    public static HardwareTier Classify(string? adapterName, string? apiName)
    {
        _ = apiName;
        if (ContainsAny(adapterName, HighSubstrings))
        {
            return HardwareTier.High;
        }

        if (ContainsAny(adapterName, LowSubstrings))
        {
            return HardwareTier.Low;
        }

        return HardwareTier.Medium;
    }

    /// <summary>Renders a tier as the one-line summary the start-up log shows, for example "540p, textures low, view distance near, dynamic resolution 30 fps".</summary>
    public static string Describe(HardwareTier tier)
    {
        TierValues values = Table[tier];
        string textures = values.TextureQuality switch
        {
            0 => "low",
            2 => "high",
            _ => "medium",
        };
        string viewDistance = values.ViewDistance <= 0.025f ? "near" : "far";
        string dynamic = values.DynamicResolution
            ? $"dynamic resolution {values.DynamicTargetFps} fps"
            : "dynamic resolution off";
        return $"{values.RenderScaleHeight}p, textures {textures}, view distance {viewDistance}, {dynamic}";
    }

    /// <summary>Applies a tier's starting values to the render CVars.</summary>
    /// <param name="tier">The tier to apply.</param>
    /// <param name="window">The window, for its height and its render-scale tier.</param>
    /// <remarks>
    /// <para>
    /// Every value is applied only while its CVar still holds its default, so a <c>+r_scale</c> (or
    /// any other <c>+</c> statement) on the command line wins over the tier for that one setting.
    /// The render scale needs the same care twice over: the Settings screen's Resolution Quality
    /// row reads the window's <see cref="IGameWindow.RenderScaleMode"/>, so the window has to be
    /// told the tier as well as the CVar, or the menu would show the tier the Vanilla preset left
    /// behind (native) while rendering at the tier's scale. The window setter is therefore called
    /// only inside the same <c>IsDefault</c> guard -- it writes r_scale itself on Android, and the
    /// direct assignment after it is the same value there (assigning an equal value raises no
    /// event) and the actual write on a host whose window setter is a no-op.
    /// </para>
    /// <para>
    /// The caller gates this on a first run (no settings file was loaded). It must not run for an
    /// existing install: a player who changed a setting keeps it.
    /// </para>
    /// </remarks>
    public static void Apply(HardwareTier tier, IGameWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        TierValues values = Table[tier];
        float height = window.Height > 0 ? window.Height : 1080f;
        float scale = Math.Clamp(values.RenderScaleHeight / height, 0.25f, 1f);

        if (ReconstructionCVars.RenderScale.IsDefault)
        {
            window.RenderScaleMode = values.RenderScaleMode;
            ReconstructionCVars.RenderScale.Value = scale;
        }

        if (ReconstructionCVars.FrameLimit.IsDefault)
        {
            ReconstructionCVars.FrameLimit.Value = values.FrameLimit;
        }

        if (ReconstructionCVars.DynamicResolution.IsDefault)
        {
            ReconstructionCVars.DynamicResolution.Value = values.DynamicResolution;
        }

        if (ReconstructionCVars.DynamicTargetFps.IsDefault)
        {
            ReconstructionCVars.DynamicTargetFps.Value = values.DynamicTargetFps;
        }

        if (ReconstructionCVars.ViewDistance.IsDefault)
        {
            ReconstructionCVars.ViewDistance.Value = values.ViewDistance;
        }

        if (MaterialCVars.TextureQuality.IsDefault)
        {
            MaterialCVars.TextureQuality.Value = values.TextureQuality;
        }
    }

    private static bool ContainsAny(string? adapterName, string[] substrings)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return false;
        }

        foreach (string substring in substrings)
        {
            if (adapterName.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
