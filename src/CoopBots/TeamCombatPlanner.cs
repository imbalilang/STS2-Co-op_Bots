using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots;

/// <summary>
/// Bounded current-turn team search shared by all bots. It borrows the architectural
/// idea of separating frontier retention from final route ordering, but uses an
/// independently implemented co-op projection instead of CombatSolver's
/// single-player simulation engine.
/// </summary>
internal static class TeamCombatPlanner
{
    private const int MaxCards = 56;
    private const int MaxTargetsPerCard = 4;
    private const int MaxDepth = 9;
    private const int BeamWidth = 44;
    private const double HumanConfidence = 0.35;
    private const double BotDeathBasePenalty = 40;
    private const double BotDeathMaxHpPenalty = 0.25;

    internal sealed class SearchStatistics
    {
        internal int ExpandedNodes;
        internal int Evaluations;
        internal int EvaluationCacheHits;
        internal int DamageHookCalls;
        internal int BlockProjectionCalls;
        internal double ElapsedMs;
        internal long AllocatedBytes;
    }
    internal static SearchStatistics? LastSearchStatistics { get; private set; }
    private static long _nextSlowLogAt;

    internal sealed record Decision(Player Player, BotBrain.CombatMove Move, int PlannedCards,
        int DeathsPrevented = 0, double HpSaved = 0, bool AssistedPlayerSurvives = false);

    private sealed record Candidate(
        int CardIndex,
        int PlayerIndex,
        Player Player,
        BotBrain.CombatMove Move,
        GeniusCombatStrategy.CardFacts Facts,
        int EnergyCost,
        int Hits,
        bool GuaranteedDirectDamage);

    private sealed class PlanNode
    {
        internal required int[] Energy;
        internal required int[] Stars;
        internal required double[] AddedStrength;
        internal required double[] AddedDexterity;
        internal required int[] Artifact;
        internal required double[] Flanking;
        internal required double[] Knockdown;
        internal required int[] TagTeam;
        internal required int[] AttackHits;
        internal required double[] OneForAll;
        internal required double[] Sneaky;
        internal required bool[] Beacon;
        internal required int[] ActionsByBot;
        internal required double[] HardEnemyHp;
        internal required double[] SoftEnemyHp;
        internal required double[] ExtraBlock;
        internal required double[] HpSpent;
        internal required double[] FutureGrowth;
        // Strength this enemy loses for the coming enemy phase (尖啸/黑暗镣铐/
        // 弱化之触/星灭/凌虐). Unlike Weak it is additive per hit, so it is kept
        // as an amount rather than a precomputed incoming matrix.
        internal required double[] EnemyStrengthDown;
        internal required CombatResourceProjection.State Resources;
        internal required CombatSolverCardRelicProjection.State Imported;
        internal required double[] DemonForm;
        internal required bool[] Barricade;
        internal ulong UsedCards;
        internal ulong AddedVulnerable;
        internal ulong AddedWeak;
        internal List<int> Path = new();
        internal double SetupValue;
        internal double HumanSynergy;
        internal double IndividualValue;
        internal bool UsedHumanHand;
        // Nodes are immutable after expansion; Fork deliberately does not copy this cache.
        internal Metrics? CachedMetrics;

        internal PlanNode Fork()
            => new()
            {
                Energy = (int[])Energy.Clone(),
                Stars = (int[])Stars.Clone(),
                AddedStrength = (double[])AddedStrength.Clone(),
                AddedDexterity = (double[])AddedDexterity.Clone(),
                Artifact = (int[])Artifact.Clone(),
                Flanking = (double[])Flanking.Clone(),
                Knockdown = (double[])Knockdown.Clone(),
                TagTeam = (int[])TagTeam.Clone(),
                AttackHits = (int[])AttackHits.Clone(),
                OneForAll = (double[])OneForAll.Clone(),
                Sneaky = (double[])Sneaky.Clone(),
                Beacon = (bool[])Beacon.Clone(),
                ActionsByBot = (int[])ActionsByBot.Clone(),
                HardEnemyHp = (double[])HardEnemyHp.Clone(),
                SoftEnemyHp = (double[])SoftEnemyHp.Clone(),
                ExtraBlock = (double[])ExtraBlock.Clone(),
                HpSpent = (double[])HpSpent.Clone(),
                FutureGrowth = (double[])FutureGrowth.Clone(),
                EnemyStrengthDown = (double[])EnemyStrengthDown.Clone(),
                Resources = Resources.Fork(),
                Imported = Imported.Fork(),
                DemonForm = (double[])DemonForm.Clone(),
                Barricade = (bool[])Barricade.Clone(),
                UsedCards = UsedCards,
                AddedVulnerable = AddedVulnerable,
                AddedWeak = AddedWeak,
                Path = new List<int>(Path),
                SetupValue = SetupValue,
                HumanSynergy = HumanSynergy,
                IndividualValue = IndividualValue,
                UsedHumanHand = UsedHumanHand,
            };
    }

    private readonly record struct Metrics(
        int HumanDeaths,
        bool TeamWiped,
        double TriageCost,
        bool AssistedPlayerSurvives,
        int WeightedDeaths,
        bool ConfirmedVictory,
        double WeightedHpLoss,
        int ConfirmedKills,
        double RemainingThreat,
        double HardEnemyHp,
        double SoftProjectedHpLoss,
        double SoftEnemyHp,
        double FocusRemainingHp,
        double SetupValue,
        double HumanSynergy,
        double IndividualValue,
        int ActionCount,
        int UnspentEnergy,
        double FocusCost,
        double BoundaryValue);

    private sealed record HumanPotential(
        double[] AttackByEnemy,
        double[] BlockByPartyMember,
        bool[] CanApplyVulnerable,
        int AttackCards,
        int BlockCards)
    {
        internal static HumanPotential Empty(int enemies, int party)
            => new(new double[enemies], new double[party], new bool[enemies], 0, 0);
    }

    private sealed class PlanningContext
    {
        internal required IReadOnlyList<Player> Bots;
        internal required IReadOnlyList<Player> Party;
        internal required IReadOnlyList<Creature> Enemies;
        internal required IReadOnlyList<Candidate> Candidates;
        internal required HumanPotential Human;
        internal required double[,] IncomingByEnemyAndPartyMember;
        internal required double[,] WeakIncoming;
        // Hits the enemy's current move makes; a point of Strength loss removes
        // this much damage for each teammate the move targets.
        internal required int[] EnemyHitCount;
        internal required uint? FocusTarget;
        internal Creature? StrategicTarget;
        internal required CombatResourceProjection.Snapshot Resources;
        internal required TurnBoundaryProjection.Snapshot TurnBoundary;
        internal required MonsterHazards.Sandpit[] Sandpits;
        internal required CombatSolverCardRelicProjection.Snapshot Imported;
        internal Player? AssistedPlayer;
        internal double AssistedBlock;
        internal double AssistedHealing;
        internal required SearchStatistics Statistics;
        // Synchronous search-local caches: discarded before the next real action.
        internal readonly Dictionary<(CardModel Card, Creature Enemy, decimal Raw, ValueProp Props), double> DamageCache = new();
        internal readonly Dictionary<(CardModel Card, Creature Recipient, Creature? Target, double Extra), double> BlockCache = new();
    }

    private static readonly HashSet<string> GuaranteedDirectAttacks = new(StringComparer.Ordinal)
    {
        "StrikeIronclad", "StrikeSilent", "StrikeRegent", "StrikeNecrobinder", "StrikeDefect",
        "TwinStrike", "Bludgeon", "Bash", "DaggerSpray", "PommelStrike", "SweepingBeam", "Uppercut",
        "Knockdown", "TagTeam", "GangUp", "BodySlam", "FlashOfSteel",
    };

    private static readonly HashSet<string> ChainStablePowers = new(StringComparer.Ordinal)
    {
        "StrengthPower", "DexterityPower", "FocusPower", "WeakPower", "VulnerablePower", "FrailPower",
        "NoBlockPower", "ArtifactPower", "NoDrawPower",
        "OneForAllPower", "SneakyPower", "BeaconOfHopePower",
        // Verified current-turn transparent effects; costs are read with modifiers.
        "DemonFormPower", "RitualPower", "BarricadePower", "PlatingPower", "CorruptionPower",
        "FeelNoPainPower", "DarkEmbracePower",
    };

    private static readonly HashSet<string> DamageTransparentTargetPowers = new(StringComparer.Ordinal)
    {
        "VulnerablePower", "WeakPower", "StrengthPower", "ArtifactPower",
        "FlankingPower", "KnockdownPower", "TagTeamPower", "SandpitPower",
        // Temporary enemy Strength loss only changes what this enemy deals, not
        // what it takes, so a kill confirmed after applying it stays confirmed.
        "PiercingWailPower", "DarkShacklesPower", "EnfeeblingTouchPower",
        "DyingStarPower", "ManglePower", "CrushUnderPower",
    };

    internal static Decision? Choose(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, uint? focus)
        => ChooseCore(bots, party, focus, null);

    internal static Decision? ChooseCore(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, uint? focus, Player? firstActor)
        => Search(bots, party, focus, firstActor, null, 0);

    internal static bool CanRescueWithProtection(IReadOnlyList<Player> bots, IReadOnlyList<Player> party,
        Player recipient, double protection, bool healing = false)
        => Search(bots, party, null, null, recipient, healing ? 0 : protection, healing ? protection : 0)?.AssistedPlayerSurvives == true;

    private static Decision? Search(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, uint? focus,
        Player? firstActor, Player? assistedPlayer, double assistedBlock, double assistedHealing = 0)
    {
        var statistics = new SearchStatistics();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            return SearchCore(bots, party, focus, firstActor, assistedPlayer, assistedBlock, assistedHealing, statistics);
        }
        finally
        {
            statistics.ElapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            statistics.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            LastSearchStatistics = statistics;
            if (statistics.ElapsedMs >= 100 && Environment.TickCount64 >= _nextSlowLogAt)
            {
                _nextSlowLogAt = Environment.TickCount64 + 5000;
                MegaCrit.Sts2.Core.Logging.Log.Info($"CoopBots slow search: kind={(assistedPlayer is not null ? "rescue" : firstActor is not null ? "advice" : "team")}; ms={statistics.ElapsedMs:F1}; nodes={statistics.ExpandedNodes}; evaluations={statistics.Evaluations}; cacheHits={statistics.EvaluationCacheHits}; damageHooks={statistics.DamageHookCalls}; blockProjections={statistics.BlockProjectionCalls}; bytes={statistics.AllocatedBytes}");
            }
        }
    }

    private static Decision? SearchCore(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, uint? focus,
        Player? firstActor, Player? assistedPlayer, double assistedBlock, double assistedHealing, SearchStatistics statistics)
    {
        // Team safety is shared by all difficulty levels; individual fallback still honors difficulty.
        var geniusBots = bots.Where(p => p.Creature.IsAlive
            && p.PlayerCombatState is { Phase: PlayerTurnPhase.Play }).ToList();
        var combat = geniusBots.FirstOrDefault()?.Creature.CombatState;
        if (combat is null)
            return null;

        var livingParty = party.Where(p => p.Creature.IsAlive).ToList();
        var enemies = combat.HittableEnemies.Where(e => e.IsAlive).Take(12).ToList();
        if (livingParty.Count == 0 || enemies.Count == 0)
            return null;

        var candidates = BuildCandidates(geniusBots, enemies);
        if (candidates.Count == 0)
            return null;

        var context = new PlanningContext
        {
            Bots = geniusBots,
            Party = livingParty,
            Enemies = enemies,
            Candidates = candidates,
            Human = BuildHumanPotential(livingParty.Where(p => !geniusBots.Contains(p)).ToList(), enemies, livingParty),
            IncomingByEnemyAndPartyMember = BuildIncoming(enemies, livingParty),
            WeakIncoming = BuildWeakIncoming(enemies, livingParty),
            EnemyHitCount = BuildEnemyHitCount(enemies),
            FocusTarget = focus,
            StrategicTarget = TeamFocus.Resolve(combat, enemies, focus),
            Resources = CombatResourceProjection.Capture(geniusBots, candidates.GroupBy(c => c.CardIndex)
                .OrderBy(group => group.Key).Select(group => group.First().Move.Card).ToArray()),
            TurnBoundary = TurnBoundaryProjection.Capture(livingParty),
            Sandpits = MonsterHazards.Capture(geniusBots[0].Creature),
            Imported = CombatSolverCardRelicProjection.Capture(livingParty),
            AssistedPlayer = assistedPlayer,
            AssistedBlock = assistedBlock,
            AssistedHealing = assistedHealing,
            Statistics = statistics,
        };

        var root = CreateRoot(context);
        var rootMetrics = Evaluate(root, context);
        var frontier = new List<PlanNode> { root };
        var finalists = new List<PlanNode>();

        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var expanded = new List<PlanNode>(frontier.Count * Math.Min(candidates.Count, 24));
            foreach (var node in frontier)
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                if (depth == 0 && firstActor is not null && candidate.Player != firstActor) continue;
                if (depth == 0 && !candidate.Move.Card.CanPlayTargeting(candidate.Move.Target)) continue;
                if (candidate.Move.Target is { IsEnemy: true } target)
                {
                    var targetIndex = IndexOfEnemy(context.Enemies, target);
                    if (targetIndex < 0 || node.HardEnemyHp[targetIndex] <= 0) continue;
                }
                var cardBit = 1UL << candidate.CardIndex;
                if ((node.Resources.Available & cardBit) == 0) continue;
                if (candidate.Move.Card.GetType().Name == "BattleTrance"
                    && !CombatResourceProjection.CanDrawAfterPlay(node.Resources, context.Resources, candidate.PlayerIndex)) continue;
                var actorIndex = IndexOfPlayer(context.Party, candidate.Player);
                // A last stand happens before the enemy turn; it must not assume
                // a card completes after its caster dies paying its own HP cost.
                var actorHp = candidate.Player.Creature.CurrentHp
                    + (candidate.Player == context.AssistedPlayer ? context.AssistedHealing : 0);
                if (actorIndex >= 0 && node.HpSpent[actorIndex] + candidate.Facts.HpLoss >= actorHp) continue;
                if ((node.UsedCards & cardBit) != 0
                    || CombatResourceProjection.Cost(node.Resources, context.Resources.Cards[candidate.CardIndex], candidate.EnergyCost) > node.Energy[candidate.PlayerIndex]
                    || Math.Max(0, candidate.Move.Card.GetStarCostWithModifiers()) > node.Stars[candidate.PlayerIndex])
                    continue;
                expanded.Add(Apply(node, candidate, candidateIndex, context));
                statistics.ExpandedNodes++;
            }

            if (expanded.Count == 0)
                break;
            frontier = Retain(expanded, context);
            finalists.AddRange(frontier);
        }

        if (finalists.Count == 0)
            return null;

        var best = finalists.OrderBy(node => node, Comparer<PlanNode>.Create((left, right) =>
            CompareFinal(left, right, context))).First();
        var bestMetrics = Evaluate(best, context);
        if (best.Path.Count == 0 || CompareMetrics(bestMetrics, rootMetrics) >= 0)
            return null;

        var first = candidates[best.Path[0]];
        var reasons = new List<string> { "team-beam" };
        if (best.Path.Any(index => candidates[index].Move.Card.Type == CardType.Attack
            && !candidates[index].GuaranteedDirectDamage)) reasons.Add("partial-damage-estimate");
        if (bestMetrics.ConfirmedVictory) reasons.Add("confirmed-team-lethal");
        else if (bestMetrics.ConfirmedKills > rootMetrics.ConfirmedKills) reasons.Add("confirmed-kill");
        if (bestMetrics.WeightedDeaths < rootMetrics.WeightedDeaths
            || bestMetrics.WeightedHpLoss + 0.01 < rootMetrics.WeightedHpLoss)
            reasons.Add("team-survival");
        if (best.UsedHumanHand) reasons.Add("human-hand-synergy");
        if (bestMetrics.WeightedDeaths > 0) reasons.Add("last-stand-team-value");
        if (finalists.Any(node =>
        {
            var other = Evaluate(node, context);
            return other.HumanDeaths == bestMetrics.HumanDeaths && other.WeightedDeaths < bestMetrics.WeightedDeaths;
        })) reasons.Add("costly-rescue-declined");
        if (focus.HasValue && first.Move.Target?.CombatId == focus) reasons.Add("focus");
        if (!focus.HasValue && context.Enemies.Count > 1 && ReferenceEquals(first.Move.Target, context.StrategicTarget))
            reasons.Add("sustained-focus");
        reasons.Add($"sequence:{best.Path.Count}");

        return new Decision(first.Player, first.Move with
        {
            Score = DecisionPriority(first, rootMetrics, bestMetrics, best),
            Reason = string.Join(',', reasons),
        }, best.Path.Count, rootMetrics.WeightedDeaths - bestMetrics.WeightedDeaths,
            rootMetrics.WeightedHpLoss - bestMetrics.WeightedHpLoss, bestMetrics.AssistedPlayerSurvives);
    }

    private static List<Candidate> BuildCandidates(IReadOnlyList<Player> bots, IReadOnlyList<Creature> enemies)
    {
        var candidates = new List<Candidate>();
        var cardIndexes = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);
        foreach (var (bot, playerIndex) in bots.Select((player, index) => (player, index)))
        {
            var legal = BotBrain.PlanningCombatMoves(bot, BotDifficulty.Genius, allowFutureResources: true);
            if ((bot.PlayerCombatState!.Hand.Cards.Any(CombatResourceProjection.ModelsDraw)
                || bot.PlayerCombatState.Hand.Cards.Any(card => card.GetType().Name == "DarkEmbrace")
                || bot.Creature.Powers.Any(power => power.GetType().Name == "DarkEmbracePower"))
                && CombatResourceProjection.CanExpandDraw(bot))
            {
                foreach (var card in bot.PlayerCombatState.DrawPile.Cards.Take(CombatResourceProjection.DrawWindow))
                {
                    if (!CombatResourceProjection.CanEnterFromDraw(card)) break;
                    card.CanPlay(out var reason, out _);
                    if ((reason & ~(UnplayableReason.EnergyCostTooHigh | UnplayableReason.StarCostTooHigh)) != UnplayableReason.None) continue;
                    var targets = bot.Creature.CombatState!.Creatures.Where(card.IsValidTarget).Cast<Creature?>().ToList();
                    if (card.IsValidTarget(null)) targets.Add(null);
                    legal.AddRange(targets.Select(target => new BotBrain.CombatMove(card, target, 0, "draw-pile-projection")));
                }
            }
            var scored = GeniusCombatStrategy.ScoreLegalMoves(bot, legal, 0);
            var selectedCards = scored.GroupBy(move => move.Card)
                .OrderByDescending(group => bot.PlayerCombatState.Hand.Cards.Contains(group.Key))
                .ThenByDescending(group => group.Key.TargetType == TargetType.AnyAlly)
                .ThenByDescending(group => group.Max(move => move.Score))
                .Take(10 + CombatResourceProjection.DrawWindow);

            foreach (var cardGroup in selectedCards)
            {
                var card = cardGroup.Key;
                if (!SupportsCard(card))
                    continue;
                foreach (var move in SelectTargets(cardGroup))
                {
                    var energy = bot.PlayerCombatState?.Energy ?? 0;
                    var facts = GeniusCombatStrategy.Analyze(card, move.Target, energy, enemies.Count);
                    // Generic "Power" value is not an implementation of its effect.
                    // Leave opaque powers to the explicit one-action fallback.
                    if (card.Type == CardType.Power && !ModeledEffects.Contains(card.GetType().Name)
                        && facts.Strength == 0 && facts.Dexterity == 0 && facts.Focus == 0
                        && facts.DamagePerEnemy == 0 && facts.Block == 0 && facts.ImmediateEnergy == 0)
                        continue;
                    var cost = Math.Max(0, facts.EnergyCost);
                    if (move.Score <= -500)
                        continue;
                    if (!cardIndexes.TryGetValue(card, out var cardIndex))
                    {
                        if (cardIndexes.Count >= MaxCards) continue;
                        cardIndex = cardIndexes.Count;
                        cardIndexes.Add(card, cardIndex);
                    }
                    var guaranteed = GuaranteedDirectAttacks.Contains(card.GetType().Name)
                        && card.Enchantment is null
                        && bot.Creature.Powers.All(power => ChainStablePowers.Contains(power.GetType().Name));
                    var perHit = (double)(card.DynamicVars.Values.OfType<DamageVar>().FirstOrDefault()?.PreviewValue ?? 0);
                    var hits = card.GetType().Name is "TwinStrike" or "DaggerSpray" ? 2
                        : perHit >= 1 ? Math.Max(1, (int)(facts.DamagePerEnemy / Math.Floor(perHit))) : 1;
                    candidates.Add(new Candidate(cardIndex, playerIndex, bot, move, facts, cost,
                        hits, guaranteed));
                }
            }
        }
        return candidates;
    }

    internal static bool SupportsCard(CardModel card) => (card.Type is not (CardType.Curse or CardType.Status)
        || card.GetType().Name == "FranticEscape")
        && !card.EnergyCost.CostsX && !card.HasStarCostX
        && card.GetType().Name is not ("BulletTime" or "DoubleEnergy" or "Intercept");

    // Eligibility for the projection is not a claim that all effects are modeled.
    // These cards have no unknown draw/generation/turn-transition effects in our model.
    internal static bool NeedsEffectFallback(CardModel card) => !SupportsCard(card)
        || !ModeledEffects.Contains(card.GetType().Name) || CombatResourceProjection.NeedsDrawFallback(card);

    private static readonly HashSet<string> ModeledEffects = new(StringComparer.Ordinal)
    {
        "StrikeIronclad", "StrikeSilent", "StrikeRegent", "StrikeNecrobinder", "StrikeDefect",
        "DefendIronclad", "DefendSilent", "DefendRegent", "DefendNecrobinder", "DefendDefect",
        "TwinStrike", "Bludgeon", "Bash", "DaggerSpray", "Uppercut", "Shockwave", "LegSweep",
        "Knockdown", "TagTeam", "GangUp", "Flanking", "Lift", "Rally", "Mimic", "DemonicShield",
        "Blaze", "OneForAll", "Sneaky", "BeaconOfHope", "Inflame", "Footwork", "Defragment",
        "Corruption", "BattleTrance", "Offering", "Bloodletting", "PommelStrike", "ShrugItOff",
        "DemonForm", "Barricade", "FranticEscape", "Entrench", "BodySlam", "FeelNoPain", "DarkEmbrace",
        "Impervious", "Backflip", "Finesse", "FlashOfSteel",
    };

    private static IEnumerable<BotBrain.CombatMove> SelectTargets(IEnumerable<BotBrain.CombatMove> moves)
    {
        var ranked = moves.OrderByDescending(move => move.Score).ToList();
        var selected = new List<BotBrain.CombatMove>();
        void Add(BotBrain.CombatMove? move)
        {
            if (move.HasValue && selected.Count < MaxTargetsPerCard
                && !selected.Any(existing => ReferenceEquals(existing.Target, move.Value.Target)))
                selected.Add(move.Value);
        }

        Add(ranked.Count > 0 ? ranked[0] : null);
        Add(ranked.Where(move => move.Target is { IsPlayer: true }
                && CombatAssessment.InDanger(move.Target))
            .Select(move => (BotBrain.CombatMove?)move).FirstOrDefault());
        Add(ranked.Where(move => move.Target is { IsEnemy: true })
            .OrderByDescending(move => move.Target!.Monster?.IntendsToAttack == true)
            .ThenBy(move => EffectiveHp(move.Target!))
            .Select(move => (BotBrain.CombatMove?)move).FirstOrDefault());
        foreach (var move in ranked)
            Add(move);
        return selected;
    }

    private static PlanNode Apply(PlanNode node, Candidate candidate, int candidateIndex, PlanningContext context)
    {
        var current = node;
        var plays = 1;
        if (candidate.Move.Card.Type == CardType.Attack)
        {
            // Play-count modifiers are consumed once, before the card's first play.
            // An AOE can consume instances on several enemies, but repeats the entire card.
            foreach (var enemyIndex in AffectedEnemies(candidate, context))
            for (var source = 0; source <= context.Party.Count; source++)
            {
                if (current.HardEnemyHp[enemyIndex] <= 0) continue;
                if (source < context.Party.Count && context.Party[source] == candidate.Player) continue;
                var index = enemyIndex * (context.Party.Count + 1) + source;
                if (current.TagTeam[index] == 0) continue;
                // Only replay consumption needs a pre-play copy. ApplyPlay owns
                // the copy for ordinary cards; never mutate the parent frontier.
                if (ReferenceEquals(current, node)) current = node.Fork();
                plays += current.TagTeam[index];
                current.TagTeam[index] = 0;
            }
        }
        // Conservative bound for unusually large stacked replay counts.
        for (var play = 0; play < Math.Min(plays, 16); play++)
            current = ApplyPlay(current, candidate, candidateIndex, context, play == 0);
        // Card resolution (including all replays) completes before the played
        // card moves to Exhaust. CombatSolver's exhaust hooks then grant block
        // and draw to that card's owner, never to every teammate.
        var bit = 1UL << candidate.CardIndex;
        if ((current.Resources.Exhausted & bit) != 0 && (node.Resources.Exhausted & bit) == 0)
        {
            var owner = IndexOfPlayer(context.Party, candidate.Player);
            var block = current.Imported.FeelNoPain[owner];
            if (block > 0)
                AddProjectedBlock(current, owner, (double)Hook.ModifyBlock(candidate.Move.Card.CombatState!,
                    candidate.Player.Creature, block, ValueProp.Unpowered, null, null, out _), context);
            if (current.Imported.DarkEmbrace[owner] > 0)
                CombatResourceProjection.Draw(current.Resources, context.Resources, candidate.PlayerIndex, current.Imported.DarkEmbrace[owner]);
        }
        return current;
    }

    private static PlanNode ApplyPlay(PlanNode node, Candidate candidate, int candidateIndex,
        PlanningContext context, bool payResources)
    {
        var child = node.Fork();
        child.UsedCards |= 1UL << candidate.CardIndex;
        if (payResources)
        {
            var spec = context.Resources.Cards[candidate.CardIndex];
            child.Path.Add(candidateIndex);
            child.ActionsByBot[candidate.PlayerIndex]++;
            child.Energy[candidate.PlayerIndex] -= CombatResourceProjection.Cost(node.Resources, spec, candidate.EnergyCost);
            CombatResourceProjection.BeginPlay(child.Resources, candidate.CardIndex, spec);
            child.Stars[candidate.PlayerIndex] -= Math.Max(0, candidate.Move.Card.GetStarCostWithModifiers());
            child.IndividualValue += candidate.Move.Score;
        }

        var card = candidate.Move.Card;
        var id = card.GetType().Name;
        if (id == "Corruption") child.Resources.Corruption[candidate.PlayerIndex] = true;
        var casterPartyIndex = IndexOfPlayer(context.Party, candidate.Player);
        if (casterPartyIndex >= 0)
        {
            CombatSolverCardRelicProjection.PlayPower(child.Imported, card, casterPartyIndex);
            if (id == "DemonForm") child.DemonForm[casterPartyIndex] += (double)card.DynamicVars["StrengthPower"].BaseValue;
            if (id == "Barricade") child.Barricade[casterPartyIndex] = true;
        }
        var targetIndex = candidate.Move.Target is { IsPlayer: true } ally
            ? IndexOfCreature(context.Party, ally) : casterPartyIndex;
        var recipients = card.TargetType == TargetType.AllAllies
            ? Enumerable.Range(0, context.Party.Count).ToArray()
            : new[] { id == "Mimic" ? casterPartyIndex : targetIndex };
        foreach (var partyIndex in recipients.Where(index => index >= 0))
        {
            var recipient = context.Party[partyIndex];
            var botIndex = IndexOfPlayer(context.Bots, recipient);
            if (botIndex >= 0)
                child.Energy[botIndex] += (int)Math.Floor(Math.Max(0, candidate.Facts.ImmediateEnergy));
            child.AddedStrength[partyIndex] += candidate.Facts.Strength;
            child.AddedDexterity[partyIndex] += candidate.Facts.Dexterity;
            child.FutureGrowth[partyIndex] += Math.Max(0, candidate.Facts.Strength) * 5
                + Math.Max(0, candidate.Facts.Dexterity) * 4 + Math.Max(0, candidate.Facts.Focus) * 5
                + (card.Type == CardType.Power && id is not ("Corruption" or "DemonForm" or "Barricade" or "FeelNoPain" or "DarkEmbrace") && ModeledEffects.Contains(id) ? 8.0 / recipients.Length : 0);
            if (id == "OneForAll") child.OneForAll[partyIndex] += (double)card.DynamicVars["OneForAllPower"].BaseValue;
            if (id == "Sneaky") child.Sneaky[partyIndex] += (double)card.DynamicVars["SneakyPower"].BaseValue;
            if (id == "BeaconOfHope") child.Beacon[partyIndex] = true;
            var block = 0.0;
            // Planned copy amounts and Dexterity enter before the recipient's block modifiers.
            var extraBase = casterPartyIndex >= 0 ? node.AddedDexterity[casterPartyIndex] : 0;
            if (id == "Mimic" && targetIndex >= 0)
                extraBase += node.ExtraBlock[targetIndex];
            if (id == "DemonicShield" && casterPartyIndex >= 0)
                extraBase += node.ExtraBlock[casterPartyIndex];
            if (id == "Entrench")
            {
                // Port of BespokeCardMirrors.EntrenchOnPlay: current branch
                // block is the base; Unpowered|Move avoids Dexterity scaling.
                block = (double)Hook.ModifyBlock(card.CombatState!, recipient.Creature,
                    recipient.Creature.Block + (decimal)node.ExtraBlock[partyIndex],
                    ValueProp.Unpowered | ValueProp.Move, card, null, out _);
            }
            else if (card.GainsBlock)
            {
                var key = (card, recipient.Creature, candidate.Move.Target, Math.Max(0, extraBase));
                if (!context.BlockCache.TryGetValue(key, out block))
                {
                    block = extraBase > 0 ? ProjectBlock(card, recipient.Creature, candidate.Move.Target, extraBase)
                        : CombatAssessment.BlockFor(card, recipient.Creature, candidate.Move.Target);
                    context.BlockCache.Add(key, block);
                    context.Statistics.BlockProjectionCalls++;
                }
            }
            AddProjectedBlock(child, partyIndex, block, context);
            if (context.Human.BlockByPartyMember[partyIndex] > 0)
            {
                child.HumanSynergy -= Math.Min(block,
                    context.Human.BlockByPartyMember[partyIndex] * HumanConfidence) * 0.4;
                child.UsedHumanHand = true;
            }
        }

        if (casterPartyIndex >= 0)
            child.HpSpent[casterPartyIndex] += Math.Max(0, candidate.Facts.HpLoss);

        foreach (var enemyIndex in AffectedEnemies(candidate, context))
        {
            if (node.HardEnemyHp[enemyIndex] <= 0) continue;
            var enemy = context.Enemies[enemyIndex];
            var baseDamage = candidate.Move.Target is { IsEnemy: true }
                ? candidate.Facts.TotalDamage
                : candidate.Facts.DamagePerEnemy;
            var speculativeDamage = ProjectDamage(candidate, enemy, node, context, enemyIndex, casterPartyIndex,
                (child.AddedVulnerable & (1UL << enemyIndex)) != 0,
                SharedMultiplier(node, context, enemyIndex, candidate.Player));
            if ((child.AddedVulnerable & (1UL << enemyIndex)) == 0
                && baseDamage > 0 && context.Human.CanApplyVulnerable[enemyIndex]
                && !HasPower(enemy, "VulnerablePower"))
            {
                // The player may choose to amplify this target later. Keep this
                // as a small option-value tie breaker; do not inflate damage.
                child.HumanSynergy += Math.Min(12, baseDamage * 0.15 * HumanConfidence);
                child.UsedHumanHand = true;
            }

            child.SoftEnemyHp[enemyIndex] = Math.Max(0, child.SoftEnemyHp[enemyIndex] - speculativeDamage);
            var transparentTarget = enemy.Powers.All(power => DamageTransparentTargetPowers.Contains(power.GetType().Name));
            if (candidate.GuaranteedDirectDamage && transparentTarget)
            {
                child.HardEnemyHp[enemyIndex] = Math.Max(0, child.HardEnemyHp[enemyIndex] - speculativeDamage);
                if (casterPartyIndex >= 0)
                    child.AttackHits[enemyIndex * context.Party.Count + casterPartyIndex] += candidate.Hits;
            }

            var delayed = candidate.Facts.Poison + candidate.Facts.Doom;
            if (delayed > 0)
                child.SoftEnemyHp[enemyIndex] = Math.Max(0, child.SoftEnemyHp[enemyIndex] - delayed * 0.6);

            var weakFirst = id is "Uppercut" or "Shockwave";
            var weakApplies = weakFirst && candidate.Facts.Weak > 0 && ConsumeDebuff(child, enemyIndex);
            var vulnerableApplies = candidate.Facts.Vulnerable > 0 && ConsumeDebuff(child, enemyIndex);
            if (!weakFirst) weakApplies = candidate.Facts.Weak > 0 && ConsumeDebuff(child, enemyIndex);
            if (vulnerableApplies && !HasPower(enemy, "VulnerablePower"))
            {
                child.AddedVulnerable |= 1UL << enemyIndex;
                var humanAttack = context.Human.AttackByEnemy[enemyIndex];
                if (humanAttack > 0)
                {
                    child.HumanSynergy += Math.Min(40, humanAttack * 0.5 * HumanConfidence);
                    child.UsedHumanHand = true;
                }
            }
            if (weakApplies && !HasPower(enemy, "WeakPower"))
                child.AddedWeak |= 1UL << enemyIndex;
            // Temporary (and permanent) enemy Strength loss stacks, so it is
            // accumulated rather than bit-flagged. Artifact consumes it like any
            // other debuff, and the amount only matters while the enemy attacks.
            if (candidate.Facts.StrengthDown > 0 && ConsumeDebuff(child, enemyIndex))
                child.EnemyStrengthDown[enemyIndex] += candidate.Facts.StrengthDown;
            if (casterPartyIndex >= 0 && id is "Flanking" or "Knockdown"
                && ConsumeDebuff(child, enemyIndex))
            {
                var amounts = id == "Flanking" ? child.Flanking : child.Knockdown;
                amounts[enemyIndex * context.Party.Count + casterPartyIndex] += id == "Flanking" ? 2
                    : (double)card.DynamicVars["KnockdownPower"].BaseValue;
            }
            if (id == "TagTeam" && casterPartyIndex >= 0 && ConsumeDebuff(child, enemyIndex))
                child.TagTeam[enemyIndex * (context.Party.Count + 1) + casterPartyIndex]++;

            var humanCanCover = context.Human.AttackByEnemy[enemyIndex] >= child.SoftEnemyHp[enemyIndex]
                && EnemyThreat(context, enemyIndex) <= 0;
            if (baseDamage > 0 && humanCanCover)
            {
                child.HumanSynergy -= Math.Min(18, baseDamage * 0.6);
                child.UsedHumanHand = true;
            }
        }

        if (card.Type == CardType.Attack)
        {
            // AfterCardPlayed, so the threshold attack itself never receives
            // Shuriken's newly gained Strength. TwinStrike counts only once.
            for (var relicIndex = 0; relicIndex < context.Imported.Relics.Length; relicIndex++)
            {
                if (!CombatSolverCardRelicProjection.IncrementCounter(child.Imported, context.Imported,
                    relicIndex, casterPartyIndex, card.Type)) continue;
                var relic = context.Imported.Relics[relicIndex];
                switch (relic.Kind)
                {
                    case CombatSolverCardRelicProjection.TriggerKind.Strength:
                        if (child.HardEnemyHp.Any(hp => hp > 0)) child.AddedStrength[casterPartyIndex] += relic.Amount; break;
                    case CombatSolverCardRelicProjection.TriggerKind.Dexterity:
                        if (child.HardEnemyHp.Any(hp => hp > 0)) child.AddedDexterity[casterPartyIndex] += relic.Amount; break;
                    case CombatSolverCardRelicProjection.TriggerKind.Energy:
                        child.Energy[candidate.PlayerIndex] += relic.Amount; break;
                    case CombatSolverCardRelicProjection.TriggerKind.Block:
                        AddProjectedBlock(child, casterPartyIndex, (double)Hook.ModifyBlock(card.CombatState!,
                            candidate.Player.Creature, relic.Amount, ValueProp.Unpowered, null, null, out _), context); break;
                }
            }
            for (var index = 0; index < context.Party.Count; index++)
            {
                if (context.Party[index] == candidate.Player || child.Sneaky[index] <= 0) continue;
                // Sneaky triggers once per card play, not once per hit, and grants unpowered block.
                AddProjectedBlock(child, index, Math.Max(0, (double)Hook.ModifyBlock(card.CombatState!,
                    context.Party[index].Creature, (decimal)child.Sneaky[index], ValueProp.Unpowered,
                    null, null, out _)), context);
            }
        }

        var targetPartyIndex = candidate.Move.Target is { IsPlayer: true } targetPlayer
            ? IndexOfCreature(context.Party, targetPlayer)
            : casterPartyIndex;
        if (targetPartyIndex >= 0 && !BotRegistry.IsBot(context.Party[targetPartyIndex].NetId))
        {
            var buffFit = candidate.Facts.Strength * Math.Max(1, context.Human.AttackCards) * 3
                + candidate.Facts.Dexterity * Math.Max(1, context.Human.BlockCards) * 2
                + candidate.Facts.Focus * 4;
            if (buffFit > 0)
            {
                child.HumanSynergy += buffFit * HumanConfidence;
                child.UsedHumanHand = true;
            }
        }

        var drawCredit = Math.Max(0, candidate.Facts.Draw) * 3 * recipients.Length;
        if (CombatResourceProjection.ModelsDraw(card))
        {
            // New legal actions carry their own value; do not double-credit exact draws.
            CombatResourceProjection.Draw(child.Resources, context.Resources, candidate.PlayerIndex,
                Math.Max(0, (int)Math.Ceiling(candidate.Facts.Draw)));
            drawCredit = 0;
        }
        if (id == "BattleTrance") child.Resources.NoDraw[candidate.PlayerIndex] = true;
        if (candidate.Facts.Draw > 0 && !CombatResourceProjection.ModelsDraw(card))
        {
            // A draw effect outside this layer may change hand size/order. Do
            // not subsequently pretend the old ordered prefix is still exact.
            foreach (var recipient in recipients.Where(index => index >= 0))
            {
                var bot = IndexOfPlayer(context.Bots, context.Party[recipient]);
                if (bot >= 0) child.Resources.Boundary[bot] = true;
            }
        }
        child.SetupValue += drawCredit
            + Math.Max(0, candidate.Facts.ImmediateEnergy) * 5 * recipients.Length;
        return child;
    }

    private static bool ConsumeDebuff(PlanNode node, int enemyIndex)
    {
        if (node.Artifact[enemyIndex] <= 0) return true;
        node.Artifact[enemyIndex]--;
        return false;
    }

    private static void AddProjectedBlock(PlanNode node, int recipient, double amount, PlanningContext context)
        => ProjectedBlockSharing.Add(node.ExtraBlock, node.Beacon, context.Party, recipient, amount);

    private static double ProjectDamage(Candidate candidate, Creature enemy, PlanNode node,
        PlanningContext context, int enemyIndex,
        int casterIndex, bool addedVulnerable, double teamMultiplier = 1)
    {
        // Stateful attacks retain their card-specific preview as a soft estimate.
        // Do not replace a calculated/conditional attack with its raw DamageVar.
        if (!candidate.GuaranteedDirectDamage)
            return candidate.Facts.DamagePerEnemy * (addedVulnerable ? 1.5 : 1) * teamMultiplier;
        var damage = candidate.Move.Card.DynamicVars.Values.OfType<DamageVar>().FirstOrDefault();
        var card = candidate.Move.Card;
        if (card.Type != CardType.Attack) return candidate.Facts.DamagePerEnemy;
        decimal raw;
        ValueProp props;
        if (card.GetType().Name == "BodySlam")
        {
            // CalculatedVarSpecRegistry reads simulated owner's block.
            raw = card.DynamicVars.CalculatedDamage.Calculate(enemy)
                + card.DynamicVars.ExtraDamage.BaseValue * (decimal)node.ExtraBlock[casterIndex];
            props = card.DynamicVars.CalculatedDamage.Props;
        }
        else if (card.GetType().Name == "GangUp")
        {
            var calculated = card.DynamicVars.CalculatedDamage;
            var additionalHits = Enumerable.Range(0, context.Party.Count).Where(index => index != casterIndex)
                .Sum(index => node.AttackHits[enemyIndex * context.Party.Count + index]);
            raw = calculated.Calculate(enemy) + card.DynamicVars.ExtraDamage.BaseValue * additionalHits;
            props = calculated.Props;
        }
        else if (damage is not null) { raw = damage.BaseValue; props = damage.Props; }
        else return candidate.Facts.DamagePerEnemy;
        var addedStrength = casterIndex >= 0 ? node.AddedStrength[casterIndex] : 0;
        if (casterIndex >= 0 && candidate.EnergyCost == 0) addedStrength += node.OneForAll[casterIndex];
        if (!props.IsPoweredAttack()) addedStrength = 0;
        var key = (card, enemy, raw + (decimal)addedStrength, props);
        if (!context.DamageCache.TryGetValue(key, out var perHit))
        {
            perHit = (double)Hook.ModifyDamage(candidate.Player.RunState, candidate.Move.Card.CombatState,
                enemy, candidate.Player.Creature, key.Item3,
                props, card, null, ModifyDamageHookType.All, CardPreviewMode.Normal, out _);
            context.DamageCache.Add(key, perHit);
            context.Statistics.DamageHookCalls++;
        }
        if (addedVulnerable) perHit *= 1.5;
        perHit *= teamMultiplier;
        return Math.Max(0, Math.Floor(perHit)) * candidate.Hits;
    }

    private static double SharedMultiplier(PlanNode node, PlanningContext context, int enemyIndex, Player attacker)
    {
        var multiplier = 1.0;
        for (var index = 0; index < context.Party.Count; index++)
        {
            if (context.Party[index] == attacker) continue;
            foreach (var (name, amounts) in new[] { ("FlankingPower", node.Flanking), ("KnockdownPower", node.Knockdown) })
            {
                var added = amounts[enemyIndex * context.Party.Count + index];
                if (added <= 0) continue;
                var existing = (double)context.Enemies[enemyIndex].Powers
                    .Where(power => power.GetType().Name == name && power.Applier == context.Party[index].Creature)
                    .Sum(power => power.Amount);
                // Live preview already includes existing instances; only apply the change.
                multiplier *= existing > 0 ? (existing + added) / existing : added;
            }
        }
        return multiplier;
    }

    private static double ProjectBlock(CardModel card, Creature recipient, Creature? target, double extraBase)
        => card.DynamicVars.Values.Sum(variable => variable switch
        {
            BlockVar block => (double)Hook.ModifyBlock(card.CombatState!, recipient,
                block.BaseValue + (decimal)extraBase, block.Props, card, null, out _),
            CalculatedBlockVar block => (double)Hook.ModifyBlock(card.CombatState!, recipient,
                block.Calculate(target ?? recipient) + (decimal)extraBase, block.Props, card, null, out _),
            _ => 0,
        });

    private static List<PlanNode> Retain(List<PlanNode> expanded, PlanningContext context)
    {
        var ordering = Comparer<PlanNode>.Create((left, right) => CompareFinal(left, right, context));
        var deduplicated = expanded.GroupBy(node => node, StateComparer.Instance)
            .Select(group => group.OrderBy(node => node, ordering).First())
            .ToList();
        var selected = deduplicated.OrderBy(node => node, ordering)
            .Take(BeamWidth).ToList();

        // Explicit diversity lanes prevent the aggregate Beam score from
        // dropping defense, confirmed kills, focus, setup, or human synergy.
        AddLane(selected, deduplicated.OrderBy(node => Evaluate(node, context).WeightedDeaths)
            .ThenBy(node => Evaluate(node, context).WeightedHpLoss).FirstOrDefault());
        AddLane(selected, deduplicated.OrderByDescending(node => Evaluate(node, context).ConfirmedKills)
            .ThenBy(node => Evaluate(node, context).HardEnemyHp).FirstOrDefault());
        AddLane(selected, deduplicated.OrderBy(node => Evaluate(node, context).FocusRemainingHp).FirstOrDefault());
        AddLane(selected, deduplicated.OrderByDescending(node => Evaluate(node, context).SetupValue).FirstOrDefault());
        AddLane(selected, deduplicated.OrderByDescending(node => Evaluate(node, context).HumanSynergy).FirstOrDefault());
        return selected;
    }

    private static void AddLane(List<PlanNode> selected, PlanNode? node)
    {
        if (node is not null && !selected.Contains(node))
            selected.Add(node);
    }

    private static int CompareFinal(PlanNode left, PlanNode right, PlanningContext context)
        => CompareMetrics(Evaluate(left, context), Evaluate(right, context));

    private static int CompareMetrics(Metrics left, Metrics right)
    {
        // Team outcome first: a wipe and the cost of the line decide, not whose
        // character died. Human deaths are already priced into TacticalCost.
        var comparison = left.TeamWiped.CompareTo(right.TeamWiped);
        if (comparison != 0) return comparison;
        comparison = right.ConfirmedVictory.CompareTo(left.ConfirmedVictory);
        if (comparison != 0) return comparison;
        comparison = TacticalCost(left).CompareTo(TacticalCost(right));
        if (comparison != 0) return comparison;
        comparison = left.WeightedDeaths.CompareTo(right.WeightedDeaths);
        if (comparison != 0) return comparison;
        comparison = right.ConfirmedKills.CompareTo(left.ConfirmedKills);
        if (comparison != 0) return comparison;
        comparison = left.RemainingThreat.CompareTo(right.RemainingThreat);
        if (comparison != 0) return comparison;
        comparison = left.HardEnemyHp.CompareTo(right.HardEnemyHp);
        if (comparison != 0) return comparison;
        comparison = left.SoftProjectedHpLoss.CompareTo(right.SoftProjectedHpLoss);
        if (comparison != 0) return comparison;
        comparison = left.SoftEnemyHp.CompareTo(right.SoftEnemyHp);
        if (comparison != 0) return comparison;
        comparison = left.FocusRemainingHp.CompareTo(right.FocusRemainingHp);
        if (comparison != 0) return comparison;
        comparison = right.HumanSynergy.CompareTo(left.HumanSynergy);
        if (comparison != 0) return comparison;
        comparison = right.SetupValue.CompareTo(left.SetupValue);
        if (comparison != 0) return comparison;
        comparison = right.IndividualValue.CompareTo(left.IndividualValue);
        if (comparison != 0) return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        return comparison != 0 ? comparison : right.UnspentEnergy.CompareTo(left.UnspentEnergy);
    }

    private static Metrics Evaluate(PlanNode node, PlanningContext context)
    {
        if (node.CachedMetrics is { } metrics)
        {
            context.Statistics.EvaluationCacheHits++;
            return metrics;
        }
        context.Statistics.Evaluations++;
        var result = EvaluateUncached(node, context);
        node.CachedMetrics = result;
        return result;
    }

    // Shared by retention and final selection. Unlike lexicographic HP loss,
    // a small nonfatal wound may be worth meaningful damage or modeled growth.
    // Unknown damage receives less credit and never becomes a confirmed kill.
    private static double TacticalCost(Metrics metrics)
        => metrics.TriageCost + metrics.HardEnemyHp * 0.25 + metrics.SoftEnemyHp * 0.10
            - metrics.SetupValue * 0.35 + metrics.FocusCost - metrics.BoundaryValue;

    private static Metrics EvaluateUncached(PlanNode node, PlanningContext context)
    {
        var weightedDeaths = 0;
        var weightedHpLoss = 0.0;
        var softHpLoss = 0.0;
        var humanDeaths = 0;
        var deathCount = 0;
        var triageCost = 0.0;
        var boundaryValue = 0.0;
        var boundary = context.TurnBoundary;
        // End-turn Plating can also trigger Beacon sharing. Apply it only to a
        // separate terminal copy, so it cannot inflate Mimic/DemonicShield now.
        var endingBlock = node.ExtraBlock;
        var fightContinues = node.HardEnemyHp.Any(hp => hp > 0);
        if (fightContinues && boundary.PlatingBlock.Any(amount => amount > 0))
        {
            endingBlock = (double[])node.ExtraBlock.Clone();
            for (var i = 0; i < context.Party.Count; i++)
                if (context.Party[i].Creature.CurrentHp - node.HpSpent[i]
                    + (context.Party[i] == context.AssistedPlayer ? context.AssistedHealing : 0) > 0)
                    ProjectedBlockSharing.Add(endingBlock, node.Beacon, context.Party, i, boundary.PlatingBlock[i]);
        }
        var doomed = new bool[context.Party.Count];
        for (var partyIndex = 0; partyIndex < context.Party.Count; partyIndex++)
        {
            var player = context.Party[partyIndex];
            var hardIncoming = 0.0;
            var softIncoming = 0.0;
            for (var enemyIndex = 0; enemyIndex < context.Enemies.Count; enemyIndex++)
            {
                var incoming = context.IncomingByEnemyAndPartyMember[enemyIndex, partyIndex];
                if ((node.AddedWeak & (1UL << enemyIndex)) != 0)
                    incoming = context.WeakIncoming[enemyIndex, partyIndex];
                // Strength loss removes damage from every remaining hit, so the
                // team does not have to block the damage it just deleted.
                if (node.EnemyStrengthDown[enemyIndex] > 0)
                    incoming = Math.Max(0, incoming - node.EnemyStrengthDown[enemyIndex] * context.EnemyHitCount[enemyIndex]);
                if (node.HardEnemyHp[enemyIndex] > 0)
                    hardIncoming += incoming;
                if (node.SoftEnemyHp[enemyIndex] > 0)
                {
                    softIncoming += incoming;
                }
            }

            var hpAfterCost = Math.Max(0, player.Creature.CurrentHp
                + (player == context.AssistedPlayer ? context.AssistedHealing : 0) - node.HpSpent[partyIndex]);
            var block = player.Creature.Block + endingBlock[partyIndex];
            var forcedDeath = false;
            foreach (var pit in context.Sandpits)
            {
                if (pit.Target != player.Creature) continue;
                var sourceIndex = IndexOfEnemy(context.Enemies, pit.Source);
                if (sourceIndex >= 0 && node.HardEnemyHp[sourceIndex] <= 0) continue;
                var escapes = pit.EscapeApplies ? node.Path.Count(index =>
                    context.Candidates[index].Player == player
                    && context.Candidates[index].Move.Card.GetType().Name == "FranticEscape") : 0;
                var remaining = pit.Remaining + escapes;
                forcedDeath |= remaining <= 1;
                triageCost += MonsterHazards.ReserveCost(remaining);
            }
            var next = TurnBoundaryProjection.Advance(hpAfterCost, block, hardIncoming,
                boundary.Strength[partyIndex] + node.AddedStrength[partyIndex], boundary.Ritual[partyIndex],
                node.DemonForm[partyIndex], boundary.Plating[partyIndex], node.Barricade[partyIndex]);
            if (fightContinues && next.Hp > 0 && !forcedDeath)
            {
                // Known carried resources receive bounded option value, never
                // confirmed next-turn damage or a promised future player action.
                var growthUse = boundary.AttackCapacity[partyIndex];
                boundaryValue += Math.Min(30, next.Block) * 0.25
                    + Math.Min(12, Math.Max(0, next.Strength - boundary.Strength[partyIndex] - node.AddedStrength[partyIndex]))
                        * growthUse * 0.35;
            }
            var hardLoss = node.HpSpent[partyIndex] + (forcedDeath ? hpAfterCost : Math.Min(hpAfterCost, Math.Max(0, hardIncoming - block)));
            var humanWeight = BotRegistry.IsBot(player.NetId) ? 1.0 : 1.5;
            weightedHpLoss += hardLoss;
            if (forcedDeath || hpAfterCost <= 0 || hardIncoming - block >= hpAfterCost)
            {
                weightedDeaths++;
                doomed[partyIndex] = true; deathCount++;
                if (!BotRegistry.IsBot(player.NetId)) humanDeaths++;
                // Every member's death is priced the same: the team outcome is
                // what matters, not whose character died.
                triageCost += BotDeathBasePenalty + player.Creature.MaxHp * BotDeathMaxHpPenalty;
            }
            // Current HP loss is costlier near death; healthy chip damage is
            // expendable, but repeatedly leaving a teammate at 1 HP is not free.
            // WeightedHpLoss remains the actual total damage for reporting.
            var healthDeficit = 1 - Math.Clamp(hpAfterCost / Math.Max(1, player.Creature.MaxHp), 0, 1);
            triageCost += hardLoss * (1 + 2 * healthDeficit * healthDeficit);

            var humanBlockOption = context.Human.BlockByPartyMember[partyIndex] * HumanConfidence;
            softHpLoss += (node.HpSpent[partyIndex]
                + (forcedDeath ? hpAfterCost : Math.Min(hpAfterCost, Math.Max(0, softIncoming - block - humanBlockOption)))) * humanWeight;
        }

        var hardEnemyHp = node.HardEnemyHp.Sum();
        var softEnemyHp = node.SoftEnemyHp.Sum();
        var confirmedKills = node.HardEnemyHp.Count(hp => hp <= 0);
        var remainingThreat = Enumerable.Range(0, context.Enemies.Count)
            .Where(index => node.HardEnemyHp[index] > 0)
            .Sum(index => EnemyThreat(context, index));
        var focusIndex = context.StrategicTarget is not null
            ? IndexOfEnemy(context.Enemies, context.StrategicTarget)
            : -1;
        var focusHp = focusIndex >= 0 ? node.SoftEnemyHp[focusIndex] : softEnemyHp;
        return new Metrics(
            humanDeaths,
            deathCount == context.Party.Count,
            triageCost,
            context.AssistedPlayer is not null && !doomed[IndexOfPlayer(context.Party, context.AssistedPlayer)],
            weightedDeaths,
            confirmedKills == context.Enemies.Count,
            weightedHpLoss,
            confirmedKills,
            remainingThreat,
            hardEnemyHp,
            softHpLoss,
            softEnemyHp,
            focusHp,
            node.SetupValue + node.FutureGrowth.Where((_, index) => !doomed[index]).Sum(),
            node.HumanSynergy,
            node.Path.Sum(index =>
            {
                var candidate = context.Candidates[index];
                if (candidate.Move.Card.GetType().Name is "DemonForm" or "Barricade" or "FranticEscape" or "FeelNoPain" or "DarkEmbrace") return 0;
                var recipient = candidate.Move.Target is { IsPlayer: true } target
                    ? IndexOfCreature(context.Party, target) : IndexOfPlayer(context.Party, candidate.Player);
                return recipient >= 0 && doomed[recipient] ? 0 : candidate.Move.Score;
            }),
            node.Path.Count,
            node.Energy.Sum(),
            context.Enemies.Count > 1 && focusIndex >= 0
                ? node.SoftEnemyHp[focusIndex] * (context.FocusTarget.HasValue ? 0.30 : 0.18) : 0,
            boundaryValue);
    }

    private static PlanNode CreateRoot(PlanningContext context)
        => new()
        {
            Energy = context.Bots.Select(bot => Math.Max(0, bot.PlayerCombatState?.Energy ?? 0)).ToArray(),
            Stars = context.Bots.Select(bot => Math.Max(0, bot.PlayerCombatState?.Stars ?? 0)).ToArray(),
            AddedStrength = new double[context.Party.Count],
            AddedDexterity = new double[context.Party.Count],
            Artifact = context.Enemies.Select(enemy => (int)enemy.Powers.Where(p => p.GetType().Name == "ArtifactPower").Sum(p => p.Amount)).ToArray(),
            Flanking = new double[context.Enemies.Count * context.Party.Count],
            Knockdown = new double[context.Enemies.Count * context.Party.Count],
            TagTeam = ExistingTagTeam(context),
            AttackHits = new int[context.Enemies.Count * context.Party.Count],
            OneForAll = new double[context.Party.Count],
            Sneaky = context.Party.Select(player => (double)player.Creature.Powers
                .Where(power => power.GetType().Name == "SneakyPower").Sum(power => power.Amount)).ToArray(),
            Beacon = context.Party.Select(player => HasPower(player.Creature, "BeaconOfHopePower")).ToArray(),
            ActionsByBot = new int[context.Bots.Count],
            HardEnemyHp = context.Enemies.Select(EffectiveHp).ToArray(),
            SoftEnemyHp = context.Enemies.Select(EffectiveHp).ToArray(),
            ExtraBlock = context.Party.Select(p => p == context.AssistedPlayer ? context.AssistedBlock : 0).ToArray(),
            HpSpent = new double[context.Party.Count],
            FutureGrowth = new double[context.Party.Count],
            EnemyStrengthDown = new double[context.Enemies.Count],
            Resources = context.Resources.Root.Fork(),
            Imported = context.Imported.Root.Fork(),
            DemonForm = (double[])context.TurnBoundary.DemonForm.Clone(),
            Barricade = (bool[])context.TurnBoundary.Barricade.Clone(),
        };

    private static int[] ExistingTagTeam(PlanningContext context)
    {
        var amounts = new int[context.Enemies.Count * (context.Party.Count + 1)];
        for (var enemy = 0; enemy < context.Enemies.Count; enemy++)
        foreach (var power in context.Enemies[enemy].Powers.Where(power => power.GetType().Name == "TagTeamPower"))
        {
            var source = power.Applier is null ? -1 : IndexOfCreature(context.Party, power.Applier);
            if (source < 0) source = context.Party.Count;
            amounts[enemy * (context.Party.Count + 1) + source] += Math.Max(0, (int)power.Amount);
        }
        return amounts;
    }

    private static HumanPotential BuildHumanPotential(IReadOnlyList<Player> party, IReadOnlyList<Creature> enemies,
        IReadOnlyList<Player>? recipients = null)
    {
        recipients ??= party;
        var result = HumanPotential.Empty(enemies.Count, recipients.Count);
        var attackCards = 0;
        var blockCards = 0;
        // Callers pass the exact shooter set (humans for the human potential, the
        // bots for the kernel's "can we finish it alone" probe).
        foreach (var human in party.Where(player => player.PlayerCombatState is { Phase: PlayerTurnPhase.Play }
            && !CombatManager.Instance.IsPlayerReadyToEndTurn(player)))
        {
            var energy = Math.Max(0, human.PlayerCombatState?.Energy ?? 0);
            var legal = BotBrain.LegalCombatMoves(human, BotDifficulty.Genius);
            attackCards += legal.Select(move => move.Card).Distinct()
                .Count(card => card.Type == CardType.Attack);
            blockCards += legal.Select(move => move.Card).Distinct()
                .Count(card => card.GainsBlock);

            for (var enemyIndex = 0; enemyIndex < enemies.Count; enemyIndex++)
            {
                var enemy = enemies[enemyIndex];
                var options = legal.Where(move => move.Card.Type == CardType.Attack
                    && (ReferenceEquals(move.Target, enemy) || move.Card.TargetType == TargetType.AllEnemies))
                    .GroupBy(move => move.Card)
                    .Select(group => group.OrderByDescending(move =>
                        GeniusCombatStrategy.Analyze(move.Card, move.Target, energy, enemies.Count).TotalDamage).First())
                    .Where(move => !move.Card.EnergyCost.CostsX && !move.Card.HasStarCostX)
                    .Select(move =>
                    {
                        var facts = GeniusCombatStrategy.Analyze(move.Card, move.Target, energy, enemies.Count);
                        var damage = move.Card.TargetType == TargetType.AllEnemies ? facts.DamagePerEnemy : facts.TotalDamage;
                        return (Cost: Math.Max(0, facts.EnergyCost), Damage: damage, facts.Vulnerable);
                    }).ToList();
                result.AttackByEnemy[enemyIndex] += Knapsack(options.Select(option => (option.Cost, option.Damage)), energy);
                result.CanApplyVulnerable[enemyIndex] |= legal.Any(move =>
                {
                    if (!(ReferenceEquals(move.Target, enemy) || move.Card.TargetType == TargetType.AllEnemies)
                        || move.Card.EnergyCost.CostsX || move.Card.HasStarCostX)
                        return false;
                    var facts = GeniusCombatStrategy.Analyze(move.Card, move.Target, energy, enemies.Count);
                    return facts.Vulnerable > 0 && facts.EnergyCost <= energy;
                });
            }

            for (var partyIndex = 0; partyIndex < recipients.Count; partyIndex++)
            {
                var recipient = recipients[partyIndex].Creature;
                var options = legal.Where(move => move.Card.GainsBlock
                    && (move.Card.TargetType == TargetType.AllAllies
                        || (move.Card.GetType().Name == "Mimic" ? ReferenceEquals(recipient, human.Creature)
                            : ReferenceEquals(move.Target, recipient)
                              || (move.Target is null && ReferenceEquals(recipient, human.Creature)))))
                    .GroupBy(move => move.Card)
                    .Select(group => group.First())
                    .Where(move => !move.Card.EnergyCost.CostsX && !move.Card.HasStarCostX)
                    .Select(move =>
                    {
                        var facts = GeniusCombatStrategy.Analyze(move.Card, move.Target, energy, enemies.Count);
                        return (Cost: Math.Max(0, facts.EnergyCost), Value: CombatAssessment.BlockFor(move.Card, recipient, move.Target));
                    });
                result.BlockByPartyMember[partyIndex] += Knapsack(options, energy);
            }
        }
        return result with { AttackCards = attackCards, BlockCards = blockCards };
    }

    // Soft per-enemy damage estimate for an explicit shooter set. The kernel team
    // score uses it to judge whether humans (or bots) can still finish a target.
    internal static double[] AttackPotential(IReadOnlyList<Player> shooters, IReadOnlyList<Creature> enemies)
        => BuildHumanPotential(shooters, enemies).AttackByEnemy;

    private static double Knapsack(IEnumerable<(int Cost, double Value)> options, int energy)
    {
        var values = new double[energy + 1];
        foreach (var (cost, value) in options.Where(option => option.Cost <= energy && option.Value > 0))
            for (var budget = energy; budget >= cost; budget--)
                values[budget] = Math.Max(values[budget], values[budget - cost] + value);
        return values.Max();
    }

    private static double[,] BuildIncoming(IReadOnlyList<Creature> enemies, IReadOnlyList<Player> party)
    {
        var incoming = new double[enemies.Count, party.Count];
        for (var enemyIndex = 0; enemyIndex < enemies.Count; enemyIndex++)
        for (var partyIndex = 0; partyIndex < party.Count; partyIndex++)
            incoming[enemyIndex, partyIndex] = CombatAssessment.FromEnemy(enemies[enemyIndex], party[partyIndex].Creature);
        return incoming;
    }

    private static int[] BuildEnemyHitCount(IReadOnlyList<Creature> enemies)
    {
        var hits = new int[enemies.Count];
        for (var index = 0; index < enemies.Count; index++)
        {
            try
            {
                hits[index] = enemies[index].Monster?.NextMove.Intents.OfType<AttackIntent>().Sum(intent => intent.Repeats) ?? 0;
            }
            catch
            {
                // A move whose damage cannot be read yet contributes no reduction.
                hits[index] = 0;
            }
        }
        return hits;
    }

    private static double[,] BuildWeakIncoming(IReadOnlyList<Creature> enemies, IReadOnlyList<Player> party)
    {
        var incoming = BuildIncoming(enemies, party);
        for (var enemy = 0; enemy < enemies.Count; enemy++)
        for (var recipient = 0; recipient < party.Count; recipient++)
        {
            // Unknown incoming-damage powers keep the original conservative estimate.
            if (enemies[enemy].Powers.All(p => DamageTransparentTargetPowers.Contains(p.GetType().Name))
                && party[recipient].Creature.Powers.All(p => ChainStablePowers.Contains(p.GetType().Name)))
                incoming[enemy, recipient] = CombatAssessment.AfterWeakUpperBound(enemies[enemy], party[recipient].Creature);
        }
        return incoming;
    }

    private static IEnumerable<int> AffectedEnemies(Candidate candidate, PlanningContext context)
    {
        if (candidate.Move.Card.TargetType == TargetType.AllEnemies)
            return Enumerable.Range(0, context.Enemies.Count);
        if (candidate.Move.Target is { IsEnemy: true } target)
        {
            var index = IndexOfEnemy(context.Enemies, target);
            return index >= 0 ? new[] { index } : Array.Empty<int>();
        }
        return Array.Empty<int>();
    }

    private static int IndexOfPlayer(IReadOnlyList<Player> players, Player player)
    {
        for (var i = 0; i < players.Count; i++)
            if (ReferenceEquals(players[i], player)) return i;
        return -1;
    }

    private static int IndexOfEnemy(IReadOnlyList<Creature> enemies, Creature target)
    {
        for (var i = 0; i < enemies.Count; i++)
            if (ReferenceEquals(enemies[i], target)) return i;
        return -1;
    }

    private static int IndexOfCreature(IReadOnlyList<Player> players, Creature creature)
    {
        for (var i = 0; i < players.Count; i++)
            if (ReferenceEquals(players[i].Creature, creature)) return i;
        return -1;
    }

    private static double EnemyThreat(PlanningContext context, int enemyIndex)
    {
        var threat = 0.0;
        for (var partyIndex = 0; partyIndex < context.Party.Count; partyIndex++)
        {
            var weight = BotRegistry.IsBot(context.Party[partyIndex].NetId) ? 1.0 : 1.5;
            threat += context.IncomingByEnemyAndPartyMember[enemyIndex, partyIndex] * weight;
        }
        return threat;
    }

    private static bool HasPower(Creature creature, string typeName)
        => creature.Powers.Any(power => power.Amount > 0 && power.GetType().Name == typeName);

    private static double EffectiveHp(Creature creature)
        => Math.Max(0, creature.CurrentHp + creature.Block);

    // Preserve the existing grouping precision and stable enumeration order.
    // Hash collisions are resolved by structural equality, never by hash alone.
    private sealed class StateComparer : IEqualityComparer<PlanNode>
    {
        internal static readonly StateComparer Instance = new();
        private static bool Same<T>(T[] a, T[] b) where T : IEquatable<T>
            => a.AsSpan().SequenceEqual(b);
        private static bool Rounded(double[] a, double[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (!Math.Round(a[i]).Equals(Math.Round(b[i]))) return false;
            return true;
        }
        public bool Equals(PlanNode? a, PlanNode? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            return a.UsedCards == b.UsedCards && a.AddedVulnerable == b.AddedVulnerable && a.AddedWeak == b.AddedWeak
                && Same(a.Energy, b.Energy) && Same(a.Stars, b.Stars) && Same(a.ActionsByBot, b.ActionsByBot)
                && Same(a.AddedStrength, b.AddedStrength) && Same(a.AddedDexterity, b.AddedDexterity)
                && Same(a.Artifact, b.Artifact) && Same(a.Flanking, b.Flanking) && Same(a.Knockdown, b.Knockdown)
                && Same(a.TagTeam, b.TagTeam) && Same(a.AttackHits, b.AttackHits)
                && Same(a.OneForAll, b.OneForAll) && Same(a.Sneaky, b.Sneaky) && Same(a.Beacon, b.Beacon)
                && Rounded(a.HardEnemyHp, b.HardEnemyHp) && Rounded(a.SoftEnemyHp, b.SoftEnemyHp)
                && Rounded(a.ExtraBlock, b.ExtraBlock) && Rounded(a.HpSpent, b.HpSpent)
                && Same(a.FutureGrowth, b.FutureGrowth) && Rounded(a.EnemyStrengthDown, b.EnemyStrengthDown)
                && a.Resources.Available == b.Resources.Available && a.Resources.Discarded == b.Resources.Discarded
                && a.Resources.Exhausted == b.Resources.Exhausted && a.Resources.PowersPlayed == b.Resources.PowersPlayed
                && Same(a.Resources.HandCount, b.Resources.HandCount) && Same(a.Resources.DrawCursor, b.Resources.DrawCursor)
                && Same(a.Resources.NoDraw, b.Resources.NoDraw) && Same(a.Resources.Corruption, b.Resources.Corruption)
                && Same(a.Resources.Boundary, b.Resources.Boundary)
                && a.Imported.Same(b.Imported)
                && Same(a.DemonForm, b.DemonForm) && Same(a.Barricade, b.Barricade);
        }
        private static void Add<T>(ref HashCode hash, T[] values)
        {
            hash.Add(values.Length);
            foreach (var value in values) hash.Add(value);
        }
        private static void AddRounded(ref HashCode hash, double[] values)
        {
            hash.Add(values.Length);
            foreach (var value in values) hash.Add(Math.Round(value));
        }
        public int GetHashCode(PlanNode node)
        {
            var hash = new HashCode();
            hash.Add(node.UsedCards); hash.Add(node.AddedVulnerable); hash.Add(node.AddedWeak);
            Add(ref hash, node.Energy); Add(ref hash, node.Stars); Add(ref hash, node.ActionsByBot);
            Add(ref hash, node.AddedStrength); Add(ref hash, node.AddedDexterity); Add(ref hash, node.Artifact);
            Add(ref hash, node.Flanking); Add(ref hash, node.Knockdown); Add(ref hash, node.TagTeam);
            Add(ref hash, node.AttackHits); Add(ref hash, node.OneForAll); Add(ref hash, node.Sneaky); Add(ref hash, node.Beacon);
            AddRounded(ref hash, node.HardEnemyHp); AddRounded(ref hash, node.SoftEnemyHp);
            AddRounded(ref hash, node.ExtraBlock); AddRounded(ref hash, node.HpSpent); Add(ref hash, node.FutureGrowth);
            AddRounded(ref hash, node.EnemyStrengthDown);
            hash.Add(node.Resources.Available); hash.Add(node.Resources.Discarded); hash.Add(node.Resources.Exhausted); hash.Add(node.Resources.PowersPlayed);
            Add(ref hash, node.Resources.HandCount); Add(ref hash, node.Resources.DrawCursor);
            Add(ref hash, node.Resources.NoDraw); Add(ref hash, node.Resources.Corruption); Add(ref hash, node.Resources.Boundary);
            Add(ref hash, node.DemonForm); Add(ref hash, node.Barricade);
            node.Imported.AddHash(ref hash);
            return hash.ToHashCode();
        }
    }

    private static double DecisionPriority(Candidate first, Metrics root, Metrics best, PlanNode plan)
    {
        var priority = first.Move.Score + 80;
        if (best.WeightedDeaths < root.WeightedDeaths) priority += 2400;
        if (best.ConfirmedVictory) priority += 1900;
        priority += Math.Max(0, best.ConfirmedKills - root.ConfirmedKills) * 720;
        priority += Math.Max(0, root.WeightedHpLoss - best.WeightedHpLoss) * 24;
        priority += Math.Min(360, Math.Max(0, root.HardEnemyHp - best.HardEnemyHp) * 4);
        priority += Math.Min(220, Math.Max(0, plan.HumanSynergy) * 4);
        return priority;
    }
}
