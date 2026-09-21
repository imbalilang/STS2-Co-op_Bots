using CoopBots.Building;
using DeckSim;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Random;

// Validates the deck score against decks that actually played Ascension 10.
//
//   --deck-sim-validate                          the whole check
//   --deck-sim-validate --runs=5                 more bot seeds in the report
//   --deck-sim-validate --chars=IRONCLAD,SILENT  one or two characters
//   --deck-sim-validate --par                    also print par values to bake in
//
// Everything the pipeline measures on its own is self-referential: the bot drafts
// by BuildValue and the scorer scores what it drafted, so the two can agree with
// each other and both be wrong. These decks, and the community's own per-card
// score, are the outside opinion. Fetch them with
//   python scripts/fetch-spire-codex-decks.py --with-losing
//
// The load-bearing test is the labelled one: a winning A10 deck against an A10
// deck that died deep in act 3. Both are finished decks built by people trying to
// win, so the only difference the scorer can see is the one that decided the run.
// Comparing against this mod's own drafts is printed too, but only as a report —
// a real winner carries twenty relics and a human piloting it, and the scorer sees
// neither.
//
// The report prints first and the failures are raised at the end, so a
// miscalibration is visible with its numbers instead of only as a stack trace.
internal static class CommunityValidationScenarios
{
    internal static void Run(string[] args)
    {
        if (!args.Contains("--deck-sim-validate")) return;
        TestEnvironment.Ensure();

        if (!CommunityDecks.Available)
            throw new FileNotFoundException("Community data is not cached. Run: python scripts/fetch-spire-codex-decks.py --with-losing");

        var config = SimConfig.Default with
        {
            Characters = Characters(args),
            RunsPerCharacter = Runs(args),
            Players = Players(args),
        };
        var loaded = CommunityDecks.Load();
        var decks = loaded.Decks;
        var scores = CommunityDecks.LoadScores();
        // Party size is a scaling input, so the sample has to match the reference
        // being validated. Mixing solo decks into a four-player calibration — which
        // is what the first version did, silently — averages two different fights.
        var sample = decks
            .Where(deck => config.Characters.Contains(deck.Character, StringComparer.OrdinalIgnoreCase))
            .Where(deck => deck.Players == config.Players)
            .ToList();
        if (sample.Count == 0)
            throw new InvalidOperationException(
                $"No cached decks for {string.Join(", ", config.Characters)} at {config.Players} players. "
                + "Fetch them with: python scripts/fetch-spire-codex-decks.py --players " + config.Players);

        var wins = sample.Count(deck => deck.Win);
        var parties = decks.GroupBy(deck => deck.Players).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}p:{group.Count()}");
        Console.WriteLine();
        Console.WriteLine($"COMMUNITY VALIDATION — {sample.Count} cached A10 finished decks at {config.Players} players "
            + $"({wins} wins, {sample.Count - wins} losses), {scores.Count} community-scored cards");
        Console.WriteLine($"  cache holds: {string.Join(" ", parties)}");
        if (loaded.SkippedNoParty > 0)
            Console.WriteLine($"  WARNING: {loaded.SkippedNoParty} of {loaded.Files} cached runs were dropped for a missing "
                + "party size. Run: python scripts/fetch-spire-codex-decks.py --refresh-meta");
        var checks = new List<(string Name, bool Passed, string Detail)>();

        if (args.Contains("--features")) FeaturePower(config, sample, scores, args.Contains("--export"));
        if (args.Contains("--incremental"))
        {
            var csv = Arg(args, "--incremental");
            var path = string.IsNullOrEmpty(csv)
                ? Path.Combine(DraftSimHarness.RepoRoot(), "outputs", "decksim-analysis", $"deck-features-{config.Players}p.csv")
                : csv;
            Console.WriteLine();
            Console.WriteLine($"incremental AUC — does a feature add anything over 'upgrades'?  ({path})");
            Console.WriteLine("feature                     base    with   delta   [5%,95%] across repeats   n");
            foreach (var result in IncrementalAuc.Run(path, "upgrades"))
                Console.WriteLine($"{result.Feature,-26} {result.BaseAuc,6:F3} {result.WithAuc,6:F3} {result.Delta,+7:F3}   "
                    + $"[{result.Lo,+6:F3},{result.Hi,+6:F3}] {result.N,5}");
            Console.WriteLine("A delta whose interval straddles 0 is a feature the upgrade count already covers.");
        }
        if (args.Contains("--calibrate")) Calibrate(config, sample);
        if (args.Contains("--par")) ParReport(config, sample, checks);
        CalibrationCheck(config, sample, checks);
        Discrimination(config, sample, scores, checks);
        GradientControls(config, sample, checks);
        CardAgreement(config, scores, checks);
        BotComparison(config, sample);

        Console.WriteLine();
        var failed = checks.Where(check => !check.Passed).ToList();
        foreach (var check in checks)
            Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")}: {check.Name} — {check.Detail}");
        if (failed.Count > 0)
            throw new Exception($"COMMUNITY VALIDATION: {failed.Count} of {checks.Count} checks failed (see report above).");
        Console.WriteLine($"PASS: deck score agrees with community data on all {checks.Count} checks.");
    }

    /// <summary>
    /// Characters whose card facts this mod does not model, so no deck score of
    /// theirs means anything yet.
    ///
    /// Regent spends and generates stars, and neither <c>CardProfile</c> nor
    /// <c>GeniusCombatStrategy</c> reads a star cost or a star payout — only
    /// <c>TeamCombatPlanner</c> knows stars exist, and only to check legality. A
    /// star-costed card is therefore priced as though it were free and a
    /// star-generating card as though it did nothing, which is why the community
    /// sample shows a Regent starter scoring level with the decks that won.
    ///
    /// Kept in the report rather than filtered out of the sample: the number is the
    /// evidence for the gap, and a character quietly dropped from a validation is a
    /// character nobody fixes.
    /// </summary>
    private static readonly HashSet<string> Unmodelled = new(StringComparer.OrdinalIgnoreCase) { "REGENT" };

    private static bool Covered(string character) => !Unmodelled.Contains(character);

    private static string[] Characters(string[] args)
    {
        var value = Arg(args, "--chars");
        return value is null
            ? ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int Runs(string[] args) => int.TryParse(Arg(args, "--runs"), out var runs) ? Math.Max(1, runs) : 3;

    private static int Players(string[] args) => int.TryParse(Arg(args, "--players"), out var players) ? Math.Max(1, players) : 4;

    private static string? Arg(string[] args, string name)
    {
        var prefix = name + "=";
        return args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    // ---- Feature power ------------------------------------------------------

    /// <summary>
    /// Which of the things the score measures actually separate A10 winners from
    /// A10 decks that died deep?
    ///
    /// This is the step that decides what is worth weighting, and it exists because
    /// the alternative is guessing. Every weight in <see cref="DeckScorer"/> was
    /// chosen by hand; the community sample is the first chance to ask which of the
    /// quantities behind them carry any signal at all. A feature that reads 0.50 on
    /// every character is decoration, however plausible it sounds, and one that
    /// reads 0.60 on all five is the one the score should be built around.
    ///
    /// Read the numbers with the control in mind: a card list explains little, so
    /// even the best feature here is weak. The question is only which are real.
    /// </summary>
    /// <summary>The feature table as markdown rows, for the receipt.</summary>
    internal static List<string> Statistics { get; private set; } = [];

    private static void FeaturePower(SimConfig config, List<CommunityDeck> decks,
        IReadOnlyDictionary<string, CommunityCardScore> community, bool export)
    {
        Console.WriteLine();
        Console.WriteLine("feature power — AUC against A10 win/loss, per character (0.50 = no signal)");
        // The structural features need the built card list and a player to read the
        // deck as, and neither fits in the (score, deck) shape the table uses.
        var structural = new Dictionary<string, (int Routes, int RoutesNoVariant, int Complete, int Stranded,
            int Involved, double PackageDead, double VariantShare, double Convergence, double Dead,
            int ShivComplete, int ShivStranded, int ShivProducers)>(StringComparer.Ordinal);
        var features = new (string Name, Func<DeckScore, CommunityDeck, double> Value)[]
        {
            ("score (power)", (s, _) => s.Power),
            ("offence", (s, _) => s.Offence),
            ("defence", (s, _) => s.Defence),
            ("consistency", (s, _) => s.Consistency),
            ("damage/turn", (s, _) => s.DamagePerTurn),
            ("block/turn", (s, _) => s.BlockPerTurn),
            ("survival", (s, _) => s.SurvivalRate),
            ("kill rate", (s, _) => s.KillRate),
            ("deck size", (s, _) => s.Size),
            ("upgrades", (s, _) => s.Upgrades),
            ("curses", (s, _) => s.Curses),
            ("dead draw", (s, _) => s.DeadDrawRate),
            ("energy waste", (s, _) => s.EnergyWaste),
            ("ramp", (s, _) => s.RampRatio),
            ("block/damage", (s, _) => s.BlockPerTurn / Math.Max(1, s.DamagePerTurn)),
            // ---- the process features ----
            // An end-state deck can be finished and still never have had a plan; a
            // deck with a plan can have found it on floor two or on floor forty.
            // Everything above measures the deck as a static object, which is why
            // none of it can speak to a change in the function that builds it.
            ("packages (routes)", (_, d) => structural[d.RunHash].Routes),
            ("packages >=2", (_, d) => structural[d.RunHash].Routes >= 2 ? 1 : 0),
            ("burst (peak turn)", (s, _) => s.PeakDamage),
            ("burst (turn no.)", (s, _) => -s.PeakTurn),
            ("convergence (pick no.)", (_, d) => structural[d.RunHash].Convergence),
            ("dead cards", (_, d) => structural[d.RunHash].Dead),
            // ---- package completeness, four separate numbers ----
            ("complete (packages)", (_, d) => structural[d.RunHash].Complete),
            ("stranded (packages)", (_, d) => structural[d.RunHash].Stranded),
            ("fragmented", (_, d) => structural[d.RunHash].Stranded),
            ("involved (packages)", (_, d) => structural[d.RunHash].Involved),
            ("dead by package", (_, d) => structural[d.RunHash].PackageDead),
            ("shiv complete", (_, d) => structural[d.RunHash].ShivComplete),
            ("shiv stranded", (_, d) => structural[d.RunHash].ShivStranded),
            ("shiv producers", (_, d) => structural[d.RunHash].ShivProducers),
            ("has variant card", (_, d) => structural[d.RunHash].VariantShare),
            ("packages (no variant)", (_, d) => structural[d.RunHash].RoutesNoVariant),
            ("community sum (control)", (_, d) => d.Deck.Sum(e => community.TryGetValue(e.Id, out var c) ? c.Score : 0)),
        };

        // One scored row per deck, tagged with the label, so each feature is just a
        // projection over the same sample.
        var sample = new List<(DeckScore Score, CommunityDeck Deck, bool Win)>();
        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = config.ReferenceFor(character, 2);
            foreach (var deck in decks.Where(d => string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase)))
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                var routes = Archetypes.Detect(cards, player);
                var packages = Packages(cards);
                structural[deck.RunHash] = (
                    routes.Count,
                    Archetypes.Detect(WithoutVariants(cards), player).Count,
                    packages.Complete, packages.Stranded, packages.Involved, packages.DeadShare,
                    cards.Any(card => RolesOf(card).Contains("variant-type")) ? 1 : 0,
                    Convergence(deck, player),
                    DeadShare(cards, routes),
                    packages.ShivComplete, packages.ShivStranded, packages.ShivProducers);
                sample.Add((DeckScorer.Score(cards, reference, config, 7), deck, deck.Win));
            }
        }

        var header = string.Join(" ", config.Characters.Select(c => $"{c[..Math.Min(6, c.Length)],6}"));
        Console.WriteLine($"{"feature",-26} {header}   mean  [95% CI]     p    n(win/loss)");
        var statistics = new List<string>();
        foreach (var (name, value) in features)
        {
            var row = new List<double>();
            var winMedians = new List<double>();
            var lossMedians = new List<double>();
            var pooledWins = new List<double>();
            var pooledLosses = new List<double>();
            var winCount = 0;
            var lossCount = 0;
            var seed = 1UL;
            foreach (var character in config.Characters)
            {
                var rows = sample.Where(row => string.Equals(row.Deck.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();
                var wins = rows.Where(row => row.Win).Select(row => value(row.Score, row.Deck)).ToList();
                var losses = rows.Where(row => !row.Win).Select(row => value(row.Score, row.Deck)).ToList();
                if (wins.Count < 5 || losses.Count < 5) continue;
                row.Add(Auc(wins, losses));
                winMedians.Add(Median(wins));
                lossMedians.Add(Median(losses));
                pooledWins.AddRange(wins);
                pooledLosses.AddRange(losses);
                winCount += wins.Count;
                lossCount += losses.Count;
            }
            // The medians are here because an AUC of 0.50 has two very different
            // causes: a feature that genuinely does not separate the groups, and a
            // feature that is the same number for everybody. The medians tell them
            // apart at a glance, and only one of the two is about the decks.
            var medians = winMedians.Count == 0 ? "" : $"  med {Median(winMedians),6:F1}/{Median(lossMedians),6:F1}";
            var ci = BootstrapCi(pooledWins, pooledLosses, seed, rounds: 300);
            var p = PermutationP(pooledWins, pooledLosses, seed, rounds: 600);
            var consistent = row.Count(direction => direction > 0.5);
            Console.WriteLine($"{name,-26} " + string.Join(" ", row.Select(v => $"{v,6:F2}"))
                + $"   {(row.Count == 0 ? 0 : row.Average()),5:F2}  [{ci.Lo:F2},{ci.Hi:F2}] {p,5:F3}  {winCount,4}/{lossCount,-4}{medians}");
            statistics.Add($"| `{name}` | {row.Count switch { 0 => "—", _ => row.Average().ToString("F2") }} "
                + $"| {ci.Lo:F2}–{ci.Hi:F2} | {p:F3} | {winCount}/{lossCount} | {consistent}/{row.Count} |");
        }
        Console.WriteLine($"n per character is roughly {sample.Count / Math.Max(1, config.Characters.Length)}, so the standard"
            + " error of an AUC here is about 0.05: 0.58 and 0.61 are the same number, and only p carries a conclusion.");
        Console.WriteLine("Equal win/loss medians mean the feature is the same number for everyone — that is a"
            + " statement about the encoding, not about the decks.");
        Statistics = statistics;
        Stratify(config, sample, features, structural);
        if (export) ExportDeckValues(config, sample, features, structural);
    }

    // ---- Exports ------------------------------------------------------------

    /// <summary>
    /// One row per deck: every feature's value plus the label.
    ///
    /// This is what lets the numbers be checked without re-running the tool. A
    /// point estimate in a report can only be taken on trust; the values behind it
    /// can be bootstrapped, paired against another feature, or recomputed by
    /// someone who does not believe the implementation.
    /// </summary>
    private static void ExportDeckValues(SimConfig config, List<(DeckScore Score, CommunityDeck Deck, bool Win)> sample,
        (string Name, Func<DeckScore, CommunityDeck, double> Value)[] features,
        Dictionary<string, (int Routes, int RoutesNoVariant, int Complete, int Stranded,
            int Involved, double PackageDead, double VariantShare, double Convergence, double Dead,
            int ShivComplete, int ShivStranded, int ShivProducers)> structural)
    {
        var directory = Path.Combine(DraftSimHarness.RepoRoot(), "outputs", "decksim-analysis");
        Directory.CreateDirectory(directory);
        // Only the identity columns are written here; deck size, curses and upgrades
        // are already in the feature list, and emitting them twice made the header
        // ambiguous for anything reading the file by name.
        var header = "run_hash,character,players,win,"
            + string.Join(",", features.Select(feature => Slug(feature.Name)));
        var rows = new List<string> { header };
        foreach (var (score, deck, win) in sample)
        {
            var values = features.Select(feature =>
            {
                var value = feature.Value(score, deck);
                // Infinity survives a round trip as the literal "inf" so a reader
                // can tell "never converged" from "converged at a huge pick".
                return double.IsInfinity(value) ? "inf" : value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            });
            rows.Add($"{deck.RunHash},{deck.Character},{deck.Players},{(win ? 1 : 0)},"
                + string.Join(",", values));
        }
        var path = Path.Combine(directory, $"deck-features-{config.Players}p.csv");
        File.WriteAllLines(path, rows);
        Console.WriteLine($"exported {rows.Count - 1} deck rows -> {path}");

        ExportRoleTable(config, Path.Combine(directory, "role-table-model.csv"));
    }

    /// <summary>
    /// Every card's cost, type, rarity and roles, in the tester's column order, so
    /// the two derivations can be diffed mechanically instead of argued about.
    /// </summary>
    private static void ExportRoleTable(SimConfig config, string path)
    {
        var player = DraftRunner.NewPlayer(config.Characters[0], 1);
        var rows = new List<string> { "id,cost,type,rarity,roles" };
        foreach (var canonical in ModelDb.AllCards.OrderBy(card => card.Id.Entry, StringComparer.Ordinal))
        {
            try
            {
                var card = player.RunState.CreateCard(canonical, player);
                var cost = card.EnergyCost.CostsX
                    ? 3
                    : Math.Max(0, card.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.All));
                var roles = RolesOf(card).OrderBy(role => role, StringComparer.Ordinal);
                rows.Add($"{canonical.Id.Entry},{cost},{card.Type},{card.Rarity},{string.Join("|", roles)}");
            }
            catch (Exception error)
            {
                rows.Add($"{canonical.Id.Entry},?,,,<unreadable:{error.GetType().Name}>");
            }
        }
        File.WriteAllLines(path, rows);
        Console.WriteLine($"exported {rows.Count - 1} cards -> {path}");
    }

    /// <summary>A CSV-safe column name: letters, digits and underscores only.</summary>
    private static string Slug(string name)
        => new(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());


    /// <summary>
    /// The three package features, split by whether the deck holds a card whose
    /// type is decided at runtime.
    ///
    /// PROTOCOL (lead ruling, 派单14 §4): any AUC for fragmented / dead cards /
    /// complete must carry this split. A pooled number for one of these can be the
    /// average of a real signal and no signal at all — a reviewer found fragmented
    /// reading 0.57 without such a card and 0.49 with it, against a pooled 0.54
    /// that showed neither.
    /// </summary>
    private static void Stratify(SimConfig config, List<(DeckScore Score, CommunityDeck Deck, bool Win)> sample,
        (string Name, Func<DeckScore, CommunityDeck, double> Value)[] features,
        Dictionary<string, (int Routes, int RoutesNoVariant, int Complete, int Stranded,
            int Involved, double PackageDead, double VariantShare, double Convergence, double Dead,
            int ShivComplete, int ShivStranded, int ShivProducers)> structural)
    {
        var watched = new[] { "fragmented", "dead cards", "dead by package", "complete (packages)" };
        Console.WriteLine();
        Console.WriteLine("stratified by variant-type card (MAD_SCIENCE, type set at runtime by TinkerTime)");
        Console.WriteLine("feature                     no-variant (n)      with-variant (n)");
        foreach (var (name, value) in features.Where(feature => watched.Contains(feature.Name)))
        {
            var withoutWins = new List<double>();
            var withoutLosses = new List<double>();
            var withWins = new List<double>();
            var withLosses = new List<double>();
            foreach (var row in sample)
            {
                var isVariant = structural[row.Deck.RunHash].VariantShare > 0.5;
                var v = value(row.Score, row.Deck);
                if (isVariant) (row.Win ? withWins : withLosses).Add(v);
                else (row.Win ? withoutWins : withoutLosses).Add(v);
            }
            Console.WriteLine($"{name,-26} {Describe(withoutWins, withoutLosses),-18} {Describe(withWins, withLosses),-18}");
        }
        Console.WriteLine("A split that straddles 0.5 either side means the pooled number is an average of"
            + " signal and no-signal, not a weak effect.");

        // Character-exclusive resources are read on that character's subset: for
        // every other character they are zero by definition, and those ties drag the
        // pooled AUC back to 0.50. The all-character value is printed beside it so
        // the degeneracy stays visible.
        Console.WriteLine();
        Console.WriteLine("character-exclusive packages — shiv, on the Silent subset (ruling 派单14 §3)");
        var silent = sample.Where(row => string.Equals(row.Deck.Character, "SILENT", StringComparison.OrdinalIgnoreCase)).ToList();
        var silentWins = silent.Where(row => row.Win).Select(row => (double)structural[row.Deck.RunHash].ShivComplete).ToList();
        var silentLosses = silent.Where(row => !row.Win).Select(row => (double)structural[row.Deck.RunHash].ShivComplete).ToList();
        if (silentWins.Count >= 5 && silentLosses.Count >= 5)
            Console.WriteLine($"  shiv complete   Silent only: AUC {Auc(silentWins, silentLosses):F2} "
                + $"(n={silent.Count}: {silentWins.Count} win / {silentLosses.Count} loss, "
                + $"{silentWins.Count(v => v > 0.5)} winners hold the package)");
        else
            Console.WriteLine($"  shiv complete   Silent subset too small (n={silent.Count}) to read");
    }

    private static string Describe(List<double> wins, List<double> losses)
    {
        var n = wins.Count + losses.Count;
        return wins.Count < 5 || losses.Count < 5
            ? $"(n={n}, too small)"
            : $"AUC {Auc(wins, losses):F2} (n={n})";
    }

    // ---- Package completeness -----------------------------------------------

    /// <summary>
    /// What one deck's packages look like, per the BakedResources producer /
    /// multiplier / replayer / payoff lists.
    ///
    /// Deliberately four separate numbers and not one score. "The deck has a plan"
    /// and "the deck is three half-plans" are different failures with different
    /// fixes, and a single count that adds them together cannot tell them apart —
    /// which is exactly what the old route count did.
    /// </summary>
    internal sealed record PackageState(
        int Complete, int Stranded, int Involved, double DeadShare, string Brief,
        int ShivComplete, int ShivStranded, int ShivProducers, int ShivMultipliers, int ShivReplayers);

    /// <summary>The threshold the spec sets for a package to count as complete.</summary>
    private const int ProducerFloor = 3;

    private static PackageState Packages(IReadOnlyList<CardModel> cards)
    {
        var ids = new HashSet<string>(cards.Select(card => card.Id.Entry), StringComparer.Ordinal);
        int complete = 0, stranded = 0, involved = 0;
        var serving = new HashSet<string>(StringComparer.Ordinal);
        var brief = new List<string>();
        foreach (var resource in BakedResources.All)
        {
            var produced = resource.Producers.Count(ids.Contains);
            var multiplied = resource.Multipliers.Count(ids.Contains);
            var replayed = resource.Replayers.Count(ids.Contains);
            var paid = resource.Spends.Count(ids.Contains);
            // SPEC (lead ruling, 派单14 §2): `complete` is only defined for a resource
            // that has both a multiplier and a replayer recorded. A resource missing
            // either does NOT participate in `complete` at all.
            //
            // The first version treated an empty list as "nothing required" and let
            // the conjunction collapse to `producers >= 3` for seven of the eight
            // resources. That measures breadth — how many resources the deck has
            // spread into — and breadth correlates with losing, which is why the
            // feature came out inverted at 0.47. A package is a producer AND a
            // multiplier AND a replayer; a resource with only producers recorded
            // cannot express that, so it must abstain rather than answer a different
            // question. cardbuild is filling in the missing M/R (派单13); until then
            // only shiv is counted here.
            var packageDefined = resource.Multipliers.Length >= 1 && resource.Replayers.Length >= 1;
            var complete_ = packageDefined && produced >= ProducerFloor
                && multiplied >= 1 && replayed >= 1;
            var stock = produced >= 1;
            var cash = multiplied >= 1 || replayed >= 1 || paid >= 1;
            // Half-built: one side present, the other absent. Stock with nothing to
            // cash it in, or a payoff with nothing to cash. Both were invisible in
            // the old count, which only asked how many routes had fired.
            var isStranded = !complete_ && (stock ^ cash);
            if (complete_) complete++;
            if (isStranded) stranded++;
            if (stock || cash)
            {
                involved++;
                foreach (var id in resource.Producers.Concat(resource.Multipliers)
                    .Concat(resource.Replayers).Concat(resource.Spends)) serving.Add(id);
                if (isStranded || complete_)
                    brief.Add($"{(complete_ ? "+" : "~")}{resource.Name}({produced}p/{multiplied}m/{replayed}r)");
            }
        }
        // Dead is redefined on the package the deck actually started, not on the
        // routes it happens to read as: a card is dead if nothing the deck is
        // building has a use for it.
        var dead = cards.Count(card => !serving.Contains(card.Id.Entry));
        // Shiv is reported on its own because it is the only package with a
        // recorded multiplier and replayer: for the other seven the completeness
        // conjunction collapses to a producer count, so the aggregate is a breadth
        // measure while this one is an actual package check.
        var shiv = BakedResources.All.First(resource => resource.Name == "shiv");
        var shivProducers = shiv.Producers.Count(ids.Contains);
        var shivMultipliers = shiv.Multipliers.Count(ids.Contains);
        var shivReplayers = shiv.Replayers.Count(ids.Contains);
        return new PackageState(complete, stranded, involved,
            cards.Count == 0 ? 0 : dead / (double)cards.Count, string.Join(" ", brief),
            shivProducers >= ProducerFloor && shivMultipliers >= 1 && shivReplayers >= 1 ? 1 : 0,
            (shivProducers >= 1) ^ (shivMultipliers >= 1 || shivReplayers >= 1) ? 1 : 0,
            shivProducers, shivMultipliers, shivReplayers);
    }

    /// <summary>
    /// Cards whose roles are a forced union because their type is decided at
    /// runtime. Excluded from route counting when <paramref name="exclude"/> is set,
    /// because their union lets one card satisfy several route roles at once.
    /// </summary>
    private static IReadOnlyList<CardModel> WithoutVariants(IReadOnlyList<CardModel> cards)
        => cards.Where(card => !RolesOf(card).Contains("variant-type")).ToList();

    // ---- Structural features ------------------------------------------------

    /// <summary>
    /// The pick number on which the deck first reads as having a plan, or infinity
    /// if it never did.
    ///
    /// Replayed from the run's own card gains, which is the only place a real run
    /// records its order at all. Two things it cannot see: removals and upgrades,
    /// so the deck being tested is the deck as it was *added to*, not as it stood;
    /// and the fact that a real player was steering, so "the plan appeared at pick
    /// 14" is the deck's doing and the pilot's together. Both make this a loud
    /// measure rather than a precise one, which is the point — the question it
    /// answers is whether a plan appears early or late or never.
    /// </summary>
    private static double Convergence(CommunityDeck deck, Player fixture)
    {
        if (deck.Picks.Count == 0) return double.PositiveInfinity;
        var built = fixture.Character.StartingDeck
            .Select(card => fixture.RunState.CreateCard(card, fixture)).ToList();
        for (var index = 0; index < deck.Picks.Count; index++)
        {
            var canonical = ModelDb.AllCards.FirstOrDefault(card =>
                string.Equals(card.Id.Entry, deck.Picks[index], StringComparison.OrdinalIgnoreCase));
            if (canonical is null) continue;
            try { built.Add(fixture.RunState.CreateCard(canonical, fixture)); }
            catch { continue; }
            if (Archetypes.Detect(built, fixture).Count > 0) return index + 1;
        }
        return double.PositiveInfinity;
    }

    /// <summary>
    /// Share of the finished deck that serves none of the routes it reads as.
    ///
    /// A deck with no route at all scores 1 by definition, which is worth knowing
    /// rather than hiding: it says the route detector had nothing to say about that
    /// deck, not that every card in it is bad.
    /// </summary>
    private static double DeadShare(IReadOnlyList<CardModel> cards, IReadOnlyList<Archetypes.Match> routes)
    {
        if (cards.Count == 0) return 0;
        if (routes.Count == 0) return 1;
        var serving = cards.Count(card =>
        {
            var roles = RolesOf(card);
            return routes.Any(route => route.Signature.Contains(card.Id.Entry)
                || route.Roles.Any(roles.Contains));
        });
        return 1 - serving / (double)cards.Count;
    }

    private static IReadOnlySet<string> RolesOf(CardModel card)
    {
        try { return CardProfile.Of(card).Roles; }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    // ---- Calibration --------------------------------------------------------

    /// <summary>
    /// The reference fight has to be one that real winners get through.
    ///
    /// This is the check the community data was worth fetching for. The first
    /// reference used the act-3 boss's biggest turn (32 a turn) and every real
    /// winning deck died in it; a fight nobody who actually won survives is not the
    /// fight they won. It is also self-validating in a way the win/loss comparison
    /// is not: it asks a question about the model, not about the decks.
    /// </summary>
    private static void CalibrationCheck(SimConfig config, List<CommunityDeck> decks, List<(string, bool, string)> checks)
    {
        Console.WriteLine();
        Console.WriteLine("calibration — real A10 winners must survive the reference fight");
        Console.WriteLine("character    n   survival  kill rate  dmg/turn  blk/turn");

        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = config.ReferenceFor(character, 2);
            var scores = new List<DeckScore>();
            foreach (var deck in decks.Where(d => d.Win && string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase)))
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                scores.Add(DeckScorer.Score(cards, reference, config, 7));
            }
            if (scores.Count < 5) continue;
            var mean = Mean(scores);
            Console.WriteLine($"{character,-11} {scores.Count,4} {mean.SurvivalRate,10:P0} {mean.KillRate,10:P0}"
                + $" {mean.DamagePerTurn,9:F1} {mean.BlockPerTurn,9:F1}");
            if (Covered(character))
                checks.Add(($"{character}: real A10 winners survive the reference fight", mean.SurvivalRate >= 0.6,
                    $"{mean.SurvivalRate:P0} of {scores.Count} winners survive"));
            else
                Console.WriteLine($"              ^ {character} is reported but not checked: "
                    + "this mod does not model its card mechanic (see Unmodelled).");
        }
    }

    /// <summary>
    /// Sweeps the reference fight's damage per turn and reports what real A10
    /// winners do in it.
    ///
    /// This is the measurement that fixes the reference rather than the decks. A
    /// fight a real winner cannot survive is not the fight a real winner won: the
    /// first attempt at this used the boss's biggest turn (32 a turn for act 3) and
    /// every real winning deck died on turn four, which made the score a measure of
    /// how fast a deck dies rather than how well it was built. The right number is
    /// the one where decks that actually won actually get through the fight —
    /// bosses also spend turns buffing, blocking and debuffing, which the average
    /// has to account for.
    /// </summary>
    private static void Calibrate(SimConfig config, List<CommunityDeck> decks)
    {
        Console.WriteLine();
        Console.WriteLine("calibration — real A10 winners against a swept act-3 incoming, per turn");
        Console.WriteLine("character   incoming  survival  damage/turn  block/turn  kill");
        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var prepared = new List<List<CardModel>>();
            foreach (var deck in decks.Where(d => d.Win && string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase)))
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                prepared.Add(cards);
            }
            if (prepared.Count == 0) continue;

            foreach (var incoming in new[] { 8.0, 10, 12, 14, 16, 18, 20, 22 })
            {
                var reference = config.ReferenceFor(character, 2) with { IncomingPerTurn = incoming };
                var scores = prepared.Select(cards => DeckScorer.Score(cards, reference, config, 7)).ToList();
                var mean = Mean(scores);
                Console.WriteLine($"{character,-11} {incoming,10:F0} {mean.SurvivalRate,9:P0} {mean.DamagePerTurn,12:F1} {mean.BlockPerTurn,11:F1} {mean.KillRate,5:P0}");
            }
        }
    }

    // ---- Par measurement ----------------------------------------------------

    /// <summary>
    /// Prints the par values to bake into <see cref="SimConfig.DefaultPar"/>.
    ///
    /// Par has to be measured by this scorer and not by a human, because it is what
    /// this scorer's own play-out extracts from a winning deck. It is a
    /// self-consistency step, not a discovery: it says "a deck like this, played
    /// like this, produced this much", and the score afterwards is relative to it.
    /// </summary>
    private static void ParReport(SimConfig config, List<CommunityDeck> decks, List<(string, bool, string)> checks)
    {
        Console.WriteLine();
        Console.WriteLine("par measurement — what real A10 winners produce under this scorer");
        Console.WriteLine("Par is the MEDIAN, not the mean. The score peaks at par and falls away above it, so a");
        Console.WriteLine("mean par sits below the top half of the winning population and caps it. Using the mean");
        Console.WriteLine("here made every winner look under-powered and inverted the winner/loser ranking.");
        Console.WriteLine("character    n  dmg mean  dmg median  blk mean  blk median  survival   suggested constant");
        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = config.ReferenceFor(character, 2);
            var scores = new List<DeckScore>();
            foreach (var deck in decks.Where(d => d.Win && string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase)))
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                scores.Add(DeckScorer.Score(cards, reference, config, 7));
            }
            if (scores.Count == 0) continue;
            var mean = Mean(scores);
            var medianDamage = Median(scores.Select(s => s.DamagePerTurn).ToList());
            var medianBlock = Median(scores.Select(s => s.BlockPerTurn).ToList());
            var medianUpgrades = Median(scores.Select(s => (double)s.Upgrades).ToList());
            var medianEfficiency = Median(scores.Select(s => 1 - s.EnergyWaste).ToList());
            // The par the score is actually using, next to the one just measured
            // from winners of this party size. The score's par is the solo figure
            // scaled by the game's own multiplayer factor; where the two agree, the
            // scaling is confirmed by the data and not only by the decompile.
            var predicted = reference.ParDamagePerTurn;
            Console.WriteLine($"{character,-11} {scores.Count,4} {mean.DamagePerTurn,8:F1} {medianDamage,11:F1}"
                + $" {mean.BlockPerTurn,9:F1} {medianBlock,11:F1} {mean.SurvivalRate,9:P0}"
                + $"   [\"{character}\"] = new(Damage: {medianDamage:F1}, Block: {medianBlock:F1},"
                + $" Upgrades: {medianUpgrades:F0}, Efficiency: {medianEfficiency:F2}),");
            // The winner count decides whether this row is a measurement or a
            // rumour. Defect's median moved from 19.6 to 28.5 when its sample grew
            // from 3 winners to 11, which is why the derived par stays the default
            // until a character has enough winners to bake a stable one — see
            // ParSampleFloor.
            var confidence = scores.Count >= ParSampleFloor ? "measured" : "THIN SAMPLE";
            Console.WriteLine($"{"",-11}      efficiency {medianEfficiency:F3}, upgrades {medianUpgrades:F0}, "
                + $"scaled-solo par {predicted:F1} vs measured {medianDamage:F1} ({confidence}, {scores.Count} winners)");
            if (!Covered(character)) continue;
            if (scores.Count >= 15)
                checks.Add(($"{character}: scaling solo par to {config.Players} players predicts the measured par",
                    Math.Abs(predicted - medianDamage) <= Math.Max(3, predicted * 0.2),
                    $"scaled {predicted:F1} vs measured {medianDamage:F1} over {scores.Count} winners"));
        }
    }

    /// <summary>
    /// Winners a character needs before its measured par is worth baking.
    ///
    /// Not a statistical threshold, a stability one: below about this many, adding
    /// a handful of runs moves the median by tens of percent, and a par baked from
    /// that would be a number nobody could reproduce. Until a character clears it,
    /// the score keeps deriving par from the verified HP scaling and the gap is
    /// printed rather than baked.
    /// </summary>
    private const int ParSampleFloor = 30;

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    // ---- The labelled test --------------------------------------------------

    /// <summary>
    /// Does the score separate decks that won from decks that lost?
    ///
    /// Both sets reached the end of act 3, so both are finished decks; the losers
    /// did everything a draft can do except survive the last fight. A scorer that
    /// cannot tell these apart is not measuring anything a run is decided by, and
    /// no amount of agreement with the bot's own drafts would make it useful.
    /// </summary>
    private static void Discrimination(SimConfig config, List<CommunityDeck> decks,
        IReadOnlyDictionary<string, CommunityCardScore> community, List<(string, bool, string)> checks)
    {
        var ourAucs = new List<double>();
        var controlAucs = new List<double>();
        int totalWinners = 0, totalLosers = 0;
        Console.WriteLine();
        Console.WriteLine("discrimination — do A10 winners score above A10 decks that died deep?");
        Console.WriteLine("This comparison carries its own control. The right-hand AUC ranks the same decks by");
        Console.WriteLine("summing the community's own per-card score, so it says how much a deck list can");
        Console.WriteLine("explain at all: relics, potions and the pilot are not in either scorer.");
        Console.WriteLine("character   winners (n)   losers (n)      gap    AUC  community-AUC | win: size upg dmg blk surv | loss: size upg dmg blk surv");

        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = config.ReferenceFor(character, 2);
            var winners = new List<DeckScore>();
            var losers = new List<DeckScore>();
            var winnerCommunity = new List<double>();
            var loserCommunity = new List<double>();

            foreach (var deck in decks.Where(d => string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase)))
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                var score = DeckScorer.Score(cards, reference, config, 7);
                var byCommunity = deck.Deck.Sum(entry => community.TryGetValue(entry.Id, out var found) ? found.Score : 0);
                if (deck.Win) { winners.Add(score); winnerCommunity.Add(byCommunity); }
                else { losers.Add(score); loserCommunity.Add(byCommunity); }
            }
            if (winners.Count < 5 || losers.Count < 5) continue;

            var win = Mean(winners);
            var loss = Mean(losers);
            var auc = Auc(winners.Select(s => s.Power).ToList(), losers.Select(s => s.Power).ToList());
            var communityAuc = Auc(winnerCommunity, loserCommunity);
            ourAucs.Add(auc);
            controlAucs.Add(communityAuc);
            totalWinners += winners.Count;
            totalLosers += losers.Count;
            // Reported per character rather than asserted: the community control on
            // the same rows is what settles it. Ranking these decks by summing the
            // community's own per-card score gets AUC 0.40-0.69, which is to say a
            // card list barely predicts who beat the act-3 boss. A test whose
            // ceiling is a coin flip cannot be the test a deck-list scorer is held
            // to, and tuning this scorer until it passed would be fitting it to
            // relic luck.
            //
            // The pooled mean IS asserted, in one direction only: below 0.50 the
            // score is worse than guessing and would be ranking decks backwards.
            // That is the failure worth failing on, and it is cheap to check.
            Console.WriteLine($"{character,-11} {win.Power,7:F1} ({winners.Count,2})"
                + $" {loss.Power,7:F1} ({losers.Count,2}) {win.Power - loss.Power,+8:F1} {auc,6:F2} {communityAuc,13:F2} |"
                + $" {win.Size,4} {win.Upgrades,3} {win.DamagePerTurn,3:F0} {win.BlockPerTurn,3:F0} {win.SurvivalRate,5:P0}"
                + $" ({win.Offence,3:F0}/{win.Defence,3:F0}/{win.Consistency,3:F0}) |"
                + $" {loss.Size,5} {loss.Upgrades,3} {loss.DamagePerTurn,3:F0} {loss.BlockPerTurn,3:F0} {loss.SurvivalRate,5:P0}"
                + $" ({loss.Offence,3:F0}/{loss.Defence,3:F0}/{loss.Consistency,3:F0})");
        }

        if (ourAucs.Count == 0) return;
        var mean = ourAucs.Average();
        var control = controlAucs.Average();
        // Two standard errors, not a flat 0.50. At these sample sizes the standard
        // error of an AUC is around 0.10, so a fixed bar makes the check a coin
        // flip: it read 0.50 on a mixed sample and 0.44 on the same data filtered
        // to solo runs, neither of which is distinguishable from chance. What this
        // is here to catch is a score that ranks decks *backwards* — the earlier
        // 0.23 on a character whose mechanic the card model did not cover — and
        // that is a large, significant gap, not a wiggle.
        // Pooled over every deck in the sample, not averaged per character: one
        // character with a single losing deck has a standard error near 0.5 on its
        // own, and averaging those in produced a tolerance so wide the check could
        // not fail.
        var slop = 2 * AucStandardError(mean, totalWinners, totalLosers);
        Console.WriteLine();
        Console.WriteLine($"  MEAN AUC  score {mean:F2} ± {slop:F2}   community control {control:F2}   "
            + $"(a card list cannot explain more than the control; only a gap this wide means ranking backwards)");
        checks.Add(("the score is not significantly worse than guessing at who won", mean + slop >= 0.50,
            $"mean AUC {mean:F2}, two standard errors {slop:F2}, control {control:F2}"));
    }

    /// <summary>
    /// Rough standard error of an AUC, from the counts alone. The usual
    /// approximation is enough here: this decides whether a gap is worth failing
    /// on, not whether a result is significant.
    /// </summary>
    private static double AucStandardError(double auc, int winners, int losers)
        => winners <= 0 || losers <= 0 ? 0 : Math.Sqrt(auc * (1 - auc) * (1.0 / winners + 1.0 / losers));

    private static string Shorten(string? killedBy)
    {
        if (string.IsNullOrEmpty(killedBy)) return string.Empty;
        var name = killedBy.Split('.').Last();
        return name is "NONE" or "" ? string.Empty : name;
    }

    /// <summary>
    /// One-sided permutation p: how often shuffling the win/loss labels produces an
    /// AUC at least as high as the one observed.
    ///
    /// A point estimate cannot carry a conclusion at this sample size. Each
    /// character holds roughly 45 winners and 45 losers, where the standard error
    /// of an AUC is about 0.05 — so 0.58 and 0.61 are the same number, and the
    /// earlier threshold of "beat 0.61" was asking a coin to beat a coin. The
    /// permutation asks the only question the sample can answer: is this ordering
    /// better than a random one?
    /// </summary>
    private static double PermutationP(List<double> winners, List<double> losers, ulong seed, int rounds = 1000)
    {
        if (winners.Count < 5 || losers.Count < 5) return 1;
        var observed = Auc(winners, losers);
        var pool = winners.Concat(losers).ToArray();
        var labels = pool.Length;
        var rng = new Rng(seed, "coopbots-permutation");
        var shuffled = new double[labels];
        var hits = 0;
        for (var round = 0; round < rounds; round++)
        {
            Array.Copy(pool, shuffled, labels);
            // Fisher-Yates on the pooled values, then split at the original sizes:
            // shuffling labels is the same thing and cheaper than tracking them.
            for (var index = labels - 1; index > 0; index--)
            {
                var swap = rng.NextInt(index + 1);
                (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
            }
            var drawn = new List<double>(winners.Count);
            for (var index = 0; index < winners.Count; index++) drawn.Add(shuffled[index]);
            var rest = new List<double>(losers.Count);
            for (var index = winners.Count; index < labels; index++) rest.Add(shuffled[index]);
            if (Auc(drawn, rest) >= observed) hits++;
        }
        return (hits + 1.0) / (rounds + 1);
    }

    /// <summary>Percentile bootstrap over decks, so the interval reflects the sample that exists.</summary>
    private static (double Lo, double Hi) BootstrapCi(List<double> winners, List<double> losers, ulong seed, int rounds = 500)
    {
        if (winners.Count < 5 || losers.Count < 5) return (0, 1);
        var rng = new Rng(seed, "coopbots-bootstrap");
        var draws = new List<double>(rounds);
        for (var round = 0; round < rounds; round++)
            draws.Add(Auc(Resample(winners, rng), Resample(losers, rng)));
        draws.Sort();
        return (draws[(int)(rounds * 0.025)], draws[Math.Min(rounds - 1, (int)(rounds * 0.975))]);
    }

    private static List<double> Resample(List<double> values, Rng rng)
    {
        var drawn = new List<double>(values.Count);
        for (var index = 0; index < values.Count; index++) drawn.Add(values[rng.NextInt(values.Count)]);
        return drawn;
    }

    /// <summary>Probability a random winner outscores a random loser; 0.5 is a coin flip.</summary>
    private static double Auc(List<double> winners, List<double> losers)
    {
        double greater = 0, ties = 0;
        foreach (var winner in winners)
        foreach (var loser in losers)
        {
            if (winner > loser) greater++;
            // Exact equality first: a deck that never converged is an infinity on
            // both sides, and Infinity - Infinity is NaN, so the epsilon test below
            // is false for it and two identical infinities were being scored as a
            // win for the loser.
            else if (winner == loser || Math.Abs(winner - loser) < 0.0001) ties++;
        }
        return (greater + ties / 2) / (winners.Count * (double)losers.Count);
    }

    // ---- Controls -----------------------------------------------------------

    /// <summary>
    /// Decks whose correct ordering nobody disputes. A scorer that cannot put a
    /// starter deck below a winner, or a curse-stuffed winner below itself, has no
    /// claim on the finer distinctions it reports.
    /// </summary>
    private static void GradientControls(SimConfig config, List<CommunityDeck> decks, List<(string, bool, string)> checks)
    {
        Console.WriteLine();
        Console.WriteLine("controls — starter / winner / winner+curses / winner without upgrades");
        Console.WriteLine("character    starter  winner  +curses  unupgraded  (power, act 3 reference)");

        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = config.ReferenceFor(character, 2);
            var sample = decks.Where(d => d.Win && string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase))
                .Take(12).ToList();
            if (sample.Count == 0) continue;

            var winners = new List<double>();
            var cursed = new List<double>();
            var unupgraded = new List<double>();
            foreach (var deck in sample)
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                var withCurses = new List<CardModel>(cards);
                withCurses.AddRange(Curses(player, 6));
                var stripped = CommunityDecks.Build(player,
                    deck with { Deck = deck.Deck.Select(entry => entry with { Upgrades = 0 }).ToList() }, out _);
                winners.Add(DeckScorer.Score(cards, reference, config, 11).Power);
                cursed.Add(DeckScorer.Score(withCurses, reference, config, 11).Power);
                unupgraded.Add(DeckScorer.Score(stripped, reference, config, 11).Power);
            }
            if (winners.Count < 3) continue;

            var starter = player.Character.StartingDeck.Select(card => player.RunState.CreateCard(card, player)).ToList();
            // Averaged over a dozen winners rather than read off one deck. A single
            // deck is a coin flip away from being a run that won on relics alone —
            // the first version of this picked whichever deck came first in the
            // cache and reported all three numbers as zero.
            var starterScore = DeckScorer.Score(starter, reference, config, 11).Power;
            var winnerScore = winners.Average();
            var cursedScore = cursed.Average();
            var unupgradedScore = unupgraded.Average();
            Console.WriteLine($"{character,-11} {starterScore,7:F1} {winnerScore,7:F1} {cursedScore,8:F1} {unupgradedScore,11:F1}");

            if (!Covered(character)) continue;
            checks.Add(($"{character}: a real winner outscores the starter deck", winnerScore > starterScore,
                $"{winnerScore:F1} vs {starterScore:F1}"));
            checks.Add(($"{character}: curses drag a real winner down", cursedScore < winnerScore,
                $"{cursedScore:F1} vs {winnerScore:F1}"));
            checks.Add(($"{character}: stripping upgrades lowers a real winner", unupgradedScore < winnerScore,
                $"{unupgradedScore:F1} vs {winnerScore:F1}"));
        }
    }

    private static IEnumerable<CardModel> Curses(Player player, int count)
    {
        var pool = ModelDb.CardPool<CurseCardPool>()
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .Where(card => !string.Equals(card.Id.Entry, Ascension10.StartingCurse, StringComparison.Ordinal))
            .ToList();
        for (var index = 0; index < count && pool.Count > 0; index++)
            yield return player.RunState.CreateCard(pool[index % pool.Count], player);
    }

    // ---- Card level ---------------------------------------------------------

    /// <summary>
    /// Does the drafting logic rank cards the way the community does?
    ///
    /// A rank correlation between what BuildValue thinks of a card against a fixed
    /// reference deck and the community's own 0-100 score for it. The bar is set on
    /// the pooled sample rather than per character: 80 cards is a noisy estimate,
    /// 400 is not, and the question "is our card ranking related to winning" is one
    /// question, not five.
    ///
    /// A perfect correlation is not the target and could not be. The community
    /// score blends how often a card is picked with how often it wins, which folds
    /// in how often it is offered; this mod is built for co-op and the community
    /// sample is not.
    /// </summary>
    private static void CardAgreement(SimConfig config, IReadOnlyDictionary<string, CommunityCardScore> scores,
        List<(string, bool, string)> checks)
    {
        Console.WriteLine();
        Console.WriteLine("card agreement — BuildValue marginal (starter deck as context) vs community Codex score");
        Console.WriteLine("character    cards  spearman   vs Elo  top-10 overlap");

        var pooledOurs = new List<double>();
        var pooledTheirs = new List<double>();
        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = player.Character.StartingDeck.Select(card => player.RunState.CreateCard(card, player)).ToList();
            var pool = player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                .Where(card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient))
                .ToList();

            var pairs = new List<(string Id, double Ours, double Score, double Elo)>();
            foreach (var canonical in pool)
            {
                if (!scores.TryGetValue(canonical.Id.Entry, out var community)) continue;
                CardModel card;
                try { card = player.RunState.CreateCard(canonical, player); }
                catch { continue; }
                pairs.Add((canonical.Id.Entry, CommunityDecks.DraftValue(card, player, reference), community.Score, community.Elo));
            }
            if (pairs.Count < 10) continue;

            pooledOurs.AddRange(pairs.Select(pair => pair.Ours));
            pooledTheirs.AddRange(pairs.Select(pair => pair.Score));

            var correlation = Spearman(pairs.Select(pair => pair.Ours).ToList(), pairs.Select(pair => pair.Score).ToList());
            var elo = Spearman(pairs.Select(pair => pair.Ours).ToList(), pairs.Select(pair => pair.Elo).ToList());
            var ourTop = pairs.OrderByDescending(pair => pair.Ours).Take(10).Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);
            var theirTop = pairs.OrderByDescending(pair => pair.Score).Take(10).Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);
            Console.WriteLine($"{character,-11} {pairs.Count,5} {correlation,9:F2} {elo,8:F2} {ourTop.Intersect(theirTop).Count(),13}/10");

            // The two ends are what a drafting change can act on: a card the
            // community rates and this code does not is a card the bot is skipping.
            var missed = pairs.OrderByDescending(pair => pair.Score).ThenBy(pair => pair.Ours).Take(4)
                .Select(pair => $"{pair.Id}(ours {pair.Ours:F0}/community {pair.Score:F0})");
            var overrated = pairs.OrderByDescending(pair => pair.Ours).ThenBy(pair => pair.Score).Take(4)
                .Select(pair => $"{pair.Id}(ours {pair.Ours:F0}/community {pair.Score:F0})");
            Console.WriteLine($"              community likes, we don't: {string.Join(", ", missed)}");
            Console.WriteLine($"              we like, community doesn't: {string.Join(", ", overrated)}");
        }

        var pooled = Spearman(pooledOurs, pooledTheirs);
        Console.WriteLine($"{"pooled",-11} {pooledOurs.Count,5} {pooled,9:F2}");
        // The bar is "clearly above noise", not "high". The standard error of a rank
        // correlation over n cards is about 1/sqrt(n-1) — 0.05 at this sample size —
        // so anything past 0.1 is more than two standard errors from unrelated. A
        // higher bar would be asking a card valuation to agree with a score that
        // blends in how often a card is offered, which is not what it claims to be.
        checks.Add(("card value correlates with the community ranking", pooled > 0.1,
            $"pooled spearman {pooled:F2} over {pooledOurs.Count} cards (noise is about ±{1 / Math.Sqrt(Math.Max(1, pooledOurs.Count - 1)):F2})"));
    }

    // ---- Informational ------------------------------------------------------

    /// <summary>
    /// The mod's own drafts next to real winners. Reported, not asserted: the scorer
    /// sees a card list, while a real winner is a card list plus twenty relics, a
    /// potion belt and a human who knows the fight. A large gap here is a warning
    /// about what the scorer rewards, not proof of a better deck.
    /// </summary>
    private static void BotComparison(SimConfig config, List<CommunityDeck> decks)
    {
        Console.WriteLine();
        Console.WriteLine("report — real A10 winners vs this mod's own drafts (same reference, informational)");
        Console.WriteLine("character   real wins        mod drafts        gap | real: dmg blk surv | mod: dmg blk surv");

        foreach (var character in config.Characters)
        {
            var player = DraftRunner.NewPlayer(character, 1);
            var reference = config.ReferenceFor(character, 2);
            var real = new List<DeckScore>();
            foreach (var deck in decks.Where(d => d.Win && string.Equals(d.Character, character, StringComparison.OrdinalIgnoreCase)))
            {
                var cards = CommunityDecks.Build(player, deck, out var unknown);
                if (unknown == deck.Deck.Count) continue;
                real.Add(DeckScorer.Score(cards, reference, config, 7));
            }
            if (real.Count == 0) continue;

            var bot = new List<DeckScore>();
            for (var index = 0; index < config.RunsPerCharacter; index++)
                bot.Add(DraftRunner.Run(character, config.Seed + (ulong)index, config).Final);
            if (bot.Count == 0) continue;

            var realMean = Mean(real);
            var botMean = Mean(bot);
            Console.WriteLine($"{character,-11} {realMean.Power,6:F1} ({real.Count,2}) {botMean.Power,8:F1} ({bot.Count,2}) {realMean.Power - botMean.Power,+8:F1} |"
                + $" {realMean.DamagePerTurn,4:F0} {realMean.BlockPerTurn,4:F0} {realMean.SurvivalRate,4:P0} |"
                + $" {botMean.DamagePerTurn,4:F0} {botMean.BlockPerTurn,4:F0} {botMean.SurvivalRate,4:P0}");
        }
    }

    private static DeckScore Mean(List<DeckScore> scores) => new(
        scores.Average(s => s.Power), scores.Average(s => s.Offence), scores.Average(s => s.Defence),
        scores.Average(s => s.UpgradeScore), scores.Average(s => s.Efficiency), scores.Average(s => s.Consistency),
        scores.Average(s => s.DamagePerTurn), scores.Average(s => s.BlockPerTurn),
        scores.Average(s => s.TurnsToKill), scores.Average(s => s.KillRate), scores.Average(s => s.SurvivalRate),
        scores.Average(s => s.BlockCoverage), scores.Average(s => s.DeadDrawRate), scores.Average(s => s.EnergyWaste),
        scores.Average(s => s.RampRatio), scores.Average(s => s.PeakDamage), scores.Average(s => s.PeakTurn),
        (int)scores.Average(s => s.Size), (int)scores.Average(s => s.Curses),
        (int)scores.Average(s => s.Upgrades));

    /// <summary>Rank correlation, computed on ranks so the two scales need not match.</summary>
    private static double Spearman(List<double> first, List<double> second)
    {
        var a = Ranks(first);
        var b = Ranks(second);
        var meanA = a.Average();
        var meanB = b.Average();
        double covariance = 0, varianceA = 0, varianceB = 0;
        for (var index = 0; index < a.Count; index++)
        {
            covariance += (a[index] - meanA) * (b[index] - meanB);
            varianceA += (a[index] - meanA) * (a[index] - meanA);
            varianceB += (b[index] - meanB) * (b[index] - meanB);
        }
        return varianceA <= 0 || varianceB <= 0 ? 0 : covariance / Math.Sqrt(varianceA * varianceB);
    }

    private static List<double> Ranks(List<double> values)
    {
        var order = Enumerable.Range(0, values.Count).OrderBy(index => values[index]).ToList();
        var ranks = new double[values.Count];
        for (var position = 0; position < order.Count; position++) ranks[order[position]] = position + 1;
        return ranks.ToList();
    }
}
