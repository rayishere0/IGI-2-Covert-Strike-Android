using ProjectIGI.Core.Configuration;

namespace ProjectIGI.Host.Configuration;

/// <summary>Whether the first-run benchmark has been run or skipped.</summary>
/// <remarks>
/// Archived, so the answer survives to the next launch: the benchmark is a one-time test, and a
/// first run that finished it -- or skipped it -- must never be asked again. An existing install
/// has a settings file but no such line, so the flag alone never arms the run: arming also needs
/// no settings file to have loaded at all (see <c>GameHost.Benchmark</c>).
/// </remarks>
[CVarContainer]
public static class BenchmarkCVars
{
    /// <summary>Whether the first-run benchmark has finished or been skipped.</summary>
    public static readonly BoolCVar BenchmarkDone = CVar.Bool(
        "r_benchmark_done",
        false,
        CVarOptions.Archive,
        "Whether the first-run benchmark has finished or been skipped; set so it never runs unprompted again.");
}
