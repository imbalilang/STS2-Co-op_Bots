using DeckSim;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

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

        var config = SimConfig.Default with { Characters = Characters(args), RunsPerCharacter = Runs(args) };
        var decks = CommunityDecks.Load();
        var scores = CommunityDecks.LoadScores();
        var sample = decks.Where(deck => config.Characters.Contains(deck.Character, StringComparer.OrdinalIgnoreCase)).ToList();
        if (sample.Count == 0)
            throw new InvalidOperationException($"No cached decks for {string.Join(", ", config.Characters)}.");

        var wins = sample.Count(deck => deck.Win);
        Console.WriteLine();
        Console.WriteLine($"COMMUNITY VALIDATION — {sample.Count} cached A10 finished decks ({wins} wins, {sample.Count - wins} losses), "
            + $"{scores.Count} community-scored cards");
        var checks = new List<(string Name, bool Passed, string Detail)>();

        if (args.Contains("--features")) FeaturePower(config, sample, scores);
        if (args.Contains("--calibrate")) Calibrate(config, sample);
        if (args.Contains("--par")) ParReport(config, sample);
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
    private static void FeaturePower(SimConfig config, List<CommunityDeck> decks,
        IReadOnlyDictionary<string, CommunityCardScore> community)
    {
        Console.WriteLine();
        Console.WriteLine("feature power — AUC against A10 win/loss, per character (0.50 = no signal)");
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
                sample.Add((DeckScorer.Score(cards, reference, config, 7), deck, deck.Win));
            }
        }

        var header = string.Join(" ", config.Characters.Select(c => $"{c[..Math.Min(6, c.Length)],6}"));
        Console.WriteLine($"{"feature",-26} {header}   mean");
        foreach (var (name, value) in features)
        {
            var row = new List<double>();
            foreach (var character in config.Characters)
            {
                var rows = sample.Where(row => string.Equals(row.Deck.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();
                var wins = rows.Where(row => row.Win).Select(row => value(row.Score, row.Deck)).ToList();
                var losses = rows.Where(row => !row.Win).Select(row => value(row.Score, row.Deck)).ToList();
                if (wins.Count < 5 || losses.Count < 5) continue;
                row.Add(Auc(wins, losses));
            }
            Console.WriteLine($"{name,-26} " + string.Join(" ", row.Select(v => $"{v,6:F2}")) + $"   {(row.Count == 0 ? 0 : row.Average()),6:F2}");
        }
        Console.WriteLine("A feature worth weighting reads above 0.55 on most characters; near 0.50 is decoration.");
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
    private static void ParReport(SimConfig config, List<CommunityDeck> decks)
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
            Console.WriteLine($"{character,-11} {scores.Count,4} {mean.DamagePerTurn,8:F1} {medianDamage,11:F1}"
                + $" {mean.BlockPerTurn,9:F1} {medianBlock,11:F1} {mean.SurvivalRate,9:P0}"
                + $"   [\"{character}\"] = new(Damage: {medianDamage:F1}, Block: {medianBlock:F1},"
                + $" Upgrades: {medianUpgrades:F0}, Efficiency: {medianEfficiency:F2}),");
            Console.WriteLine($"{"",-11}      efficiency median {medianEfficiency:F3}, upgrades median {medianUpgrades:F0}");
        }
    }

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
            // Reported, never asserted. The community control on the same rows is
            // what settles it: ranking these decks by summing the community's own
            // per-card score gets AUC 0.40-0.69, which is to say a card list barely
            // predicts who beat the act-3 boss. A test whose ceiling is a coin flip
            // cannot be the test a deck-list scorer is held to, and tuning this
            // scorer until it passed would be fitting it to relic luck.
            // Kept visible because a character where the gap is large and negative
            // (REGENT here) is worth a look, not a pass/fail.
            Console.WriteLine($"{character,-11} {win.Power,7:F1} ({winners.Count,2})"
                + $" {loss.Power,7:F1} ({losers.Count,2}) {win.Power - loss.Power,+8:F1} {auc,6:F2} {communityAuc,13:F2} |"
                + $" {win.Size,4} {win.Upgrades,3} {win.DamagePerTurn,3:F0} {win.BlockPerTurn,3:F0} {win.SurvivalRate,5:P0}"
                + $" ({win.Offence,3:F0}/{win.Defence,3:F0}/{win.Consistency,3:F0}) |"
                + $" {loss.Size,5} {loss.Upgrades,3} {loss.DamagePerTurn,3:F0} {loss.BlockPerTurn,3:F0} {loss.SurvivalRate,5:P0}"
                + $" ({loss.Offence,3:F0}/{loss.Defence,3:F0}/{loss.Consistency,3:F0})");
        }
    }

    private static string Shorten(string? killedBy)
    {
        if (string.IsNullOrEmpty(killedBy)) return string.Empty;
        var name = killedBy.Split('.').Last();
        return name is "NONE" or "" ? string.Empty : name;
    }

    /// <summary>Probability a random winner outscores a random loser; 0.5 is a coin flip.</summary>
    private static double Auc(List<double> winners, List<double> losers)
    {
        double greater = 0, ties = 0;
        foreach (var winner in winners)
        foreach (var loser in losers)
        {
            if (winner > loser) greater++;
            else if (Math.Abs(winner - loser) < 0.0001) ties++;
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
        scores.Average(s => s.RampRatio), (int)scores.Average(s => s.Size), (int)scores.Average(s => s.Curses),
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
