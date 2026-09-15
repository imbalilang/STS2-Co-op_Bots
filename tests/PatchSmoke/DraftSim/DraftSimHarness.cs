using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeckSim;

/// <summary>One run, flattened for the report. Deck lists are kept: they are what a regression gets read against.</summary>
internal sealed record RunSummary(
    string Character, ulong Seed, string Route, int Size, int Curses, int Upgrades,
    int Taken, int Skipped, int Removals, int Smiths, int CursesAdded, int GoldEarned, int GoldSpent,
    double Power, double Offence, double Defence, double Consistency,
    double DamagePerTurn, double BlockPerTurn, double TurnsToKill, double KillRate, double Coverage, double DeadDraw,
    double EnergyWaste, double Ramp, string Deck, StageSummary[] Trajectory);

/// <summary>One deck snapshot in a run's act-by-act trajectory.</summary>
internal sealed record StageSummary(
    string Stage, double Size, double Curses, double Upgrades, double Power, double Offence, double Defence,
    double DamagePerTurn, double BlockPerTurn, double TurnsToKill, double KillRate, double Coverage, double DeadDraw);

/// <summary>What one character's seed batch averaged. The unit a parameter change is judged on.</summary>
internal sealed record CharacterSummary(
    string Character, int Runs,
    double Power, double Offence, double Defence, double Consistency,
    double DamagePerTurn, double BlockPerTurn, double TurnsToKill, double KillRate, double Coverage, double DeadDraw,
    double EnergyWaste, double Ramp, double Size, double Curses, double Upgrades,
    double Taken, double Skipped, double Removals, double Smiths, double CursesAdded,
    StageSummary[] Trajectory);

/// <summary>The whole sweep, as written to disk.</summary>
internal sealed record SimReport(
    string Label, string GeneratedBy, string[] Notes, int RunsPerCharacter,
    double MeanPower, CharacterSummary[] Characters, RunSummary[] Runs);

/// <summary>
/// Runs the sweep and turns it into something a change can be judged on.
///
/// Fixed seeds are the contract: run N of a character always uses the same seed,
/// so a report taken before a parameter change and one taken after differ only
/// by the change. Everything the report prints is therefore a distribution over
/// the same fights, and a single lucky run cannot pass for an improvement.
/// </summary>
internal static class DraftSimHarness
{
    internal const string ReportDirectory = "outputs/deck-sim";

    internal static SimReport Run(SimConfig config, Action<string>? progress = null)
    {
        var runs = new List<DraftRunResult>();
        foreach (var character in config.Characters)
        {
            for (var index = 0; index < config.RunsPerCharacter; index++)
            {
                var seed = config.Seed + (ulong)index;
                runs.Add(DraftRunner.Run(character, seed, config));
                progress?.Invoke($"  {character} run {index + 1}/{config.RunsPerCharacter}: "
                    + $"power={runs[^1].Final.Power:F1} size={runs[^1].FinalSize} curses={runs[^1].Curses} route={runs[^1].Route}");
            }
        }

        var summaries = runs.GroupBy(run => run.Character)
            .Select(group => Summarise(group.Key, group.ToList()))
            .ToArray();

        return new SimReport(
            Label: config.Label("decksim"),
            GeneratedBy: "tests/PatchSmoke --deck-sim",
            Notes: Notes(),
            RunsPerCharacter: config.RunsPerCharacter,
            MeanPower: summaries.Length == 0 ? 0 : summaries.Average(s => s.Power),
            Characters: summaries,
            Runs: runs.Select(Flatten).ToArray());
    }

    private static CharacterSummary Summarise(string character, List<DraftRunResult> runs)
    {
        var trajectory = runs
            .SelectMany(run => run.Trajectory)
            .GroupBy(stage => stage.Stage)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StageSummary(
                group.Key,
                Mean(group.Select(s => (double)s.Size)),
                Mean(group.Select(s => (double)s.Curses)),
                Mean(group.Select(s => (double)s.Upgrades)),
                Mean(group.Select(s => s.Score.Power)),
                Mean(group.Select(s => s.Score.Offence)),
                Mean(group.Select(s => s.Score.Defence)),
                Mean(group.Select(s => s.Score.DamagePerTurn)),
                Mean(group.Select(s => s.Score.BlockPerTurn)),
                Mean(group.Select(s => s.Score.TurnsToKill)),
                Mean(group.Select(s => s.Score.KillRate)),
                Mean(group.Select(s => s.Score.BlockCoverage)),
                Mean(group.Select(s => s.Score.DeadDrawRate))))
            .ToArray();

        return new CharacterSummary(
            character, runs.Count,
            Mean(runs.Select(r => r.Final.Power)),
            Mean(runs.Select(r => r.Final.Offence)),
            Mean(runs.Select(r => r.Final.Defence)),
            Mean(runs.Select(r => r.Final.Consistency)),
            Mean(runs.Select(r => r.Final.DamagePerTurn)),
            Mean(runs.Select(r => r.Final.BlockPerTurn)),
            Mean(runs.Select(r => r.Final.TurnsToKill)),
            Mean(runs.Select(r => r.Final.KillRate)),
            Mean(runs.Select(r => r.Final.BlockCoverage)),
            Mean(runs.Select(r => r.Final.DeadDrawRate)),
            Mean(runs.Select(r => r.Final.EnergyWaste)),
            Mean(runs.Select(r => r.Final.RampRatio)),
            Mean(runs.Select(r => (double)r.FinalSize)),
            Mean(runs.Select(r => (double)r.Curses)),
            Mean(runs.Select(r => (double)r.Upgrades)),
            Mean(runs.Select(r => (double)r.RewardsTaken)),
            Mean(runs.Select(r => (double)r.RewardsSkipped)),
            Mean(runs.Select(r => (double)r.Removals)),
            Mean(runs.Select(r => (double)r.Smiths)),
            Mean(runs.Select(r => (double)r.CursesAdded)),
            trajectory);
    }

    private static RunSummary Flatten(DraftRunResult run) => new(
        run.Character, run.Seed, run.Route, run.FinalSize, run.Curses, run.Upgrades,
        run.RewardsTaken, run.RewardsSkipped, run.Removals, run.Smiths, run.CursesAdded,
        run.GoldEarned, run.GoldSpent,
        run.Final.Power, run.Final.Offence, run.Final.Defence, run.Final.Consistency,
        run.Final.DamagePerTurn, run.Final.BlockPerTurn, run.Final.TurnsToKill, run.Final.KillRate,
        run.Final.BlockCoverage, run.Final.DeadDrawRate, run.Final.EnergyWaste, run.Final.RampRatio,
        run.FinalDeck,
        run.Trajectory.Select(stage => new StageSummary(
            stage.Stage, stage.Size, stage.Curses, stage.Upgrades, stage.Score.Power,
            stage.Score.Offence, stage.Score.Defence, stage.Score.DamagePerTurn, stage.Score.BlockPerTurn,
            stage.Score.TurnsToKill, stage.Score.KillRate, stage.Score.BlockCoverage, stage.Score.DeadDrawRate)).ToArray());

    private static double Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    /// <summary>
    /// The parts of a real run this pipeline does not reproduce. Printed into the
    /// report on purpose: a number is only worth acting on if what it leaves out
    /// is written next to it.
    /// </summary>
    internal static string[] Notes() =>
    [
        "No combat and no HP: encounters are a gold payout, a card reward and a curse roll.",
        "Relics, potions and the map graph are not modelled; chests are a no-op.",
        "Rest sites always smith (the live heal-versus-smith line needs HP).",
        "Shop card purchases are not modelled; shops only remove.",
        "Reference fights are per-act HP and damage constants, not real encounters.",
        "Card text is read through CardProfile facts, not executed: temporary Strength counts as permanent, Focus is folded into damage, Doom is folded into delayed damage.",
    ];

    // ---- Output -------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string Write(SimReport report, string? path = null)
    {
        path ??= Path.Combine(RepoRoot(), ReportDirectory, report.Label + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        return path;
    }

    internal static string? FindBaseline(string label)
    {
        var path = Path.Combine(RepoRoot(), ReportDirectory, label);
        return File.Exists(path) ? path : null;
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "CoopBots", "mod_manifest.json")))
                return directory.FullName;
        return AppContext.BaseDirectory;
    }

    internal static void Print(SimReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"DECK-SIM {report.Label}  ({report.Runs.Length} runs, {report.RunsPerCharacter} seeds/character)");
        Console.WriteLine("character   power  off  def  cons | dmg/turn blk/turn  kill cover wasted dead ramp | size curse upg | take skip rm smith");
        foreach (var c in report.Characters)
            Console.WriteLine($"{c.Character,-11} {c.Power,5:F1} {c.Offence,4:F0} {c.Defence,4:F0} {c.Consistency,5:F0} |"
                + $" {c.DamagePerTurn,8:F1} {c.BlockPerTurn,8:F1} {c.TurnsToKill,3:F1}/{c.KillRate:P0} {c.Coverage,5:F2} {c.EnergyWaste,6:F2} {c.DeadDraw,4:F2} {c.Ramp,4:F2} |"
                + $" {c.Size,4:F1} {c.Curses,5:F1} {c.Upgrades,3:F1} | {c.Taken,4:F1} {c.Skipped,4:F1} {c.Removals,2:F1} {c.Smiths,5:F1}");
        Console.WriteLine($"all characters mean power: {report.MeanPower:F1}");
        Console.WriteLine();
        Console.WriteLine("trajectory (mean power per act, same seeds)");
        foreach (var c in report.Characters)
            Console.WriteLine($"{c.Character,-11} " + string.Join("  ", c.Trajectory.Select(stage =>
                $"{stage.Stage}={stage.Power:F1}(size {stage.Size:F1}, kill {stage.KillRate:P0}, dead {stage.DeadDraw:F2})")));
    }

    /// <summary>
    /// Prints what moved between a stored report and this one. The pipeline exists
    /// to be iterated against, so the comparison is part of it rather than a script
    /// the next person has to write.
    /// </summary>
    internal static void PrintDiff(SimReport baseline, SimReport current)
    {
        Console.WriteLine();
        Console.WriteLine($"DECK-SIM DELTA vs {baseline.Label}");
        Console.WriteLine("character   power  off  def  cons | dmg/turn blk/turn  kill cover dead | size curse upg | rm smith");
        foreach (var now in current.Characters)
        {
            var was = baseline.Characters.FirstOrDefault(c => c.Character == now.Character);
            if (was is null) continue;
            Console.WriteLine($"{now.Character,-11} {Delta(now.Power, was.Power),5:+0.0;-0.0;0.0} {Delta(now.Offence, was.Offence),4:+0;-0;0}"
                + $" {Delta(now.Defence, was.Defence),4:+0;-0;0} {Delta(now.Consistency, was.Consistency),5:+0;-0;0} |"
                + $" {Delta(now.DamagePerTurn, was.DamagePerTurn),8:+0.0;-0.0;0.0} {Delta(now.BlockPerTurn, was.BlockPerTurn),8:+0.0;-0.0;0.0}"
                + $" {Delta(now.KillRate, was.KillRate),4:+0%;-0%;0%} {Delta(now.Coverage, was.Coverage),5:+0.00;-0.00;0.00}"
                + $" {Delta(now.DeadDraw, was.DeadDraw),4:+0.00;-0.00;0.00} |"
                + $" {Delta(now.Size, was.Size),4:+0.0;-0.0;0.0} {Delta(now.Curses, was.Curses),5:+0.0;-0.0;0.0} {Delta(now.Upgrades, was.Upgrades),3:+0.0;-0.0;0.0}"
                + $" | {Delta(now.Removals, was.Removals),2:+0.0;-0.0;0.0} {Delta(now.Smiths, was.Smiths),5:+0.0;-0.0;0.0}");
        }
        Console.WriteLine($"all characters mean power: {Delta(current.MeanPower, baseline.MeanPower):+0.0;-0.0;0.0}");
    }

    private static double Delta(double now, double before) => now - before;
}
