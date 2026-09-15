using DeckSim;

// The deck-construction pipeline. It is not part of the pass/fail regression
// suite — a full sweep takes minutes — so it only runs when asked for:
//
//   --deck-sim                       sweep the default characters and seeds
//   --deck-sim-only                  the same, skipping the regression assertions
//   --deck-sim --runs=24             more seeds per character
//   --deck-sim --chars=IRONCLAD      one character
//   --deck-sim --seed=123            a different seed block
//   --deck-sim --ascension=0         the same sweep without A10
//   --deck-sim --baseline            diff against the last stored report
//   --deck-sim --out=path.json       where to write the report
//
// What it is for: change a weight in BuildValue (or any drafting rule), rerun
// the same seeds, and read the delta. The seeds are fixed so the only thing that
// moves between two reports is the change.
internal static class DraftSimScenarios
{
    internal static void Run(string[] args)
    {
        // --deck-sim-only routes here directly from Program (skipping the
        // regression assertions), so either flag means "run the sweep".
        if (!args.Contains("--deck-sim") && !args.Contains("--deck-sim-only")) return;
        TestEnvironment.Ensure();
        var config = Parse(args, SimConfig.Default);
        Console.WriteLine($"DECK-SIM sweeping {string.Join(", ", config.Characters)} "
            + $"— {config.RunsPerCharacter} runs each from seed {config.Seed}"
            + (config.Ascension10 ? " (A10)" : " (no ascension)"));

        var started = Environment.TickCount64;
        VerifyScorer(config);
        VerifyDeterminism(config);
        var report = DraftSimHarness.Run(config, message => Console.WriteLine(message));
        DraftSimHarness.Print(report);

        // The previous report is read before this one is written, so "diff against
        // the last run" compares against the last run and not against itself.
        var baselinePath = ArgValue(args, "--baseline") ?? DraftSimHarness.FindBaseline(report.Label + ".json");
        var baseline = baselinePath is not null && File.Exists(baselinePath)
            ? System.Text.Json.JsonSerializer.Deserialize<SimReport>(File.ReadAllText(baselinePath))
            : null;

        var path = DraftSimHarness.Write(report, ArgValue(args, "--out"));
        Console.WriteLine($"report: {path}  ({Environment.TickCount64 - started} ms)");
        if (baseline is not null) DraftSimHarness.PrintDiff(baseline, report);
    }

    private static SimConfig Parse(string[] args, SimConfig config)
    {
        var runs = ArgValue(args, "--runs");
        if (runs is not null) config = config with { RunsPerCharacter = Math.Max(1, int.Parse(runs)) };
        var chars = ArgValue(args, "--chars");
        if (chars is not null) config = config with { Characters = chars.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        var seed = ArgValue(args, "--seed");
        if (seed is not null) config = config with { Seed = ulong.Parse(seed) };
        // --ascension=0 runs the identical sweep at ascension 0, which is how the
        // pipeline's own A10 model is checked: the difference between the two
        // reports is exactly what the ascension is worth.
        var ascension = ArgValue(args, "--ascension");
        if (ascension is not null) config = config with { Ascension10 = ascension != "0" };
        if (args.Contains("--quick")) config = config with { RunsPerCharacter = 3, ScoreShuffles = 4 };
        return config;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var prefix = name + "=";
        return args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    /// <summary>
    /// Checks the scorer points the right way before the sweep wastes minutes on
    /// it. A scoring function that cannot tell a deck of Defends from a deck of
    /// Strikes is not measuring anything, and every number it prints afterwards
    /// would look equally authoritative.
    /// </summary>
    private static void VerifyScorer(SimConfig config)
    {
        var player = DraftRunner.NewPlayer("Ironclad", 1);
        var offense = DraftRunner.DeckOf(player, "STRIKE_IRONCLAD", 10);
        var defence = DraftRunner.DeckOf(player, "DEFEND_IRONCLAD", 10);
        var reference = config.References[0];
        var attack = DeckScorer.Score(offense, reference, config, 1);
        var guard = DeckScorer.Score(defence, reference, config, 1);
        if (!(attack.Offence > guard.Offence))
            throw new Exception($"DECK-SIM scorer: ten Strikes must out-damage ten Defends "
                + $"({attack.Offence:F1} vs {guard.Offence:F1}).");
        if (!(guard.Defence > attack.Defence))
            throw new Exception($"DECK-SIM scorer: ten Defends must out-block ten Strikes "
                + $"({guard.Defence:F1} vs {attack.Defence:F1}).");
        // Curses have to cost something, or the pipeline cannot price A10's tax.
        // Calibrated against the energy cap rather than against intuition: three
        // energy buys three cards, so a deck that still draws three playable cards
        // is unharmed by a curse that sits in hand. The cost only appears once the
        // curses crowd the hand past that line, which is exactly when a real deck
        // starts losing turns — and the dead-draw rate has to show it either way.
        var cleanDeck = DraftRunner.DeckOf(player, "STRIKE_IRONCLAD", 8);
        var cursed = DraftRunner.DeckOf(player, "STRIKE_IRONCLAD", 4, "ASCENDERS_BANE", 4);
        var clean = DeckScorer.Score(cleanDeck, reference, config, 1);
        var drag = DeckScorer.Score(cursed, reference, config, 1);
        if (!(drag.DeadDrawRate > clean.DeadDrawRate))
            throw new Exception($"DECK-SIM scorer: curses must raise the dead-draw rate "
                + $"({drag.DeadDrawRate:F2} vs {clean.DeadDrawRate:F2}).");
        if (!(drag.DamagePerTurn < clean.DamagePerTurn))
            throw new Exception($"DECK-SIM scorer: a curse-crowded hand must deal less damage "
                + $"({drag.DamagePerTurn:F1} vs {clean.DamagePerTurn:F1}).");
        Console.WriteLine($"PASS: deck-sim scorer separates offence, defence and curse drag "
            + $"(strikes {attack.Offence:F0}/{guard.Defence:F0}, curses {drag.DamagePerTurn:F1} vs {clean.DamagePerTurn:F1} dmg/turn).");
    }

    /// <summary>
    /// The pipeline's one hard invariant. A report is only worth acting on if the
    /// same seed produces the same run, on this machine and the next; a drafting
    /// rule that reads a live RNG, a wall clock or a hash order would break it, and
    /// the break would otherwise show up as "the scores moved" with no cause.
    /// </summary>
    private static void VerifyDeterminism(SimConfig config)
    {
        var character = config.Characters[0];
        var first = DraftRunner.Run(character, config.Seed, config with { RunsPerCharacter = 1 });
        var second = DraftRunner.Run(character, config.Seed, config with { RunsPerCharacter = 1 });
        if (first.FinalDeck != second.FinalDeck || Math.Abs(first.Final.Power - second.Final.Power) > 0.0001
            || first.FinalSize != second.FinalSize)
            throw new Exception($"DECK-SIM is not deterministic for {character}/{config.Seed}:\n"
                + $"  {first.FinalSize} cards power={first.Final.Power:F2} {first.FinalDeck}\n"
                + $"  {second.FinalSize} cards power={second.Final.Power:F2} {second.FinalDeck}");

        // The A10 model has to actually reach the deck, or the sweep is measuring
        // an ascension it never applied. Ascender's Bane is Eternal, so at A10 it
        // must still be in the final deck of every run.
        if (config.Ascension10 && !first.FinalDeck.Contains("ASCENDERS_BANE", StringComparison.Ordinal))
            throw new Exception($"DECK-SIM did not keep Ascender's Bane in the A10 deck: {first.FinalDeck}");
        if (!config.Ascension10 && first.FinalDeck.Contains("ASCENDERS_BANE", StringComparison.Ordinal))
            throw new Exception($"DECK-SIM added Ascender's Bane to a non-ascension deck: {first.FinalDeck}");

        Console.WriteLine($"PASS: deck-sim is deterministic ({character}/{config.Seed}, "
            + $"{first.FinalSize} cards, power {first.Final.Power:F1}, route {first.Route}).");
    }
}
