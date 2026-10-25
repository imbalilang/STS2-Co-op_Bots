using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Potions.OnUse;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    public static long LiveRevision => KernelIsolation.LiveRevision;

    /// <summary>
    /// The simulated slot holding this branch's clone of a potion, or -1.
    /// </summary>
    /// <remarks>
    /// PotionExecutionSupport.Prepare takes a SLOT (it consumes the bottle by index), and
    /// our branch API carries the PotionModel instead. Scanning the slots keeps the
    /// upstream path intact rather than re-deriving the consume from the model.
    /// </remarks>
    private int SimulatedPotionSlot(PotionModel simulated)
    {
        var owner = simulated.Owner;
        // Native Player.GetPotionSlotIndex scans the slot list, including holes.
        // Counting bottles instead hid slot 2 after slot 0 was spent: V10 live
        // predicted a death but could never model the remaining CureAll there.
        for (var slot = 0; slot < owner.PotionSlots.Count; slot++)
            if (ReferenceEquals(Combat.GetPotionAtSlot(owner, slot), simulated)) return slot;
        return -1;
    }

    /// <summary>
    /// Per-turn attack count for one creature in THIS branch.
    /// </summary>
    /// <remarks>
    /// Test-only probe, and it exists because the ENEMY-side per-turn reset is invisible to
    /// the state text: enemies render as combat_id/hp/max_hp/block/move, and their per-turn
    /// counters appear nowhere. Without this there is nothing the suite could assert about
    /// the reset, so removing it again would go unnoticed — which is exactly how the
    /// player-side omission survived as long as it did.
    /// </remarks>
    internal int CreatureAttacksThisTurnForTesting(Creature creature)
        => Combat.GetCreatureAttacksThisTurn(creature);

    /// <summary>
    /// The live board's visible-state text for one player, in the same format
    /// <see cref="StateText"/> produces for a branch. Exposed so a caller can compare a
    /// plan's turn-boundary prediction against reality without having to name the
    /// vendor's ContinuationStamp itself.
    /// </summary>
    public static string CaptureLiveStateText(CombatState state, Player player)
        => ContinuationStamp.CaptureLive(state, player).StateText;

    /// <summary>
    /// THE table's visible state as one text: every seat, labelled by NetId, in party
    /// order. This is the form every script check compares, and it exists because the
    /// per-seat form cannot be compared across seats.
    /// </summary>
    /// <remarks>
    /// The per-seat renderer answers "what does THIS seat see". A script check needs
    /// "what does the TABLE look like", because the two sides of a comparison are not
    /// always produced by the same seat. The turn-boundary check did exactly that: it
    /// compared a text rendered from the seat that ENDED the turn against the live text
    /// of the seat that played the NEXT action. On a four-seat table those differ in hp,
    /// block, energy, hand and gold, so every crossing was refused — with no drift, no
    /// differing field, just `boundary=expected` and a dropped script
    /// (SEAPUNK_WEAK, 2026-09-20). Labelling each seat inside ONE string removes the
    /// question rather than answering it: there is a single text, both sides build it
    /// the same way, and no caller can pick the wrong viewpoint because it never picks
    /// one at all.
    /// </remarks>
    public static string CaptureLivePartyText(CombatState state)
        => PartyText(state.Players, player => CaptureLiveStateText(state, player));

    /// <summary>
    /// The branch's counterpart of <see cref="CaptureLivePartyText"/>: same seats, same
    /// order, same labels, so the two are directly comparable.
    /// </summary>
    public string PartyStateText()
    {
        // The live root supplies the party ORDER on both sides. Without it a branch would
        // have to invent an order, and an order difference would read as a state
        // difference — the same class of bug this method exists to remove.
        if (liveRoot is null) throw new InvalidOperationException("Branch has no live root to order the party by.");
        return PartyText(liveRoot.Players, StateText);
    }

    /// <summary>
    /// The party in the LIVE ROOT's order — the same order <see cref="PartyStateText"/> uses.
    /// A per-seat reading (post-combat HP, say) has to line up with every other per-seat
    /// reading of the same fight; an order difference would silently reindex it. Empty for a
    /// branch with no live root, which is the case in fixtures.
    /// </summary>
    public IReadOnlyList<Player> Party => liveRoot?.Players ?? Array.Empty<Player>();

    private static string PartyText(IEnumerable<Player> players, Func<Player, string> render)
    {
        var text = new StringBuilder();
        foreach (var player in players)
            text.Append('p').Append(player.NetId).Append('{').Append(render(player)).Append("}\n");
        return text.ToString();
    }

    /// <summary>
    /// Resolves a planned card against the LIVE hand by identity — state key plus
    /// occurrence — the way upstream's FindCardForDeployment resolves against the hand it
    /// is deploying into.
    /// </summary>
    /// <remarks>
    /// A plan must never depend on holding a CardModel INSTANCE. Generated cards (Shiv,
    /// Mirage…) are fresh simulated objects whose Original is a new CardModel, and
    /// PredictedCard.References is plain reference equality — so `Hand.Cards.Contains(card)`
    /// matched nothing for them and every route through such a card was reported as
    /// `card-left-hand`.
    /// </remarks>
    public static CardModel? FindLiveCardByKey(Player player, string stateKey, int occurrence)
    {
        if (string.IsNullOrEmpty(stateKey)) return null;
        var hand = player.PlayerCombatState?.Hand.Cards;
        if (hand is null) return null;
        var seen = 0;
        foreach (var card in hand)
        {
            if (!string.Equals(CardChoiceSupport.ChoiceCardKey(card), stateKey, StringComparison.Ordinal))
                continue;
            if (seen++ == occurrence) return card;
        }
        return null;
    }

    /// <summary>
    /// Deployment resolution for one planned step: key+occurrence first, then — only for
    /// actions that recorded no key — the plain instance. When a key WAS recorded, an
    /// instance whose live state no longer matches it is refused
    /// (<c>card-state-changed</c>) instead of being played.
    /// </summary>
    /// <remarks>
    /// P0-SIM-2 (2026-10-05 false VICTORY). The instance fallback existed for generated
    /// cards whose planned model is never in the live hand. But it also silently accepted
    /// a card whose mutable state had moved since verification (upgrade, cost change,
    /// enchantment, affliction): the object identity matched while the semantics the plan
    /// was verified against no longer did. A step is only the verified step when both the
    /// object and its state key match.
    /// </remarks>
    public static CardModel? ResolvePlannedCard(Player player, CardModel? planned,
        string stateKey, int occurrence, out string refusal)
    {
        var card = FindLiveCardByKey(player, stateKey, occurrence);
        if (card is null && planned is not null
            && player.PlayerCombatState?.Hand.Cards.Contains(planned) == true)
        {
            if (string.IsNullOrEmpty(stateKey))
            {
                refusal = "";
                return planned;
            }
            var liveKey = CardChoiceSupport.ChoiceCardKey(planned);
            if (!string.Equals(liveKey, stateKey, StringComparison.Ordinal))
            {
                refusal = $"card-state-changed:{planned.Id.Entry} planned={stateKey} live={liveKey}";
                return null;
            }
            card = planned;
        }
        refusal = card is null ? "card-left-hand" : "";
        return card;
    }

    // Hand exposes stable originals so Play can resolve identity across forks.
    // Action keys must instead describe the branch's mutable preview: upgrades,
    // cost changes and afflictions may have changed since that original was captured.
    internal string CardStateKey(CardModel source)
        => simulator.State.FindCard(source) is { } predicted
            ? CardChoiceSupport.ChoiceCardKey(predicted)
            : CardChoiceSupport.ChoiceCardKey(source);

    /// <summary>
    /// The same identity lookup against THIS branch's own simulated hand, for replay.
    /// </summary>
    /// <remarks>
    /// Replay cannot use the live hand, and it cannot use the planned instance either —
    /// that is a simulated object Play's FindCard will not match. Resolving by identity and
    /// handing Play the simulated instance's Original is what makes `FindCard` succeed, so
    /// one generated card no longer empties the step sentinel for the whole path.
    /// </remarks>
    internal PredictedCard? FindSimulatedCardByKey(Player player, string stateKey, int occurrence)
    {
        if (string.IsNullOrEmpty(stateKey)) return null;
        var hand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
        var seen = 0;
        foreach (var predicted in hand)
        {
            if (!string.Equals(CardChoiceSupport.ChoiceCardKey(predicted), stateKey, StringComparison.Ordinal))
                continue;
            if (seen++ == occurrence) return predicted;
        }
        return null;
    }

    /// <summary>
    /// Compares a live board against a state text this branch produced earlier and
    /// returns the first differing field, or null when they match. Public so a caller
    /// can report WHICH field drifted instead of only that something did — the whole
    /// reason the old cross-turn check was hard to act on is that it could only say
    /// "the plan no longer applies".
    /// </summary>
    public static string? FirstLiveDifference(CombatState state, string expected)
        => LiveDifferences(state, expected, 1);

    public static string? LiveDifferences(CombatState state, string expected,
        int maximumDifferences = int.MaxValue)
    {
        string actual;
        try { actual = CaptureLivePartyText(state); }
        catch (Exception error) { return $"live-capture-failed {error.GetType().Name}"; }
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return null;
        return string.Join(" | ", new ContinuationStamp(expected)
            .DescribeDifferences(new ContinuationStamp(actual), maximumDifferences));
    }
    public static string CaptureLiveStamp(CombatState state) => $"{state.RoundNumber}/{state.CurrentSide}\n" +
        string.Join("\n", state.Players.Select(p => $"{p.NetId}/{p.PlayerCombatState?.Phase}:" +
            ContinuationStamp.CaptureLive(state, p).StateText));

    /// <summary>
    /// The same fingerprint, but only over the players that will act. A human
    /// merely ending their turn changes their own phase and nothing the team can
    /// see — same hands, same energy, same intents — so a verdict reached before
    /// that still holds afterwards; the full stamp would hide that.
    /// </summary>
    public static string CaptureBotStamp(CombatState state, IEnumerable<Player> actors) =>
        $"{state.RoundNumber}/{state.CurrentSide}\n" +
        string.Join("\n", actors.Select(p => $"{p.NetId}:" + ContinuationStamp.CaptureLive(state, p).StateText));
    /// <summary>
    /// True when the last action resolved with at least one uncompensated
    /// environmental risk (an enemy power or relic hook we do not model). The
    /// action stays usable, but its result is an estimate: callers must not treat
    /// it as a verified kill, rescue or death.
    /// </summary>
    public bool LastActionHadEnvironmentalRisk { get; private set; }

    /// <summary>
    /// WHICH uncompensated prediction gaps made the last action an estimate, or "" when it
    /// was exact. Carried beside the bool because the bit alone cannot be acted on: an
    /// end-turn is refused on any risky path, and without the names there is no way to tell
    /// which card or hook to model next.
    /// </summary>
    internal string LastActionRiskDetail { get; private set; } = "";

    /// <summary>
    /// Names the gaps that made an action an estimate: everything that is neither the
    /// played card's own OnPlay (which would have been a hard boundary) nor potion OnUse.
    /// </summary>
    private static string DescribeUncompensated(
        IReadOnlyList<PredictionGap> gaps, IReadOnlyList<PredictionGap> blocking)
        => gaps.Count == 0 || gaps.Count == blocking.Count
            ? ""
            : string.Join(";", gaps.Where(gap => !blocking.Contains(gap))
                .Select(gap => $"{gap.SourceId}.{gap.Method}:{gap.Reason}"));

    /// <summary>Completed player-turn segments, including extra turns, inside this branch.</summary>
    public int RoundsAdvanced { get; private set; }

    /// <summary>
    /// Latest simulated seat turn, used for search progress. Extra turns can give
    /// seats different native TurnNumbers; continuation text renders each seat's
    /// own simulated value rather than this aggregate or mutable live state.
    /// </summary>
    public int TurnNumber => Combat.Players.Select(Combat.GetPlayerTurnNumber).DefaultIfEmpty(rootTurnNumber).Max();

    private string? failedBoundary;
    private readonly CombatPredictionSimulator simulator;
    private readonly HashSet<uint> processedDeaths;
    // Fallback for an empty captured party; player turn numbers live in Combat.
    private readonly int rootTurnNumber;
    // The live state this branch descends from. Only the diagnostic state-text
    // fingerprint needs it: the intent forecast is built once from the root, the
    // same way the vendored solver builds it for every branch.
    private readonly CombatState? liveRoot;
    private SimulatedCombatState Combat => (SimulatedCombatState)simulator.State.CombatState;

    /// <summary>
    /// This branch's simulator, exposed so CombatSolver's evaluator can score it.
    ///
    /// Its state evaluation takes the simulator as a PARAMETER rather than reading its
    /// own field — it only uses its bound player to pick the perspective, derive the
    /// creature and then read everything from the simulator it was handed. That is what
    /// makes the join possible: our joint multi-actor expansion produces the state, and
    /// their evaluator prices it. See CombatSolverRoute.
    /// </summary>
    internal CombatPredictionSimulator Simulator => simulator;
    // Physical potion identity is captured once. Vendor FindPotion matches only
    // owner+model ID, which aliases duplicate bottles. Forkable potion slots
    // preserve their captured model references; a replacement at the same slot
    // must not resurrect an already consumed original. Live removal is harmless
    // to an older frozen root because neither lookup reads the live belt again.
    private IReadOnlyDictionary<PotionModel, (int Slot, PotionModel Captured)> capturedPotions
        = new Dictionary<PotionModel, (int, PotionModel)>();
    private sealed record PotionInventory(int Capacity, bool CanUse, PotionModel[] Originals,
        HashSet<PotionModel> Usable);
    // Native usability can invoke run hooks. Evaluate it on Capture's game
    // thread, once; a frozen worker must never enumerate a changing live belt.
    private IReadOnlyDictionary<Player, PotionInventory> potionInventories = new Dictionary<Player, PotionInventory>();
    private IReadOnlyDictionary<PotionModel, IReadOnlyDictionary<string, decimal>> potionValues
        = new Dictionary<PotionModel, IReadOnlyDictionary<string, decimal>>();
    private IReadOnlyDictionary<PotionModel, IReadOnlyDictionary<Creature, bool>> potionTargetRules
        = new Dictionary<PotionModel, IReadOnlyDictionary<Creature, bool>>();
    private PotionModel? FindCapturedPotion(PotionModel original)
        => capturedPotions.TryGetValue(original, out var entry)
            && ReferenceEquals(Combat.GetPotionAtSlot(original.Owner, entry.Slot), entry.Captured)
                ? entry.Captured : null;
    private KernelSession(CombatPredictionSimulator simulator, HashSet<uint> deaths,
        CombatState? liveRoot = null, int rootTurnNumber = 0)
    { this.simulator = simulator; processedDeaths = deaths; this.liveRoot = liveRoot; this.rootTurnNumber = rootTurnNumber; }

    /// <summary>
    /// First difference per seat between the simulator's projection of the freshly
    /// captured board and the live board itself. Empty means the capture reproduced
    /// the live state exactly — the precondition every downstream reuse rule assumes
    /// when it treats a searched script as executable. Non-empty does NOT make the
    /// branch unusable; it says a script searched from here cannot be promised to
    /// replay, which is exactly the fact that used to be discovered several actions
    /// (or a whole turn) too late.
    /// </summary>
    public IReadOnlyList<string> ProjectionDifferences { get; private set; } = [];

    // Capture is invoked by the host on the game thread; every branch owns the
    // complete party, shared monsters, card piles, relics, powers and RNG streams.
    public static KernelSession Capture(CombatState live)
    {
        KernelIsolation.Initialize();
        using var isolation = SimulationNotificationIsolation.Enter();
        PowerDynamicVarWarmup.EnsureMaterialized(live);
        CardDynamicVarWarmup.EnsureMaterialized(live);
        var session = new KernelSession(
            new CombatPredictionSimulator(new SimulatedCombatState(live, live.IterateHookListeners().ToArray())), new(), live,
            live.Players.Select(player => player.PlayerCombatState?.TurnNumber ?? 0).DefaultIfEmpty(0).Max())
        {
            ready = live.Players.Where(CombatManager.Instance.IsPlayerReadyToEndTurn).Select(p => p.NetId).ToHashSet(),
            extraTurnParticipants = CombatManager.Instance.PlayersTakingExtraTurn.Select(p => p.NetId).ToHashSet()
        };
        session.capturedPotions = live.Players.SelectMany(player => Enumerable.Range(0, player.PotionSlots.Count)
            .Where(slot => player.GetPotionAtSlotIndex(slot) is not null)
            .Select(slot => (Original: player.GetPotionAtSlotIndex(slot)!, Slot: slot,
                Captured: session.Combat.GetPotionAtSlot(player, slot)!)))
            .ToDictionary(row => row.Original, row => (row.Slot, row.Captured));
        session.potionInventories = live.Players.ToDictionary(player => player, player => {
            var originals = player.Potions.ToArray();
            return new PotionInventory(player.MaxPotionCount, player.CanUseOrRemovePotions, originals,
                originals.Where(p => !p.IsQueued && !p.HasBeenRemovedFromState && p.PassesCustomUsabilityCheck
                    && p.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime).ToHashSet());
        });
        session.potionValues = session.capturedPotions.Keys.ToDictionary(p => p,
            p => (IReadOnlyDictionary<string, decimal>)p.DynamicVars.Values.ToDictionary(v => v.Name, v => v.BaseValue));
        session.potionTargetRules = session.capturedPotions.Keys.ToDictionary(p => p,
            p => (IReadOnlyDictionary<Creature, bool>)live.Creatures.ToDictionary(c => c, p.IsValidTarget));
        session.VerifyProjection(live);
        return session;
    }

    /// <summary>
    /// Re-projects the live board through the vendor's full visible-state text and
    /// records every seat where the fresh simulator disagrees with reality.
    ///
    /// Deliberately a report and not a throw. The vendored single-player controller
    /// throws on this (CombatRootSnapshot.Capture), but a kernel branch that cannot
    /// reproduce one live detail is still a usable search — it just cannot promise
    /// replay. Failing closed here would turn every unmodeled mechanic into "the bots
    /// stop playing", which is a worse outcome than a plan that is honestly labelled
    /// as unverifiable.
    /// </summary>
    private void VerifyProjection(CombatState live)
    {
        List<string>? differences = null;
        foreach (var player in live.Players)
        {
            if (player.PlayerCombatState is null) continue;
            string actual;
            try { actual = ContinuationStamp.CaptureLive(live, player).StateText; }
            catch (Exception error)
            {
                (differences ??= new List<string>()).Add($"{player.NetId}: live-capture-failed {error.GetType().Name}");
                continue;
            }
            string predicted;
            try { predicted = StateText(player); }
            catch (Exception error)
            {
                (differences ??= new List<string>()).Add($"{player.NetId}: projection-failed {error.GetType().Name}: {error.Message}");
                continue;
            }
            if (string.Equals(predicted, actual, StringComparison.Ordinal)) continue;
            (differences ??= []).Add($"{player.NetId}: "
                + new ContinuationStamp(predicted).DescribeFirstDifference(new ContinuationStamp(actual)));
        }
        ProjectionDifferences = differences ?? new List<string>();
    }
    /// <summary>
    /// Whether this session can be forked at all. <see cref="Fork"/> refuses when an action is
    /// half-resolved, and its only other caller-facing signal is the thrown exception — which a
    /// caller that merely wants to DECLINE (rather than fail) cannot use without turning a
    /// routine condition into an exception trace on every tick.
    /// </summary>
    public bool CanFork => failedBoundary is null;

    public KernelSession Fork()
    {
        if (failedBoundary is not null) throw new InvalidOperationException("Cannot fork incomplete action: " + failedBoundary);
        using var isolation = SimulationNotificationIsolation.Enter();
        return new(simulator.Fork(), new(processedDeaths), liveRoot, rootTurnNumber) { ready = new(ready), extraTurnParticipants = new(extraTurnParticipants),
            capturedPotions = capturedPotions,
            potionInventories = potionInventories,
            potionValues = potionValues,
            potionTargetRules = potionTargetRules,
            synchronousChoicePlayers = new(synchronousChoicePlayers),
            EnemyPhaseCompleted = EnemyPhaseCompleted, HorizonClosed = HorizonClosed,
            LastActionHadEnvironmentalRisk = LastActionHadEnvironmentalRisk,
            ProjectionDifferences = ProjectionDifferences.ToArray(),
            AutoResolveEnemyChoices = AutoResolveEnemyChoices,
            AutoResolveTurnStartChoices = AutoResolveTurnStartChoices,
            RoundsAdvanced = RoundsAdvanced };
    }
    public IReadOnlyList<Creature> Enemies => Combat.HittableEnemies.Where(c => Hp(c) > 0).ToArray();

    // Targetability is not progress: a reviving boss still owns its future
    // forms. Use the native lifecycle's effective HP for terminal comparison.
    //
    // public since 2026-10-05：mod 侧的 KernelCombatEvaluation 也要用同一份语义
    // （次要敌人/爪牙不计进度），不能各自读 native 属性导致两套判定。
    public bool IsPrimaryEnemy(Creature enemy)
        => ((CoopBots.Kernel.Vendor.Engine.Common.ICombatPredictionCreatureSemantics)Combat).IsPrimaryEnemy(enemy);

    internal (int Count, int Hp) RemainingOpposition()
    {
        int count = 0, hp = 0;
        foreach (var enemy in Combat.Enemies.Where(IsPrimaryEnemy))
        {
            var remaining = Combat.EffectiveEnemyHp(enemy, simulator.State.GetCreature(enemy));
            if (remaining <= 0) continue;
            count++; hp += remaining;
        }
        return (count, hp);
    }

    /// <summary>
    /// Cheap grouping key: HP, block, energy, hand, powers and readiness. It
    /// omits piles, RNG and mutable card state and is not an equality contract.
    /// Production joint search disables this deduplication. Exact continuation
    /// reuse must use PlanningState, never extend the lifetime of this key.
    /// </summary>
    public string CompactStateKey()
    {
        using var allocation = AllocationProbe.Measure(AllocationProbe.Phase.CompactKey);
        var text = new System.Text.StringBuilder(256);
        foreach (var player in Combat.Players)
        {
            text.Append(player.NetId).Append(':').Append(Hp(player.Creature)).Append('/')
                .Append(Block(player.Creature)).Append('/').Append(Energy(player)).Append('/')
                .Append(IsReady(player) ? '1' : '0').Append('[');
            foreach (var card in Hand(player).Select(c => c.Id.Entry + "+" + c.CurrentUpgradeLevel)
                .OrderBy(name => name, StringComparer.Ordinal))
                text.Append(card).Append(',');
            text.Append(']').Append(';');
        }
        foreach (var enemy in Enemies)
            text.Append(enemy.CombatId).Append(':').Append(Hp(enemy)).Append('/').Append(Block(enemy)).Append(';');
        foreach (var power in Combat.EffectivePowers()
            .OrderBy(p => p.Owner.CombatId).ThenBy(p => p.Id.Entry, StringComparer.Ordinal))
            text.Append(power.Owner.CombatId).Append(':').Append(power.Id.Entry).Append('=').Append(power.Amount).Append(';');
        text.Append('R').Append(Combat.RoundNumber).Append('E').Append(EnemyPhaseCompleted ? '1' : '0');
        return text.ToString();
    }
    public bool HasWon => simulator.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory };

    /// <summary>
    /// True once this branch has reached ANY terminal outcome — a victory or the party
    /// being wiped. This is what "a world-line that ends the fight" means, and a wiped
    /// line deliberately counts: it ends the fight, so it is a route the solver can
    /// commit to rather than a fixed-horizon plan that stops wherever the depth ran out.
    ///
    /// Distinct from <see cref="EnemyPhaseCompleted"/>, which only says an enemy phase
    /// resolved inside the branch. Conflating the two is how a search comes to prefer
    /// "crossed a round" over "actually finished".
    /// </summary>
    public bool HasTerminal => simulator.TerminalStamp is not null;

    /// <summary>
    /// True once this branch has run out of ROUNDS while the fight is still going — i.e. the
    /// bounded-lookahead horizon closed here, rather than by a victory or a wipe.
    /// </summary>
    /// <remarks>
    /// Set in <c>KernelTurns.EndTurn</c>'s "no rounds remain" branch. It exists because a
    /// bounded search CANNOT reach a terminal (the whole point of bounding it is that the fight
    /// outlives the horizon), so the search needs something concrete to reward instead —
    /// otherwise the deepest line it can find is worth nothing and a two-card stub outscores a
    /// full five-round line. Measured live 2026-09-20 with MaxRounds=3: the best line came back
    /// as `actions=2`. See <c>KernelTeamSearch.Score</c>'s HorizonBonus.
    /// <para>
    /// Deliberately NOT set when the branch won or the party died: those are
    /// <see cref="HasTerminal"/>, which outranks the horizon in the scoring.
    /// </para>
    /// </remarks>
    public bool HorizonClosed { get; private set; }
    // Some attacks become group attacks only inside the predicted branch.
    // Delegate to the same upstream target resolver used by native simulation.
    internal TargetType CardTargetType(CardModel source)
    {
        var card = simulator.State.FindCard(source);
        return card is null ? source.TargetType : simulator.GetTargetType(card);
    }
    public IReadOnlyList<Creature?> Targets(CardModel source)
    {
        var card = simulator.State.FindCard(source);
        if (card is null) return [];
        return simulator.GetTargetType(card) switch
        {
            TargetType.AnyEnemy => Enemies.Cast<Creature?>().ToArray(),
            TargetType.AnyPlayer => Combat.Players.Where(p => Hp(p.Creature) > 0).Select(p => (Creature?)p.Creature).ToArray(),
            TargetType.AnyAlly => Combat.Players.Count(p => Hp(p.Creature) > 0) > 1
                ? Combat.Players.Where(p => Hp(p.Creature) > 0).Select(p => (Creature?)p.Creature).ToArray() : [],
            // A Self card is played with NO target: the live log shows
            // `PlayCardAction ... DEFEND_SILENT ... targetid:` blank and
            // `playing card DEFEND_SILENT (no target)`. This used to hand back the
            // owner's creature instead, which made every such card fail
            // `CanPlayTargeting` at deployment and threw the whole searched plan away —
            // the reason a 68-action route with route=True never played a single step.
            // Nothing downstream needs the creature: both mirrors read the target for
            // Self cards from card.Owner (GeneralCardMirrors "case TargetType.Self:
            // blockAction(card.Owner.Creature)" and StructuralCardMirror
            // "case TargetType.Self: recipients = [owner]"), so this changes the
            // deployed target only.
            TargetType.Self => [null],
            TargetType.Osty => Combat.GetOsty(card.Preview.Owner) is { } osty && Hp(osty) > 0 ? [osty] : [],
            TargetType.TargetedNoCreature => [],
            _ => [null]
        };
    }
    public int Hp(Creature creature) => simulator.State.GetCreature(creature).CurrentHp;
    public int MaxHp(Creature creature) => simulator.State.GetCreature(creature).MaxHp;
    public int Block(Creature creature) => simulator.State.GetCreature(creature).Block;
    public int Energy(Player player) => simulator.State.GetPlayerCombatState(player).Energy;
    public IReadOnlyList<CardModel> Hand(Player player) => simulator.State.GetPlayerCombatState(player).Hand.Cards.Select(c => c.Original).ToArray();

    // Original is a stable physical identity; transfers update the branch preview.
    public Player CardOwner(CardModel source) => simulator.State.FindCard(source)?.Preview.Owner ?? source.Owner;

    // Before deploying a cached EndTurn, the live hand can contain an affordable
    // attack absent from the prediction. Replan only for a resolved improvement;
    // this must not force attacks into lethal thorns or a sleeping enemy.
    public bool HasBeneficialAttack(Player player)
    {
        if (!CanFork || !CanAct(player)) return false;
        var before = KernelTacticalPolicy.Value(this, Party);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in Hand(player))
        {
            if (card.Type != CardType.Attack || !CanPlay(card) || !seen.Add(CardStateKey(card))) continue;
            foreach (var target in Targets(card))
            {
                var branch = CardBranches(card, target, maximumBranches: 1).FirstOrDefault(b => b.Boundary.Length == 0);
                if (branch is not null && KernelTacticalPolicy.ActionValue(this, branch.State, Party, Party) > before + 0.01)
                    return true;
            }
        }
        return false;
    }
    public IReadOnlyList<CardModel> Exhaust(Player player) => simulator.State.GetPlayerCombatState(player).ExhaustPile.Cards.Select(c => c.Original).ToArray();
    public IReadOnlyList<CardModel> Discard(Player player) => simulator.State.GetPlayerCombatState(player).DiscardPile.Cards.Select(c => c.Original).ToArray();
    public IReadOnlyList<CardModel> DrawPile(Player player) => simulator.State.GetPlayerCombatState(player).DrawPile.Cards.Select(c => c.Original).ToArray();
    public int OrbCount(Player player) => simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.Count;
    public bool HasOsty(Player player) => Combat.GetOsty(player) is not null;
    public int PotionCapacity(Player player) => potionInventories.TryGetValue(player, out var inventory) ? inventory.Capacity : 0;
    public bool CanUsePotions(Player player) => potionInventories.TryGetValue(player, out var inventory) && inventory.CanUse;
    internal IReadOnlyList<PotionModel> CapturedPotions(Player player)
        => potionInventories.TryGetValue(player, out var inventory) ? inventory.Originals : [];
    internal decimal PotionValue(PotionModel potion, string variable)
        => potionValues.TryGetValue(potion, out var values) && values.TryGetValue(variable, out var value) ? value : 0;
    public int HeldPotionCount(Player player) => Enumerable.Range(0, PotionCapacity(player))
        .Count(slot => Combat.GetPotionAtSlot(player, slot) is not null);
    public bool HoldsPotion(PotionModel potion) => FindCapturedPotion(potion) is not null;
    public IReadOnlyList<PotionModel> UsablePotions(Player player)
        => potionInventories.TryGetValue(player, out var inventory)
            ? inventory.Originals.Where(p => inventory.Usable.Contains(p) && FindCapturedPotion(p) is not null).ToArray() : [];
    /// <summary>
    /// Whether the kernel can simulate this potion COMPLETELY. A potion with no mirrored
    /// OnUse is rejected by every branch, so the search can never pick it; the planner
    /// reports these by name instead of leaving a full slot unexplained in the log.
    /// </summary>
    /// <remarks>
    /// "Mirrored" is not "modelled", and conflating the two cost a whole class of live
    /// desyncs. The four CHOOSER potions (Attack/Skill/Power/Colorless) DO have a mirror, but
    /// it is explicitly half a model: <c>CardGenerationPotionMirrors</c> returns
    /// <c>AddsToHand: false</c> and its own docstring says the generated cards are produced
    /// "without … applying combat piles and hooks". So the search rolls the three cards and
    /// then plans the rest of the fight as if the bottle added NOTHING, while the live game
    /// adds whichever card the chooser picked.
    ///
    /// <para>
    /// Measured live 2026-09-20 (`SLIMES_WEAK`): live hand +1 (`BLOODLETTING`), the plan's step
    /// 4 (play the free card) never replayed, eleven `plan step drift` lines that were all that
    /// one displacement, and `plan step refused: card-left-hand` at step 12 — the
    /// `card-left-hand` family's root cause. Reporting these as modelable while they are only
    /// half-modelled is exactly the lying metric R5b exists against.
    /// </para>
    /// <para>
    /// Until the choice is captured, propagated and answered (a four-part feature), R4's
    /// standing policy applies: an unmodelled effect is a BOUNDARY, not something to plan
    /// across. So these report as unmodelable and the search refuses them; the standalone
    /// potion planner can still choose them outside a script.
    /// </para>
    /// </remarks>
    public static bool CanModelPotion(PotionModel potion)
        => RequiresPotionChoiceContract(potion)
            || PotionOnUseMirrors.CanMirror(potion) && !PotionChoiceSupport.RequiresChoice(potion);

    /// <summary>
    /// The status card the player's next play would hand them, or null when no
    /// enemy counts their plays or the counter is not yet at its last step. Only
    /// Aeonglass's Withering Presence does this today, and the legacy scorer
    /// prices one card at a time, so it could not see that playing one more card
    /// was about to cost end-of-turn damage.
    /// </summary>
    public static CardModel? PendingHeldStatus(Player player)
    {
        try
        {
            foreach (var enemy in player.Creature.CombatState?.Enemies ?? [])
                foreach (var power in enemy.Powers)
                {
                    if (power is not WitheringPresencePower presence || power.Amount <= 0) continue;
                    if (presence.Target?.Player != player) continue;
                    if (!presence.DynamicVars.TryGetValue(WitheringPresencePower._cardsLeftKey, out var left)) continue;
                    if (left.IntValue == 1) return ModelDb.Card<Wither>().ToMutable();
                }
        }
        catch
        {
            // A mechanic we cannot read must not invent a cost.
        }
        return null;
    }
    public IReadOnlyList<Creature?> PotionTargets(PotionModel potion)
    {
        bool Allowed(Creature target)
        {
            // Native target rules may read IsAlive. Freeze their answers for
            // native identities on Capture's thread; later summons are private
            // simulated creatures and can still use the native rule directly.
            if (potionTargetRules.TryGetValue(potion, out var targets) && targets.TryGetValue(target, out var allowed))
                return allowed;
            return potion.IsValidTarget(target);
        }
        switch (potion.TargetType)
        {
            case TargetType.AllEnemies:
                return [null];
            case TargetType.AnyEnemy:
                return Enemies.Where(Allowed).Cast<Creature?>().ToArray();
            case TargetType.AnyPlayer:
                return Combat.Players.Where(p => Hp(p.Creature) > 0 && Allowed(p.Creature))
                    .Select(p => (Creature?)p.Creature).ToArray();
            case TargetType.Self:
                return Hp(potion.Owner.Creature) > 0 ? [potion.Owner.Creature] : [];
            default:
                return [];
        }
    }
    public bool CanPlay(CardModel source)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var card = simulator.State.FindCard(source);
        return failedBoundary is null && card is not null && CanAct(card.Preview.Owner)
            && simulator.State.GetPlayerCombatState(card.Preview.Owner).Hand.Cards.Contains(card)
            && Combat.CanPlayCard(simulator, card);
    }
    public bool Play(CardModel source, Creature? target, out string boundary)
    {
        LastTurnStartChoices = null;
        using var allocation = AllocationProbe.Measure(AllocationProbe.Phase.Play);
        using var isolation = SimulationNotificationIsolation.Enter();
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        var card = simulator.State.FindCard(source);
        if (card is null || !CanPlay(source)) { boundary = "unplayable"; return false; }
        if (!Targets(source).Contains(target)) { boundary = "invalid-target"; return false; }
        // A transfer can change Preview.Owner during ManualPlay. This action's
        // choices, turn and requested ending still belong to its original actor.
        var actingOwner = card.Preview.Owner;
        // Any exception/choice/risk after this point leaves an unusable branch.
        // Never allow a partially resolved action to enter a subsequent fork.
        failedBoundary = "incomplete-action";
        // 0.33.9 needed an explicit suppression window here because its PhantomBlades /
        // Lethality / Unmovable mirrors read "cards already played this turn" and would
        // double-count the card being played. 0.41 folded that adjustment into the
        // mirrors themselves (ModifyDamageMirrors.HandleLethalityPower subtracts the
        // in-play card; HandleUnmovablePower subtracts GetPoweredBlockEvents(CardPlay)),
        // so the scope no longer exists upstream and ManualPlay needs no wrapper.
        Combat.BeginActionChoices(ChoiceCursor(actingOwner));
        var structural = false;
        try
        {
            using (Combat.BeginCardExecutionScope(processedDeaths))
            {
                if (!simulator.ManualPlay(card, target, out _)) { boundary = failedBoundary = "pending-choice"; return false; }
                // A card the snapshot cannot mirror would otherwise be dropped from
                // the search entirely. Price the shapes we can describe exactly
                // from their own declared variables instead.
                structural = StructuralCardMirror.ApplyIfUnmodeled(simulator, Combat, source, target);
                // The death-power dispatch runs inside an execution-dispatch scope upstream
                // (Expansion.cs:2724-2726: `using (simulator.BeginExecutionDispatch())`
                // around ApplyEnemyDeathPowers). We called the dispatch without it.
                bool deathsCompleted;
                using (simulator.BeginExecutionDispatch())
                    deathsCompleted = CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator, Combat, Combat.KnownEnemies, processedDeaths);
                if (!deathsCompleted || !CombatBeamSolver.SettleReplayActionBoundary(simulator, Combat))
                { boundary = failedBoundary = "pending-boundary"; return false; }
            }
        }
        catch (Exception error)
        {
            // A mirror that throws (e.g. an upstream assumption our multiplayer
            // capture does not satisfy) must fail closed like any other boundary.
            // Aborting here would poison the whole search instead of falling back.
            //
            // The MESSAGE and the throwing frame go in, not just the type name. A live
            // run reported `SURVIVOR:prediction-exception:ArgumentException` seven times
            // and there was no way to tell which argument, from where — the type name
            // alone is a dead end for every card in the deck.
            boundary = failedBoundary = "prediction-exception:" + error.GetType().Name
                + ": " + error.Message
                // THREE frames, not one. The live case's first frame was
                // `System.MulticastDelegate.ThrowNullThisInDelegateToInstance()`, which
                // names the mechanism and not whoever built the bad delegate — a single
                // frame is another dead end, the same shape of mistake as the type name.
                + " @ " + string.Join(" <- ",
                    (error.StackTrace ?? "no-stack").Split('\n')
                        .Select(line => line.Trim())
                        .Where(line => line.StartsWith("at "))
                        .Take(3)
                        .DefaultIfEmpty("no-stack"));
            return false;
        }
        finally { Combat.EndActionChoices(); }
        var requestedEnd = Combat.ConsumePlayerTurnEndRequest();
        simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(actingOwner));
        // Only the played card's own semantics must be fully modeled to trust the
        // action. An unknown enemy power/relic hook is an approximation, not a
        // reason to remove the card from the plan: rejecting those made the bots
        // refuse to attack enemies whose passive hooks the snapshot lacks.
        var gaps = PredictionCoverage.Collect(simulator).Where(gap => !gap.Compensated).ToArray();
        // `structural` used to empty this, which is what let an unmodelled card into a plan:
        // StructuralCardMirror prices "one declared power, granted to self or one ally" from
        // the card's own DynamicVars and then declares the action an estimate. That estimate
        // is not always good enough — measured live 2026-09-20, CONCOCT went through this path
        // into a 90-action route whose hand then diverged from reality (live hand 6 cards
        // against the plan's 5, the extra a generated AFTERIMAGE) and which was refused at
        // its turn boundary eighteen times in a row.
        //
        // Policy now: an unmodelled OnPlay is a BOUNDARY, exactly as upstream treats it. The
        // card is not played at all rather than played on a guess. The cost is real and
        // deliberate — the multiplayer support family (Blaze, Coordinate, Fade, …) stops
        // being used until each is actually modelled — and it is the conservative half of
        // "search once, then follow the script": a plan must not contain steps whose effect
        // the simulator only approximated.
        var blocking = gaps
            .Where(gap => gap.Method == "OnPlay" && gap.SourceId == source.Id.Entry)
            .ToArray();
        LastActionHadEnvironmentalRisk = gaps.Length > blocking.Length;
        LastActionRiskDetail = DescribeUncompensated(gaps, blocking);
        boundary = blocking.Length > 0 ? "prediction-risk:" + string.Join(";", blocking.Select(gap => gap.ToString())) : "";
        failedBoundary = boundary.Length > 0 ? boundary : null;
        if (boundary.Length == 0 && requestedEnd && !HasWon) return EndTurn(actingOwner, 1, out boundary);
        return boundary.Length == 0;
    }

    /// <summary>
    /// Uses a potion inside this branch. Potions are free actions, so a potion
    /// that turns a non-lethal turn into a confirmed kill can be evaluated and
    /// confirmed by the same engine that plans the cards.
    /// </summary>
    public bool UsePotion(PotionModel potion, Creature? target, out string boundary)
        => UsePotion(potion, target, null, out boundary);

    private bool UsePotion(PotionModel potion, Creature? target, IReadOnlyList<KernelChoice>? choices, out string boundary)
    {
        LastTurnStartChoices = null;
        if (RequiresPotionChoiceContract(potion)) return UseTouch(potion, target, choices, out boundary);
        using var isolation = SimulationNotificationIsolation.Enter();
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        if (!CanAct(potion.Owner)) { boundary = "owner-ended"; return false; }
        // R4: a half-modelled effect is a BOUNDARY, not something to plan across. The four
        // CHOOSER potions generate their offer but never apply the pick (see CanModelPotion),
        // so a branch that used one would continue on a board that is wrong by exactly one
        // card — which is how `SLIMES_WEAK` desynchronised and finally refused at step 12.
        // SOFT refusal on purpose (`boundary =`, not `boundary = failedBoundary =`): nothing
        // about this session is broken, the action is simply not available to the search, and
        // marking the session failed would poison every other expansion off the same fork.
        // ANY potion that needs a CHOICE is not replayable here, and the engine already has the
        // predicate for that: `PotionChoiceSupport.RequiresChoice`. It covers the four card
        // generation potions AND the five pile/pick potions (Ashwater, DropletOfPrecognition,
        // GamblersBrew, LiquidMemories, TouchOfInsanity) — NINE, where this guard used to cover
        // only the first four.
        //
        // Why it matters: the search builds a potion action with NO choices
        // (`KernelTeamSearch`: `new Action(actor, null, target, potion)`) and UsePotion passes
        // `choice: null` to PotionExecutionSupport.Complete, so the plan can never say WHICH card
        // the potion should take. The live screen is answered by the generic `BotCardSelector`
        // instead, and when it picks differently the script's hand model diverges. Measured live
        // 2026-09-20 (A10 4-bot, SLIMES_NORMAL): DROPLET_OF_PRECOGNITION was used at step 16/52
        // ("pick 1 card from the draw pile into your hand" — `CardSelectCmd.FromCombatPile`), the
        // drift then read `D[2]`/`H[2] expected={POMMEL_STRIKE}`, and at step 36/52 the script
        // refused `card-left-hand` on POMMEL_STRIKE — the very card the potion should have moved.
        //
        // R4: an effect that cannot be replayed is a BOUNDARY, not something to plan across. The
        // standalone potion planner can still drink it outside a script.
        if (PotionChoiceSupport.RequiresChoice(potion))
        { boundary = "potion-choice-unmodeled"; return false; }
        failedBoundary = "incomplete-action";
        try
        {
            // The simulation owns cloned potions; map the live one to its clone.
            var simulated = FindCapturedPotion(potion);
            if (simulated is null)
            { boundary = failedBoundary = "potion-not-captured"; return false; }
            var slot = SimulatedPotionSlot(simulated);
            if (slot < 0)
            { boundary = failedBoundary = "potion-not-captured"; return false; }
            // The FULL potion lifecycle, not just its effect. Upstream drives a potion as
            // PotionExecutionSupport.Prepare + Complete (Expansion.cs:2638-2648); we called
            // `ManualUse` alone, which is OnUseWrapper and nothing else. That skipped
            // ConsumePotion (so the bottle stayed in the simulated inventory),
            // BeforePotionUsed and AfterPotionUsed (so "when a potion is used" relic and
            // power hooks never fired), SynchronizePowerAmountPredictionStates,
            // ResolvePowerAmountChanges and CompensateHistorySince — i.e. almost the whole
            // lifecycle, silently, on every potion the search ever considered.
            var potionHistoryStart = simulator.History.Entries.Count;
            Combat.BeginActionChoices(ChoiceCursor(potion.Owner));
            try
            {
                if (!PotionExecutionSupport.Prepare(simulator, Combat, simulated, slot, target)
                    || !PotionExecutionSupport.Complete(simulator, Combat, simulated, target, null,
                        potionHistoryStart, processedDeaths))
                { boundary = failedBoundary = "pending-choice"; return false; }
                if (!CombatBeamSolver.SettleReplayActionBoundary(simulator, Combat))
                { boundary = failedBoundary = "pending-boundary"; return false; }
            }
            finally { Combat.EndActionChoices(); }
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(potion.Owner));
        }
        catch (Exception error)
        {
            boundary = failedBoundary = "potion-exception:" + error.GetType().Name;
            return false;
        }
        return FinishPotionEvidence(potion, out boundary);
    }

    // Ordinary and selected Touch lifecycles share the exact original evidence gate.
    private bool FinishPotionEvidence(PotionModel potion, out string boundary)
    {
        // Same rule as Play: only the potion's own OnUse must be modeled; other
        // unknowns are estimates and are surfaced through the risk flag.
        var gaps = PredictionCoverage.Collect(simulator).Where(gap => !gap.Compensated).ToArray();
        var blocking = gaps
            .Where(gap => gap.Method == "OnUse" && gap.SourceId == potion.Id.Entry)
            .ToArray();
        LastActionHadEnvironmentalRisk = gaps.Length > blocking.Length;
        LastActionRiskDetail = DescribeUncompensated(gaps, blocking);
        boundary = blocking.Length > 0 ? "prediction-risk:" + string.Join(";", blocking.Select(gap => gap.ToString())) : "";
        failedBoundary = boundary.Length > 0 ? boundary : null;
        return boundary.Length == 0;
    }
}
