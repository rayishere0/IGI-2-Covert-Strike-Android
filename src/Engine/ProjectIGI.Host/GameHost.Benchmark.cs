using ProjectIGI.Core.Graphics;
using ProjectIGI.Host.Configuration;
using ProjectIGI.Input;
using ProjectIGI.Presentation.Text;
using ProjectIGI.Rendering;
using ProjectIGI.UI;
using ProjectIGI.UI.Layout;

namespace ProjectIGI.Host;

public sealed partial class GameHost
{
    // The first-run benchmark (Ray: "benchmark testing with all of the settings rolling as soon as
    // the files are selected, and out of all, pick the best one and apply it, and then boot into the
    // game ... users can perform it again via clicking the gear icon, in the cheats menu").
    //
    // What it measures, honestly: the main menu's showcase level -- mission 1, Infiltration -- as
    // the world renderer draws it, with the backdrop's lite profile lifted so the full preset
    // (real-time GI, shadows, traces, gathers) is what is measured, not the lite picture. That is
    // the closest the game gets to a mission without loading one: no mission load, no AI, no player,
    // no cutscene, no soldiers (a lite backdrop never arms them). It is a render-cost measurement
    // on the mission-1 level, not a played mission, and the picked tier can still be optimistic for
    // a firefight -- the 30 FPS bar is on the 1% low, and Low keeps dynamic resolution on, so real
    // play has its own ladder back towards the target. Measuring inside a played mission would
    // need the player dropped into live gameplay with AI -- a bigger change, and a stranger
    // first run -- so this is the shape the code can actually measure.
    //
    // It cannot run in the file picker itself: no world is loaded there and no frame has been
    // drawn, so there is nothing to measure yet. It runs on the first menu frames instead, right
    // after the files are selected and the game boots -- the player is looking at the menu over
    // the level while it measures, and lands on the menu with the best tier applied when it ends.

    private enum BenchmarkPhase
    {
        Idle,
        Waiting,
        PreWarm,
        WarmUp,
        Measure,
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

    // How long to wait for the menu level before giving up and leaving the tiered settings alone.
    private const int BenchmarkWaitTimeoutMilliseconds = 30000;

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
    }

    /// <summary>Starts a benchmark run over the menu level, from the first run or the cheats menu.</summary>
    /// <param name="state">The frame loop's state, for the console that saves the result.</param>
    /// <param name="manual">Whether the run was asked for from the cheats menu.</param>
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
        _logger.Information(
            "Benchmark",
            manual
                ? "Benchmark started from the cheats menu: measuring High, Medium, Low on the menu level."
                : "First-run benchmark started: measuring High, Medium, Low on the menu level.");
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
        // still run their course over whatever backdrop arrives.
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
                if (_menuBackdrop.Preview is not null && _scene is not null && _arming is { IsArmed: true })
                {
                    _benchmarkPhase = BenchmarkPhase.PreWarm;
                    _benchmarkPhaseStartedMs = now;
                }
                else if (now - _benchmarkPhaseStartedMs > BenchmarkWaitTimeoutMilliseconds)
                {
                    AbortBenchmark(state, "ended: the menu level never arrived");
                }

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
            FinishBenchmark(state, tier);
            return;
        }

        _benchmarkTierIndex++;
        if (_benchmarkTierIndex >= BenchmarkTierOrder.Length)
        {
            // Nothing held 30 FPS: Low is the floor, and its dynamic resolution -- on for Low --
            // is what lifts real play back towards the target from there.
            FinishBenchmark(state, HardwareTier.Low);
            return;
        }

        BeginBenchmarkTier(state, now);
    }

    private void FinishBenchmark(FrameLoopState state, HardwareTier winner)
    {
        HardwareTiering.ApplyChoice(winner, _window);
        RestoreBenchmarkEnvironment();
        state.Console.SaveSettings();
        _benchmarkPhase = BenchmarkPhase.Idle;

        string results = string.Join(", ", _benchmarkResults.Select(result =>
            FormattableString.Invariant($"{result.Tier} {result.OnePercentLowFps:0.0} FPS 1% low")));
        _logger.Information(
            "Benchmark",
            $"{(_benchmarkManual ? "Manual" : "First-run")} benchmark finished. Best tier: {winner} ({HardwareTiering.Describe(winner)}). Measured: {results}.");
    }

    private void AbortBenchmark(FrameLoopState state, string reason)
    {
        if (_benchmarkSnapshot is not { } snapshot)
        {
            _benchmarkPhase = BenchmarkPhase.Idle;
            return;
        }

        // The run never finished, so the settings go back to exactly what they were -- the
        // tier the first run had applied, or the player's own on a manual run.
        _window.RenderScaleMode = snapshot.RenderScaleMode;
        ReconstructionCVars.RenderScale.Value = snapshot.RenderScale;
        ReconstructionCVars.FrameLimit.Value = snapshot.FrameLimit;
        ReconstructionCVars.DynamicResolution.Value = snapshot.DynamicResolution;
        ReconstructionCVars.DynamicTargetFps.Value = snapshot.DynamicTargetFps;
        ReconstructionCVars.ViewDistance.Value = snapshot.ViewDistance;
        MaterialCVars.TextureQuality.Value = snapshot.TextureQuality;
        RestoreBenchmarkEnvironment(snapshot);
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

    /// <summary>Reads the SKIP tap before the shell sees it. The bar sits at the bottom of the screen, where no menu button is.</summary>
    private void UpdateBenchmarkInput()
    {
        if (!BenchmarkRunning)
        {
            return;
        }

        InputState input = _window.Input;
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
        if (BenchmarkSkipRect(width, height, scale).Contains(clickX, clickY))
        {
            _benchmarkSkipRequested = true;
            input.Mouse.SetButton(MouseButton.Left, down: false);
        }
    }

    private static UiRect BenchmarkSkipRect(float width, float height, float scale)
    {
        float barH = 48f * scale;
        float btnW = 150f * scale;
        float btnH = 34f * scale;
        float btnX = width - btnW - (16f * scale);
        float btnY = height - barH + ((barH - btnH) * 0.5f);
        return new UiRect(btnX, btnY, btnW, btnH);
    }

    private int BenchmarkSecondsLeft(int now)
    {
        int totalMs = BenchmarkPreWarmMilliseconds
            + (BenchmarkTierOrder.Length * (BenchmarkWarmUpMilliseconds + BenchmarkMeasureMilliseconds));
        return Math.Max(0, (totalMs - (now - _benchmarkStartedMs) + 999) / 1000);
    }

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
        float barH = 48f * scale;
        int now = _loop.TimeSource.Milliseconds;

        _device.BeginStageTiming(RenderStage.UserInterface);
        _device.Set2DProjection(SafeWindowWidth, SafeWindowHeight);
        state.Batch.Begin();

        var painter = UiPainter.Create(state.Batch, new LayoutContext(SafeWindowWidth, SafeWindowHeight));

        Bgra32 brightGreen = Bgra32.FromRgb(80, 160, 255);
        painter.FillRect(new UiRect(0f, height - barH, width, barH), Bgra32.FromRgba(8, 14, 28, 252));

        // Worst-case progress: 3 s settle + 3 tiers x 7 s. An early exit finishes ahead of it.
        int totalMs = BenchmarkPreWarmMilliseconds
            + (BenchmarkTierOrder.Length * (BenchmarkWarmUpMilliseconds + BenchmarkMeasureMilliseconds));
        float progress = Math.Clamp((now - _benchmarkStartedMs) / (float)totalMs, 0f, 1f);
        painter.FillRect(new UiRect(0f, height - barH, width, 1.5f * scale), Bgra32.FromRgb(40, 70, 110));
        painter.FillRect(new UiRect(0f, height - barH, width * progress, 3f * scale), brightGreen);

        int secondsLeft = BenchmarkSecondsLeft(now);
        string status = _benchmarkPhase switch
        {
            BenchmarkPhase.Waiting => $"BENCHMARK - preparing the menu level... up to {secondsLeft}s left",
            BenchmarkPhase.PreWarm => $"BENCHMARK - warming up... up to {secondsLeft}s left",
            BenchmarkPhase.WarmUp => FormattableString.Invariant(
                $"BENCHMARK - testing {BenchmarkTierOrder[_benchmarkTierIndex]} ({_benchmarkTierIndex + 1} of {BenchmarkTierOrder.Length})... up to {secondsLeft}s left"),
            BenchmarkPhase.Measure => FormattableString.Invariant(
                $"BENCHMARK - testing {BenchmarkTierOrder[_benchmarkTierIndex]} ({_benchmarkTierIndex + 1} of {BenchmarkTierOrder.Length})  {state.FramePerformance.Statistics().FramesPerSecond:0} FPS  up to {secondsLeft}s left"),
            _ => "BENCHMARK",
        };

        painter.DrawText(type.Heading, status, new UiVec2(16f * scale, height - barH + (15f * scale)), 15f * scale, Bgra32.White);

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

        state.Batch.End();
        _device.EndStageTiming();
    }
}
