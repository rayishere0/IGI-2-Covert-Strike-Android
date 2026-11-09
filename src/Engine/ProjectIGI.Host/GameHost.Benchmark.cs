using ProjectIGI.Core.Graphics;
using ProjectIGI.Game.Ai;
using ProjectIGI.Host.Configuration;
using ProjectIGI.Host.Menu;
using ProjectIGI.Input;
using ProjectIGI.Presentation.Cameras;
using ProjectIGI.Presentation.Rendering;
using ProjectIGI.Presentation.Text;
using ProjectIGI.Rendering;
using ProjectIGI.UI;
using ProjectIGI.UI.Kit;
using ProjectIGI.UI.Layout;

namespace ProjectIGI.Host;

public sealed partial class GameHost
{
    // The first-run benchmark (Ray: "benchmark testing with all of the settings rolling as soon as
    // the files are selected, and out of all, pick the best one and apply it, and then boot into the
    // game ... users can perform it again via clicking the gear icon, in the cheats menu"), reworked
    // into a visible, professional benchmark scene (Ray: "make it a professional benchmark, render
    // the weather station map in drone view, rotate it, with snow and players etc. ... let it
    // display aswell and not just run in the background").
    //
    // What it measures, honestly: the Weather Station -- mission 13, the heaviest render load on the
    // slow Mali path -- as a drone orbit over the level, with the level's own weather falling and its
    // soldiers standing at their posts. It is still a backdrop, not a played mission: no player, no
    // HUD, no cutscene, no AI ticking. The soldiers stand where the level put them (their poses are
    // the spawn poses the backdrop armed; nothing patrols, because the backdrop's loop never
    // advances), and the weather is the level's own RainEffect task read the way a mission reads it
    // (rain through the storm renderer, snow through the snow renderer, whichever the level
    // authors). What moves is the camera, and what is measured is the frame rate of that picture
    // under each tier. A firefight can still cost more than this scene -- the 30 FPS bar is on the
    // 1% low, and Low keeps dynamic resolution on, so real play has its own ladder back towards
    // the target.
    //
    // What the player sees: the menu hides for the run and the level fills the screen, the camera
    // circling the station's spawn point once every ~25 seconds; a heads-up display shows the live
    // FPS, which tier is being measured and what each finished tier scored; then a RESULTS screen
    // with the per-tier numbers and the tier picked, and a choice -- apply it and continue to the
    // menu, or keep the settings as they were. The run is also written down: r_benchmark_last
    // (date, tier, per-tier 1% low) survives in settings.cfg and is shown on the cheats menu's
    // BENCHMARK row, and the per-tier lines go to logcat as before.
    //
    // It cannot run in the file picker itself: no world is loaded there and no frame has been
    // drawn, so there is nothing to measure yet. It runs on the first menu frames instead, right
    // after the files are selected and the game boots -- the player is looking at the drone view
    // while it measures, and lands on the results screen, then the menu, with the best tier
    // applied when it ends. After the results are dismissed the run returns to the main menu; it
    // does not start a mission on its own -- starting one is the player's press, not the
    // benchmark's.
    //
    // The drone path: one continuous 360-degree orbit around the Weather Station's first showcase
    // point -- the player's spawn, which MissionShowcasePoints puts first -- at 0.25 radians per
    // second, about 25 seconds per revolution, so a 5-second measurement sweeps about 72 degrees.
    // One place, no cuts: a cut mid-run would put a different view under each tier and the tiers
    // would no longer be comparable. The orbit is the menu showcase camera itself, rebuilt over
    // that one point for the run and handed back to the menu's own multi-point orbit when the run
    // ends, so the camera the benchmark flies is the camera the menu already flies.
    //
    // If the Weather Station never loads, the run gives up cleanly after 30 seconds: the settings
    // go back to what they were, the menu's previous backdrop is asked for again, and a log line
    // says so. Nothing is applied, nothing is recorded, and r_benchmark_done stays set (it was
    // set when the run started) so a broken level does not re-prompt every launch.

    private enum BenchmarkPhase
    {
        Idle,
        Waiting,
        PreWarm,
        WarmUp,
        Measure,

        // The measurement is done and the numbers are saved; the drone view keeps orbiting behind
        // the results panel until the player picks APPLY or KEEP. Still "running" (see
        // BenchmarkRunning) so the menu stays hidden and the shell stays deaf while it is up.
        Results,
    }

    private sealed record BenchmarkTierResult(HardwareTier Tier, double MeanFps, double OnePercentLowFps, int Samples);

    private sealed record BenchmarkSettingsSnapshot(
        float RenderScale,
        int RenderScaleMode,
        int TextureQuality,
        float ViewDistance,
        bool DynamicResolution,
        int DynamicTargetFps,
        int FrameLimit,
        bool VSync);

    // Highest first, so the first tier that holds the bar is the answer and the run can stop: a
    // flagship never pays for the cheaper measurements, and a weak device measures all three on
    // its way down to Low.
    private static readonly HardwareTier[] BenchmarkTierOrder = [HardwareTier.High, HardwareTier.Medium, HardwareTier.Low];

    // The bar: the 1% low -- the frame rate the worst hundredth of frames sustains -- at the
    // dynamic-resolution target, 30 FPS. A mean would let a tier that stutters every second pass;
    // the 1% low is the rate the player actually feels.
    private const double BenchmarkPassOnePercentLowFps = 30.0;

    // Settle time before the first measurement, so the full preset's lighting convergence and the
    // backdrop's arming are done before High is judged on them rather than during them.
    private const int BenchmarkPreWarmMilliseconds = 3000;

    // Settle time after each tier switch, so the render-target resize and the texture LOD re-resolve
    // are not counted as the tier's frame rate.
    private const int BenchmarkWarmUpMilliseconds = 2000;

    // Timed frames per tier. Worst case 3 s + 3 x 7 s = 24 s; a flagship that holds High is done
    // in 3 s + 7 s = 10 s.
    private const int BenchmarkMeasureMilliseconds = 5000;

    // A tier that slow is failing whatever the sample count says; below this the number is not
    // trusted to pass, only to fail.
    private const int BenchmarkMinimumSamples = 30;

    // How long to wait for the Weather Station level before giving up and leaving the tiered settings alone.
    private const int BenchmarkWaitTimeoutMilliseconds = 30000;

    // The level the benchmark flies over: mission 13, The Weather Station -- night, storm, snowy
    // mountain station, the heaviest render load on the slow Mali path.
    private const int BenchmarkMissionId = 13;

    // The drone's turn rate: a full revolution in about 25 seconds, so a 5-second measurement
    // sweeps about 72 degrees -- visibly rotating, not whipping round.
    private const double BenchmarkOrbitSpeedRadiansPerSecond = 0.25;

    private BenchmarkPhase _benchmarkPhase = BenchmarkPhase.Idle;
    private bool _benchmarkPendingAuto;
    private bool _benchmarkManual;
    private int _benchmarkPhaseStartedMs;
    private int _benchmarkStartedMs;
    private int _benchmarkTierIndex;
    private int _benchmarkShowcaseMissionId;
    private bool _benchmarkSkipRequested;
    private BenchmarkSettingsSnapshot? _benchmarkSnapshot;
    private readonly List<BenchmarkTierResult> _benchmarkResults = [];

    // The showcase mission the run found the menu on, asked for again when the run ends so the
    // menu is exactly as the player left it. 0 when no run has recorded one.
    private int _benchmarkPreviousShowcaseMissionId;

    // The drone orbit itself: the menu's showcase camera rebuilt over the station's first
    // showcase point. Kept so the run can put it back every frame -- adopting the station
    // rebuilds the menu's camera, and a rebuild after this one would silently restore the
    // multi-point orbit (and its cuts) mid-run.
    private MenuShowcaseCamera? _benchmarkCamera;

    // The tier the measurement picked, decided when the results screen opens and applied (or
    // not) when it closes.
    private HardwareTier _benchmarkWinner;

    // Soldiers standing in the drone view, counted once when the station comes up.
    private int _benchmarkSoldierCount;

    // Whether the run built the level's weather for the scene, so the close step knows to take
    // it back down again. Only ever true while no mission is up.
    private bool _benchmarkWeatherBuilt;

    private bool BenchmarkRunning => _benchmarkPhase != BenchmarkPhase.Idle;

    /// <summary>Arms the first-run benchmark, if this is a first run that has never had one.</summary>
    /// <param name="settingsLoaded">Whether a settings file was loaded.</param>
    /// <remarks>
    /// The same gate PR #58's tiering uses: no settings file was loaded, so nothing the player
    /// chose can be overwritten. An existing install has a settings file and never arms, even
    /// though its file has no <c>r_benchmark_done</c> line. Capture and lockstep runs are
    /// measuring something else already, and the -entrycam / -backdropcycle / -presetcycle
    /// shortcuts would fight the run for the same scene, so none of them arms it either. This
    /// only arms; the run itself starts on the first menu frame (see
    /// <see cref="UpdateBenchmark"/>).
    /// </remarks>
    private void ArmFirstRunBenchmark(bool settingsLoaded)
    {
        if (settingsLoaded
            || BenchmarkCVars.BenchmarkDone.Value
            || _options.IsBenchmark
            || _options.Lockstep
            || _options.EntryCamMission > 0
            || _options.BackdropCycleFrames > 0
            || _options.PresetCycleFrames > 0)
        {
            return;
        }

        _benchmarkPendingAuto = true;
        _logger.Information(
            "Benchmark",
            "First-run benchmark armed: no settings file was loaded and no run has finished or been skipped; it starts on the first menu frame.");
    }

    /// <summary>Starts a benchmark run over the Weather Station, from the first run or the cheats menu.</summary>
    /// <param name="state">The frame loop's state, for the console that saves the result and the loader that builds the station.</param>
    /// <param name="manual">Whether the run was asked for from the cheats menu.</param>
    /// <remarks>
    /// The run flies over mission 13 whatever the menu was showing: the station is asked for here
    /// (superseding whatever backdrop was building) and the run waits for it in
    /// <see cref="UpdateBenchmark"/>. The showcase the menu was on is recorded and asked for again
    /// when the run ends, so the menu is exactly as the player left it.
    /// </remarks>
    private void StartBenchmark(FrameLoopState state, bool manual)
    {
        if (BenchmarkRunning || _options.IsBenchmark || _options.Lockstep)
        {
            return;
        }

        if (_session.Mission is not null || !_menuFlow.OverShowcase)
        {
            return;
        }

        // A catalogue that does not offer the station cannot build it: give up before the flag
        // below is set, so the next launch tries again rather than the run being silently marked
        // done on an install that may still gain the mission's files.
        if (!_missions.TryGet(BenchmarkMissionId, out _))
        {
            _logger.Warning("Benchmark", "Benchmark not run: mission 13, The Weather Station, is not offered.");
            return;
        }

        _benchmarkPreviousShowcaseMissionId = _menuFlow.CurrentShowcaseMissionId;

        // A browse the player started before the run must not fire mid-run and steal the
        // showcase out from under the measurement.
        _menuFlow.PendingPreviewMissionId = null;

        // The cheats modal cannot be open during a run: the manual path closes it below, and
        // the first-run path could otherwise start with it open over the drone view.
        _cheatsModalOpen = false;

        _benchmarkSnapshot = new BenchmarkSettingsSnapshot(
            ReconstructionCVars.RenderScale.Value,
            _window.RenderScaleMode,
            MaterialCVars.TextureQuality.Value,
            ReconstructionCVars.ViewDistance.Value,
            ReconstructionCVars.DynamicResolution.Value,
            ReconstructionCVars.DynamicTargetFps.Value,
            ReconstructionCVars.FrameLimit.Value,
            FrameCVars.VSync.Value);
        _benchmarkManual = manual;
        _benchmarkResults.Clear();
        _benchmarkTierIndex = 0;
        _benchmarkSkipRequested = false;
        _benchmarkShowcaseMissionId = _menuFlow.CurrentShowcaseMissionId;

        // One-time, whatever happens next: the flag is set and saved now, not at the end, so a
        // crash or a force-quit mid-run never re-prompts. What is saved beside it is the tier
        // the first run had already applied (or the player's own settings on a manual run),
        // never a half-measured tier: the measurement changes below are in-memory only until
        // the finish step saves them.
        BenchmarkCVars.BenchmarkDone.Value = true;
        state.Console.SaveSettings();

        // The measurement reads the picture, so nothing may cap it or move under it: vertical
        // sync off, dynamic resolution off (per tier, at apply time), and the backdrop's lite
        // profile off so the menu level renders with the full preset -- the same picture a
        // mission renders -- rather than without its shadows, traces and gathers. The lite
        // switch is not archived, so the override below leaves no trace; the profile is
        // re-applied right after it is cleared.
        FrameCVars.VSync.Value = false;
        FrameCVars.MenuBackdropLite.Override(false);
        UpdateBackdropProfile();

        _benchmarkStartedMs = _loop.TimeSource.Milliseconds;
        _benchmarkPhaseStartedMs = _benchmarkStartedMs;
        _benchmarkPhase = BenchmarkPhase.Waiting;
        _benchmarkWinner = HardwareTier.Low;
        _benchmarkSoldierCount = 0;
        _benchmarkWeatherBuilt = false;
        _benchmarkCamera = null;

        // Fly the station, not whatever the menu happened to be showing. Skipped when it is
        // already up: rebuilding the backdrop the run is about to measure would only make the
        // player wait for a level that is already there.
        if (_menuBackdrop.Preview?.MissionId != BenchmarkMissionId)
        {
            _menuFlow.CurrentShowcaseMissionId = BenchmarkMissionId;
            RequestBackdrop(state.PreviewLoader, BenchmarkMissionId);
        }

        _logger.Information(
            "Benchmark",
            manual
                ? "Benchmark started from the cheats menu: measuring High, Medium, Low over the Weather Station."
                : "First-run benchmark started: measuring High, Medium, Low over the Weather Station.");
    }

    /// <summary>Advances the benchmark one rendered frame. Called from DrawPresentation, on the same clock its measurements are taken from.</summary>
    /// <param name="state">The frame loop's state, whose frame timings are measured.</param>
    private void UpdateBenchmark(FrameLoopState state)
    {
        if (_benchmarkPendingAuto && !BenchmarkRunning)
        {
            if (_session.Mission is not null)
            {
                // Play was pressed before the run could start: that is a skip.
                _benchmarkPendingAuto = false;
                BenchmarkCVars.BenchmarkDone.Value = true;
                state.Console.SaveSettings();
                _logger.Information("Benchmark", "First-run benchmark skipped: a mission started before it could run.");
            }
            else if (_menuFlow.OverShowcase)
            {
                _benchmarkPendingAuto = false;
                StartBenchmark(state, manual: false);
            }

            return;
        }

        if (!BenchmarkRunning)
        {
            return;
        }

        int now = _loop.TimeSource.Milliseconds;

        // The drone orbit is the showcase camera for the run. Adopting the station rebuilds
        // the menu's camera behind this run's back, so the orbit is put back every frame it is
        // not already the one flying. This runs before the frame's RenderWorld (UpdateBenchmark
        // is called first in DrawPresentation), so the frame below is always the drone's.
        if (_benchmarkCamera is not null && !ReferenceEquals(_showcaseCamera, _benchmarkCamera))
        {
            _showcaseCamera = _benchmarkCamera;
        }

        // Leaving the showcase ends the run as a skip: the menu's Play button, a title switch,
        // or the pause menu. The snapshot goes back, the flag stays set, and the player is
        // wherever they were going.
        if (_session.Mission is not null || !_menuFlow.MenuMode || _menuFlow.PauseMenu)
        {
            AbortBenchmark(state, "ended: a mission started");
            return;
        }

        if (_benchmarkSkipRequested)
        {
            _benchmarkSkipRequested = false;
            AbortBenchmark(state, "skipped");
            return;
        }

        // The player browsed to another mission while the run was measuring it: the scene under
        // the measurement changed, so the tier being measured starts over rather than reporting
        // a number half one level and half another. Before any tier is being measured there is
        // nothing to restart -- the recorded mission just moves, and the wait / settle phases
        // still run their course over whatever backdrop arrives. The menu is hidden during the
        // run, so this can only fire for the run's own swap to the station, which the Waiting
        // phase below is already expecting.
        if (_menuFlow.CurrentShowcaseMissionId != _benchmarkShowcaseMissionId)
        {
            _benchmarkShowcaseMissionId = _menuFlow.CurrentShowcaseMissionId;
            if (_benchmarkPhase is BenchmarkPhase.WarmUp or BenchmarkPhase.Measure)
            {
                _logger.Information("Benchmark", "Showcase mission changed mid-run; re-measuring the current tier.");
                BeginBenchmarkTier(state, now);
            }

            return;
        }

        switch (_benchmarkPhase)
        {
            case BenchmarkPhase.Waiting:
                if (_menuBackdrop.Preview is { MissionId: BenchmarkMissionId } stationPreview
                    && _scene is not null
                    && _arming is { IsArmed: true })
                {
                    _benchmarkShowcaseMissionId = BenchmarkMissionId;
                    BuildBenchmarkScene(stationPreview);
                    _benchmarkPhase = BenchmarkPhase.PreWarm;
                    _benchmarkPhaseStartedMs = now;
                }
                else if (now - _benchmarkPhaseStartedMs > BenchmarkWaitTimeoutMilliseconds)
                {
                    AbortBenchmark(state, "ended: the Weather Station level never arrived");
                }

                break;

            case BenchmarkPhase.Results:
                // The measurement is done; the drone view keeps orbiting behind the results
                // panel until the player picks APPLY or KEEP. Nothing advances here.
                break;

            case BenchmarkPhase.PreWarm:
                if (now - _benchmarkPhaseStartedMs >= BenchmarkPreWarmMilliseconds)
                {
                    BeginBenchmarkTier(state, now);
                }

                break;

            case BenchmarkPhase.WarmUp:
                if (now - _benchmarkPhaseStartedMs >= BenchmarkWarmUpMilliseconds)
                {
                    _benchmarkPhase = BenchmarkPhase.Measure;
                    _benchmarkPhaseStartedMs = now;
                    state.FramePerformance.Reset();
                }

                break;

            case BenchmarkPhase.Measure:
                if (now - _benchmarkPhaseStartedMs >= BenchmarkMeasureMilliseconds)
                {
                    FinishBenchmarkTier(state, now);
                }

                break;
        }
    }

    /// <summary>Builds the benchmark scene once the Weather Station backdrop is up: the drone orbit, the level's weather, and the soldier count for the display.</summary>
    /// <param name="preview">The station's preview, adopted and armed.</param>
    /// <remarks>
    /// The camera is the menu's showcase camera rebuilt over the station's first showcase point
    /// -- the player's spawn, which MissionShowcasePoints puts first, so the orbit circles where
    /// the level put its people -- with one place instead of several (a single place never cuts)
    /// and the benchmark's own turn rate. The soldiers are already armed (the lite profile is
    /// off for the run, so AdoptPreview built them); this only counts them, for the heads-up
    /// display and the log. The weather is built here because a backdrop never builds it: the
    /// renderers are created only by a mission load, so the menu's station would otherwise show
    /// a dry, still night.
    /// </remarks>
    private void BuildBenchmarkScene(MissionPreview preview)
    {
        if (preview.PointsOfInterest.Count > 0)
        {
            ShowcasePoint first = preview.PointsOfInterest[0];
            _benchmarkCamera = new MenuShowcaseCamera(
                [first],
                first.Centre,
                first.Orbit with { SpeedRadiansPerSecond = BenchmarkOrbitSpeedRadiansPerSecond });
            _showcaseCamera = _benchmarkCamera;
        }
        else
        {
            // A level with too little in it to cluster gets no places; the menu's own orbit
            // (the whole level's median, at its own speed) flies instead, and the run still
            // measures the same picture.
            _logger.Information("Benchmark", "The Weather Station has no showcase points; measuring on the menu's orbit.");
        }

        // Best-effort: the count is display-only, and a read that cannot run over a backdrop
        // must not take the run down with it. 0 then means the count did not read, not that
        // the station is empty -- the soldiers are still drawn either way.
        var soldiers = new List<SoldierSnapshot>();
        try
        {
            _menuBackdrop.Soldiers?.Snapshot(soldiers);
            _benchmarkSoldierCount = soldiers.Count;
        }
        catch (Exception ex)
        {
            _benchmarkSoldierCount = 0;
            _logger.Warning("Benchmark", $"Could not count the station's soldiers: {ex.Message}");
        }

        BeginBenchmarkWeather(preview);
        _logger.Information(
            "Benchmark",
            FormattableString.Invariant(
                $"The Weather Station is up: drone orbit over the spawn, {_benchmarkSoldierCount} soldier(s) on station, weather {BenchmarkWeatherText()}."));
    }

    /// <summary>Builds the Weather Station's own weather for the drone view, the way a mission load builds it.</summary>
    /// <param name="preview">The station's preview, whose task tree carries the RainEffect task.</param>
    /// <remarks>
    /// The same three calls MissionSceneView makes, against the preview's own level root, scene
    /// and mission: the renderers read the level's RainEffect task (rain when it says Is Rain,
    /// snow when it says Is Active without Is Rain), the shelter field is the roofs the rain
    /// and snow stay out from under, and ApplyWeatherSettings resolves them against the
    /// player's weather switches. What is NOT built is the original's own weather presenter:
    /// that is made in MissionAdornments against the session's registry, which has no MISSION
    /// alias while the menu is up, so on the Vanilla preset the storm is off (vanilla turns
    /// r_storm off) and the original's rain band draws instead -- either way, the level's
    /// weather falls. Taken back down by ReleaseBenchmarkWeather when the run ends.
    /// </remarks>
    private void BeginBenchmarkWeather(MissionPreview preview)
    {
        if (_session.Mission is not null)
        {
            return;
        }

        _rain?.Dispose();
        _rain = new RainRenderer(_device, _logger);
        _rain.ReadParams(preview.LevelRoot);
        _snow?.Dispose();
        _snow = new SnowRenderer(_device, _logger);
        _snow.ReadParams(preview.LevelRoot);
        _storm?.Dispose();
        _storm = new StormRenderer(_device, _logger);
        BuildShelterField(preview.Scene, preview.Mission);
        _storm.SetShelter(_shelterField);
        ApplyWeatherSettings();
        _benchmarkWeatherBuilt = true;
    }

    /// <summary>Takes the benchmark's weather back down, leaving the menu as dry and still as it was.</summary>
    /// <remarks>
    /// Only while no mission is up: a run aborted because a mission started must not pull the
    /// weather out from under the mission that just built its own.
    /// </remarks>
    private void ReleaseBenchmarkWeather()
    {
        if (!_benchmarkWeatherBuilt || _session.Mission is not null)
        {
            return;
        }

        _benchmarkWeatherBuilt = false;
        _rain?.Dispose();
        _rain = null;
        _snow?.Dispose();
        _snow = null;
        _storm?.Dispose();
        _storm = null;
        ReleaseShelterField();
    }

    /// <summary>Describes the weather falling in the drone view, for the display and the log.</summary>
    private string BenchmarkWeatherText()
    {
        if (_storm is { IsActive: true })
        {
            return "storm";
        }

        if (_snow is { IsWeatherEnabled: true })
        {
            return "snow";
        }

        if (_rain is { IsWeatherEnabled: true })
        {
            return "rain";
        }

        return "none - the level authors none";
    }

    private void BeginBenchmarkTier(FrameLoopState state, int now)
    {
        HardwareTier tier = BenchmarkTierOrder[_benchmarkTierIndex];
        HardwareTiering.ApplyForMeasurement(tier, _window);
        _benchmarkPhase = BenchmarkPhase.WarmUp;
        _benchmarkPhaseStartedMs = now;
        state.FramePerformance.Reset();
        _logger.Information("Benchmark", $"Measuring {tier}: {HardwareTiering.Describe(tier)}, dynamic resolution off.");
    }

    private void FinishBenchmarkTier(FrameLoopState state, int now)
    {
        HardwareTier tier = BenchmarkTierOrder[_benchmarkTierIndex];
        FramePerformanceStatistics stats = state.FramePerformance.Statistics();
        _benchmarkResults.Add(new BenchmarkTierResult(tier, stats.FramesPerSecond, stats.OnePercentLowFramesPerSecond, stats.SampleCount));
        _logger.Information(
            "Benchmark",
            FormattableString.Invariant(
                $"{tier}: mean {stats.FramesPerSecond:0.0} FPS, 1% low {stats.OnePercentLowFramesPerSecond:0.0} FPS over {stats.SampleCount} frames."));

        // The first tier that holds the bar is the answer.
        if (stats.SampleCount >= BenchmarkMinimumSamples && stats.OnePercentLowFramesPerSecond >= BenchmarkPassOnePercentLowFps)
        {
            EnterBenchmarkResults(state, tier);
            return;
        }

        _benchmarkTierIndex++;
        if (_benchmarkTierIndex >= BenchmarkTierOrder.Length)
        {
            // Nothing held 30 FPS: Low is the floor, and its dynamic resolution -- on for Low --
            // is what lifts real play back towards the target from there.
            EnterBenchmarkResults(state, HardwareTier.Low);
            return;
        }

        BeginBenchmarkTier(state, now);
    }

    /// <summary>Opens the results screen: the winner is decided and the run is written down, but nothing is applied yet.</summary>
    /// <param name="state">The frame loop's state, for the console that saves the record.</param>
    /// <param name="winner">The tier the measurement picked.</param>
    /// <remarks>
    /// The record and the log land here, not when the panel closes, so a force-quit on the
    /// results screen still leaves proof the run happened and what it found. Applying the tier
    /// waits for the player's choice on the panel (see <see cref="CloseBenchmark"/>): APPLY takes
    /// the winner, KEEP takes the snapshot back.
    /// </remarks>
    private void EnterBenchmarkResults(FrameLoopState state, HardwareTier winner)
    {
        _benchmarkWinner = winner;
        _benchmarkPhase = BenchmarkPhase.Results;

        string Part(HardwareTier tier) => _benchmarkResults.FirstOrDefault(result => result.Tier == tier) is { } measured
            ? FormattableString.Invariant($"{measured.OnePercentLowFps:0}")
            : "--";
        BenchmarkCVars.LastRun.Value = FormattableString.Invariant(
            $"{DateTime.UtcNow:dd MMM HH:mm} UTC - {winner} - H{Part(HardwareTier.High)} M{Part(HardwareTier.Medium)} L{Part(HardwareTier.Low)} FPS 1% low");
        state.Console.SaveSettings();

        string results = string.Join(", ", _benchmarkResults.Select(result =>
            FormattableString.Invariant($"{result.Tier} {result.OnePercentLowFps:0.0} FPS 1% low")));
        _logger.Information(
            "Benchmark",
            $"{(_benchmarkManual ? "Manual" : "First-run")} benchmark finished. Best tier: {winner} ({HardwareTiering.Describe(winner)}). Measured: {results}. Record: {BenchmarkCVars.LastRun.Value}.");
    }

    /// <summary>Closes the results screen, applying the winner or keeping the player's settings, and hands the menu back.</summary>
    /// <param name="state">The frame loop's state, for the console that saves the choice.</param>
    /// <param name="applyWinner">Whether the winner is applied, or the snapshot restored instead.</param>
    private void CloseBenchmark(FrameLoopState state, bool applyWinner)
    {
        if (applyWinner)
        {
            HardwareTiering.ApplyChoice(_benchmarkWinner, _window);
            RestoreBenchmarkEnvironment();
        }
        else
        {
            RestoreBenchmarkSnapshot();
            RestoreBenchmarkEnvironment(_benchmarkSnapshot);
        }

        RestoreBenchmarkShowcase(state);
        ReleaseBenchmarkWeather();
        RestoreBenchmarkCamera();
        state.Console.SaveSettings();
        _benchmarkPhase = BenchmarkPhase.Idle;
        _logger.Information(
            "Benchmark",
            applyWinner
                ? $"Benchmark closed: {_benchmarkWinner} applied ({HardwareTiering.Describe(_benchmarkWinner)})."
                : "Benchmark closed: the winner was not applied; settings left as they were.");
    }

    /// <summary>Puts the settings snapshot back, as the abort path does when a run never finishes.</summary>
    private void RestoreBenchmarkSnapshot()
    {
        if (_benchmarkSnapshot is not { } snapshot)
        {
            return;
        }

        _window.RenderScaleMode = snapshot.RenderScaleMode;
        ReconstructionCVars.RenderScale.Value = snapshot.RenderScale;
        ReconstructionCVars.FrameLimit.Value = snapshot.FrameLimit;
        ReconstructionCVars.DynamicResolution.Value = snapshot.DynamicResolution;
        ReconstructionCVars.DynamicTargetFps.Value = snapshot.DynamicTargetFps;
        ReconstructionCVars.ViewDistance.Value = snapshot.ViewDistance;
        MaterialCVars.TextureQuality.Value = snapshot.TextureQuality;
    }

    /// <summary>Asks for the menu's previous backdrop again, so the menu is as the player left it.</summary>
    /// <param name="state">The frame loop's state, for the loader that builds it.</param>
    /// <remarks>
    /// Only while no mission is up: a run aborted because a mission started is promoting the
    /// station into that mission, and asking for another backdrop under it would replace the
    /// renderer, the scene and the level's light the mission is about to play under. Skipped
    /// when the menu is already on the station (a manual re-run from there) -- there is nothing
    /// to restore to.
    /// </remarks>
    private void RestoreBenchmarkShowcase(FrameLoopState state)
    {
        if (_session.Mission is not null)
        {
            return;
        }

        if (_benchmarkPreviousShowcaseMissionId <= 0
            || _benchmarkPreviousShowcaseMissionId == BenchmarkMissionId
            || _menuFlow.CurrentShowcaseMissionId != BenchmarkMissionId)
        {
            return;
        }

        _menuFlow.CurrentShowcaseMissionId = _benchmarkPreviousShowcaseMissionId;
        RequestBackdrop(state.PreviewLoader, _benchmarkPreviousShowcaseMissionId);
    }

    /// <summary>Hands the showcase camera back to the menu's own orbit for the level on screen.</summary>
    /// <remarks>
    /// The adoption of the restored backdrop rebuilds it again for that level; this only covers
    /// the level still on screen in the meantime (and the no-restore case, where the station
    /// stays the backdrop). A no-op when there is no preview to take the points from.
    /// </remarks>
    private void RestoreBenchmarkCamera()
    {
        _benchmarkCamera = null;
        if (_menuBackdrop.Preview is { } preview)
        {
            ApplyShowcaseCamera(preview.MissionId, preview.PointsOfInterest);
        }
    }

    private void AbortBenchmark(FrameLoopState state, string reason)
    {
        if (_benchmarkSnapshot is not { } snapshot)
        {
            _benchmarkPhase = BenchmarkPhase.Idle;
            return;
        }

        // The run never finished, so the settings go back to exactly what they were -- the
        // tier the first run had applied, or the player's own on a manual run. No results
        // screen, no record: a skip is not a measurement, and r_benchmark_last keeps whatever
        // the last finished run wrote.
        RestoreBenchmarkSnapshot();
        RestoreBenchmarkEnvironment(snapshot);
        RestoreBenchmarkShowcase(state);
        ReleaseBenchmarkWeather();
        RestoreBenchmarkCamera();
        state.Console.SaveSettings();
        _benchmarkPhase = BenchmarkPhase.Idle;
        _logger.Information("Benchmark", $"Benchmark {reason}; settings left as they were.");
    }

    private void RestoreBenchmarkEnvironment(BenchmarkSettingsSnapshot? snapshot = null)
    {
        // The lite profile and vertical sync go back to what the player had; the tier choice
        // itself is applied by the finish step, or the snapshot restored by the abort step.
        FrameCVars.MenuBackdropLite.ClearOverride();
        FrameCVars.VSync.Value = (snapshot ?? _benchmarkSnapshot)?.VSync ?? FrameCVars.VSync.Value;
        UpdateBackdropProfile();
    }

    /// <summary>Reads the benchmark's taps before the shell sees them, and the Back key as a skip.</summary>
    /// <param name="state">The frame loop's state, for the console that saves the results-screen choice.</param>
    /// <remarks>
    /// The menu is hidden and the shell is deaf while a run is going (see GameHost.FramePasses),
    /// so these are the only live buttons: SKIP while measuring, APPLY and KEEP on the results
    /// screen. Escape does what the on-screen button does -- skip while measuring, apply on the
    /// results screen.
    /// </remarks>
    private void UpdateBenchmarkInput(FrameLoopState state)
    {
        if (!BenchmarkRunning)
        {
            return;
        }

        InputState input = _window.Input;

        if (input.Keyboard.WasPressed(KeyCode.Escape))
        {
            if (_benchmarkPhase == BenchmarkPhase.Results)
            {
                CloseBenchmark(state, applyWinner: true);
            }
            else
            {
                _benchmarkSkipRequested = true;
            }

            return;
        }

        bool click = false;
        float clickX = 0f, clickY = 0f;

        if (input.Touch.InUse)
        {
            foreach (var point in input.Touch.Points)
            {
                if (point.Began)
                {
                    click = true;
                    clickX = point.X;
                    clickY = point.Y;
                    break;
                }
            }
        }

        if (!click && input.Mouse.WasPressed(MouseButton.Left))
        {
            click = true;
            clickX = input.Mouse.X;
            clickY = input.Mouse.Y;
        }

        if (!click)
        {
            return;
        }

        float width = SafeWindowWidth;
        float height = SafeWindowHeight;
        float scale = Math.Clamp(height / 480f, 1f, 3f);

        if (_benchmarkPhase == BenchmarkPhase.Results)
        {
            if (BenchmarkResultsApplyRect(width, height, scale).Contains(clickX, clickY))
            {
                CloseBenchmark(state, applyWinner: true);
                input.Mouse.SetButton(MouseButton.Left, down: false);
            }
            else if (BenchmarkResultsKeepRect(width, height, scale).Contains(clickX, clickY))
            {
                CloseBenchmark(state, applyWinner: false);
                input.Mouse.SetButton(MouseButton.Left, down: false);
            }

            return;
        }

        if (BenchmarkSkipRect(width, height, scale).Contains(clickX, clickY))
        {
            _benchmarkSkipRequested = true;
            input.Mouse.SetButton(MouseButton.Left, down: false);
        }
    }

    // The heads-up display's bottom panel: tier rows and the status line above the SKIP band.
    private const float BenchmarkHudHeight = 132f;

    private static UiRect BenchmarkSkipRect(float width, float height, float scale)
    {
        float bandH = 48f * scale;
        float btnW = 150f * scale;
        float btnH = 34f * scale;
        float btnX = width - btnW - (16f * scale);
        float btnY = height - bandH + ((bandH - btnH) * 0.5f);
        return new UiRect(btnX, btnY, btnW, btnH);
    }

    private static UiRect BenchmarkResultsPanel(float width, float height, float scale)
    {
        float panelW = Math.Min(620f * scale, width - (32f * scale));
        float panelH = 312f * scale;
        return new UiRect((width - panelW) * 0.5f, (height - panelH) * 0.5f, panelW, panelH);
    }

    private static UiRect BenchmarkResultsApplyRect(float width, float height, float scale)
    {
        UiRect panel = BenchmarkResultsPanel(width, height, scale);
        float btnH = 36f * scale;
        float btnW = (panel.Width - (60f * scale)) * 0.55f;
        float btnY = panel.Y + panel.Height - btnH - (16f * scale);
        return new UiRect(panel.X + (20f * scale), btnY, btnW, btnH);
    }

    private static UiRect BenchmarkResultsKeepRect(float width, float height, float scale)
    {
        UiRect panel = BenchmarkResultsPanel(width, height, scale);
        float btnH = 36f * scale;
        float btnW = (panel.Width - (60f * scale)) * 0.45f - (12f * scale);
        float btnY = panel.Y + panel.Height - btnH - (16f * scale);
        return new UiRect(panel.X + panel.Width - btnW - (20f * scale), btnY, btnW, btnH);
    }

    private int BenchmarkSecondsLeft(int now)
    {
        int totalMs = BenchmarkPreWarmMilliseconds
            + (BenchmarkTierOrder.Length * (BenchmarkWarmUpMilliseconds + BenchmarkMeasureMilliseconds));
        return Math.Max(0, (totalMs - (now - _benchmarkStartedMs) + 999) / 1000);
    }

    /// <summary>Draws the benchmark's heads-up display over the drone view, or the results screen when the measurement is done.</summary>
    private void DrawBenchmarkOverlay(FrameLoopState state)
    {
        if (!BenchmarkRunning)
        {
            return;
        }

        _shellType ??= UiFontLibrary.Typography(_device);
        if (_shellType is not { } type)
        {
            return;
        }

        float width = SafeWindowWidth;
        float height = SafeWindowHeight;
        float scale = Math.Clamp(height / 480f, 1f, 3f);

        _device.BeginStageTiming(RenderStage.UserInterface);
        _device.Set2DProjection(SafeWindowWidth, SafeWindowHeight);
        state.Batch.Begin();

        var painter = UiPainter.Create(state.Batch, new LayoutContext(SafeWindowWidth, SafeWindowHeight));

        if (_benchmarkPhase == BenchmarkPhase.Results)
        {
            DrawBenchmarkResults(painter, type, width, height, scale);
        }
        else
        {
            DrawBenchmarkHud(state, painter, type, width, height, scale);
        }

        state.Batch.End();
        _device.EndStageTiming();
    }

    private void DrawBenchmarkHud(FrameLoopState state, UiPainter painter, IgiTypography type, float width, float height, float scale)
    {
        Bgra32 brightGreen = Bgra32.FromRgb(80, 160, 255);
        Bgra32 panelBg = Bgra32.FromRgba(8, 14, 28, 252);
        int now = _loop.TimeSource.Milliseconds;
        double liveFps = state.FramePerformance.Statistics().FramesPerSecond;

        // Top strip: what this is, what is in the picture, and the live frame rate -- the
        // number that proves the test is running rather than a still.
        float stripH = 56f * scale;
        painter.FillRect(new UiRect(0f, 0f, width, stripH), panelBg);
        painter.DrawText(type.Heading, "BENCHMARK - THE WEATHER STATION", new UiVec2(16f * scale, 12f * scale), 18f * scale, Bgra32.White);

        // Before the station is up there is nothing to count or describe yet: the line says
        // the run is loading, not that the level authors no weather and stations no soldiers.
        string sceneLine = _benchmarkWeatherBuilt
            ? FormattableString.Invariant($"DRONE VIEW - {_benchmarkSoldierCount} SOLDIERS ON STATION - {BenchmarkWeatherText().ToUpperInvariant()}")
            : "DRONE VIEW - LOADING THE WEATHER STATION...";
        painter.DrawText(
            type.Mono,
            sceneLine,
            new UiVec2(16f * scale, 36f * scale),
            12f * scale,
            Bgra32.FromRgb(150, 180, 220));

        string fpsText = FormattableString.Invariant($"{liveFps:0} FPS");
        UiVec2 fpsExtent = type.Mono.Measure(fpsText, 26f * scale);
        painter.DrawText(type.Mono, fpsText, new UiVec2(width - fpsExtent.X - (16f * scale), 13f * scale), 26f * scale, brightGreen);

        // Bottom panel: the status line, one row per tier, the progress bar and SKIP.
        float panelY = height - (BenchmarkHudHeight * scale);
        float panelH = BenchmarkHudHeight * scale;
        painter.FillRect(new UiRect(0f, panelY, width, panelH), panelBg);

        // Worst-case progress: 3 s settle + 3 tiers x 7 s. An early exit finishes ahead of it.
        int totalMs = BenchmarkPreWarmMilliseconds
            + (BenchmarkTierOrder.Length * (BenchmarkWarmUpMilliseconds + BenchmarkMeasureMilliseconds));
        float progress = Math.Clamp((now - _benchmarkStartedMs) / (float)totalMs, 0f, 1f);
        painter.FillRect(new UiRect(0f, panelY, width, 1.5f * scale), Bgra32.FromRgb(40, 70, 110));
        painter.FillRect(new UiRect(0f, panelY, width * progress, 3f * scale), brightGreen);

        int secondsLeft = BenchmarkSecondsLeft(now);
        string status = _benchmarkPhase switch
        {
            BenchmarkPhase.Waiting => $"LOADING THE WEATHER STATION... UP TO {secondsLeft}s LEFT",
            BenchmarkPhase.PreWarm => $"WARMING UP - LETTING THE LIGHTING SETTLE... UP TO {secondsLeft}s LEFT",
            BenchmarkPhase.WarmUp => FormattableString.Invariant(
                $"TESTING {BenchmarkTierOrder[_benchmarkTierIndex]} ({_benchmarkTierIndex + 1} OF {BenchmarkTierOrder.Length})... UP TO {secondsLeft}s LEFT"),
            BenchmarkPhase.Measure => FormattableString.Invariant(
                $"TESTING {BenchmarkTierOrder[_benchmarkTierIndex]} ({_benchmarkTierIndex + 1} OF {BenchmarkTierOrder.Length})... UP TO {secondsLeft}s LEFT"),
            _ => "BENCHMARK",
        };
        painter.DrawText(type.Heading, status, new UiVec2(16f * scale, panelY + (10f * scale)), 14f * scale, Bgra32.White);

        float rowY = panelY + (34f * scale);
        float rowH = 26f * scale;
        for (int i = 0; i < BenchmarkTierOrder.Length; i++)
        {
            HardwareTier tier = BenchmarkTierOrder[i];
            string tierStatus;
            if (_benchmarkResults.FirstOrDefault(result => result.Tier == tier) is { } done)
            {
                tierStatus = done.OnePercentLowFps >= BenchmarkPassOnePercentLowFps
                    ? FormattableString.Invariant($"PASS - {done.OnePercentLowFps:0.0} FPS 1% low  (mean {done.MeanFps:0.0}, {done.Samples} frames)")
                    : FormattableString.Invariant($"BELOW 30 - {done.OnePercentLowFps:0.0} FPS 1% low  (mean {done.MeanFps:0.0}, {done.Samples} frames)");
            }
            else if (_benchmarkPhase is BenchmarkPhase.WarmUp or BenchmarkPhase.Measure
                && _benchmarkTierIndex < BenchmarkTierOrder.Length
                && BenchmarkTierOrder[_benchmarkTierIndex] == tier)
            {
                tierStatus = _benchmarkPhase == BenchmarkPhase.Measure
                    ? FormattableString.Invariant($"MEASURING... {liveFps:0} FPS")
                    : "WARMING UP...";
            }
            else
            {
                tierStatus = "QUEUED";
            }

            Bgra32 tierColour = tierStatus.StartsWith("PASS", StringComparison.Ordinal) ? brightGreen : Bgra32.White;
            painter.DrawText(type.Mono, tier.ToString().ToUpperInvariant(), new UiVec2(16f * scale, rowY + (i * rowH)), 13f * scale, Bgra32.FromRgb(150, 180, 220));
            painter.DrawText(type.Mono, tierStatus, new UiVec2(110f * scale, rowY + (i * rowH)), 13f * scale, tierColour);
        }

        UiRect skip = BenchmarkSkipRect(width, height, scale);
        painter.FillRect(skip, Bgra32.FromRgba(25, 32, 50, 200));
        painter.StrokeRect(skip, 1.5f * scale, brightGreen);
        string skipText = "[ SKIP ]";
        UiVec2 extent = type.Mono.Measure(skipText, 14f * scale);
        painter.DrawText(
            type.Mono,
            skipText,
            new UiVec2(skip.X + ((skip.Width - extent.X) * 0.5f), skip.Y + (9f * scale)),
            14f * scale,
            Bgra32.White);
    }

    private void DrawBenchmarkResults(UiPainter painter, IgiTypography type, float width, float height, float scale)
    {
        Bgra32 brightGreen = Bgra32.FromRgb(80, 160, 255);
        Bgra32 panelBg = Bgra32.FromRgba(8, 14, 28, 252);

        // Dimmed, not blacked out: the drone view keeps orbiting behind the panel.
        painter.FillRect(new UiRect(0f, 0f, width, height), Bgra32.FromRgba(0, 0, 0, 120));

        UiRect panel = BenchmarkResultsPanel(width, height, scale);
        painter.FillRect(panel, panelBg);
        painter.StrokeRect(panel, 1.5f * scale, brightGreen);

        float x = panel.X + (20f * scale);
        painter.DrawText(type.Heading, "BENCHMARK COMPLETE", new UiVec2(x, panel.Y + (16f * scale)), 20f * scale, Bgra32.White);
        painter.DrawText(
            type.Mono,
            FormattableString.Invariant($"THE WEATHER STATION - DRONE VIEW - {_benchmarkSoldierCount} SOLDIERS - {BenchmarkWeatherText().ToUpperInvariant()}"),
            new UiVec2(x, panel.Y + (44f * scale)),
            12f * scale,
            Bgra32.FromRgb(150, 180, 220));

        float rowY = panel.Y + (72f * scale);
        float rowH = 40f * scale;
        for (int i = 0; i < BenchmarkTierOrder.Length; i++)
        {
            HardwareTier tier = BenchmarkTierOrder[i];
            float y = rowY + (i * rowH);
            bool isWinner = tier == _benchmarkWinner;
            if (isWinner)
            {
                painter.FillRect(new UiRect(panel.X + (8f * scale), y, panel.Width - (16f * scale), rowH - (6f * scale)), Bgra32.FromRgba(40, 90, 140, 110));
                painter.FillRect(new UiRect(panel.X + (8f * scale), y, 4f * scale, rowH - (6f * scale)), brightGreen);
            }

            string name = isWinner ? $"{tier.ToString().ToUpperInvariant()} - BEST" : tier.ToString().ToUpperInvariant();
            painter.DrawText(type.Mono, name, new UiVec2(x, y + (4f * scale)), 14f * scale, Bgra32.White);

            if (_benchmarkResults.FirstOrDefault(result => result.Tier == tier) is { } done)
            {
                string numbers = FormattableString.Invariant($"mean {done.MeanFps:0.0}  1% low {done.OnePercentLowFps:0.0}  {done.Samples} frames");
                painter.DrawText(type.Mono, numbers, new UiVec2(x, y + (22f * scale)), 12f * scale, Bgra32.FromRgb(150, 180, 220));
                string verdict = done.OnePercentLowFps >= BenchmarkPassOnePercentLowFps ? "PASS" : "BELOW 30";
                UiVec2 verdictExtent = type.Mono.Measure(verdict, 13f * scale);
                painter.DrawText(
                    type.Mono,
                    verdict,
                    new UiVec2(panel.X + panel.Width - verdictExtent.X - (20f * scale), y + (10f * scale)),
                    13f * scale,
                    verdict == "PASS" ? brightGreen : Bgra32.FromRgb(255, 120, 90));
            }
            else
            {
                painter.DrawText(type.Mono, "not measured - a higher tier already held 30 FPS", new UiVec2(x, y + (22f * scale)), 12f * scale, Bgra32.FromRgb(150, 180, 220));
                string verdict = "-";
                UiVec2 verdictExtent = type.Mono.Measure(verdict, 13f * scale);
                painter.DrawText(type.Mono, verdict, new UiVec2(panel.X + panel.Width - verdictExtent.X - (20f * scale), y + (10f * scale)), 13f * scale, Bgra32.FromRgb(150, 180, 220));
            }
        }

        float winnerY = rowY + (BenchmarkTierOrder.Length * rowH) + (8f * scale);
        painter.DrawText(
            type.Heading,
            $"BEST FOR THIS DEVICE: {_benchmarkWinner.ToString().ToUpperInvariant()}",
            new UiVec2(x, winnerY),
            15f * scale,
            brightGreen);
        painter.DrawText(type.Mono, HardwareTiering.Describe(_benchmarkWinner), new UiVec2(x, winnerY + (22f * scale)), 12f * scale, Bgra32.White);

        UiRect apply = BenchmarkResultsApplyRect(width, height, scale);
        painter.FillRect(apply, Bgra32.FromRgba(25, 32, 50, 200));
        painter.StrokeRect(apply, 1.5f * scale, brightGreen);
        string applyText = $"APPLY {_benchmarkWinner.ToString().ToUpperInvariant()} & CONTINUE";
        UiVec2 applyExtent = type.Mono.Measure(applyText, 12f * scale);
        painter.DrawText(
            type.Mono,
            applyText,
            new UiVec2(apply.X + ((apply.Width - applyExtent.X) * 0.5f), apply.Y + (11f * scale)),
            12f * scale,
            Bgra32.White);

        UiRect keep = BenchmarkResultsKeepRect(width, height, scale);
        painter.FillRect(keep, Bgra32.FromRgba(25, 32, 50, 200));
        painter.StrokeRect(keep, 1.5f * scale, Bgra32.FromRgb(70, 100, 140));
        string keepText = "KEEP MY SETTINGS";
        UiVec2 keepExtent = type.Mono.Measure(keepText, 12f * scale);
        painter.DrawText(
            type.Mono,
            keepText,
            new UiVec2(keep.X + ((keep.Width - keepExtent.X) * 0.5f), keep.Y + (11f * scale)),
            12f * scale,
            Bgra32.White);
    }
}
