using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

public static class BotBrain
{
    public readonly record struct CombatMove(CardModel Card, Creature? Target, double Score, string Reason = "basic",
        IReadOnlyList<CoopBots.Kernel.KernelChoice>? Choices = null)
    {
        public CoopBots.Kernel.KernelTurnStartChoiceContract? TurnStartChoices { get; init; }
    }

    public static CombatMove? ChooseCombatMove(Player player, int actionNumber)
    {
        var moves = LegalCombatMoves(player);
        if (moves.Count == 0)
            return null;
        // Every tier runs the same policy; difficulty only changes pacing and
        // thinking budget, never the quality of a single decision.
        return GeniusCombatStrategy.Choose(player, moves, actionNumber);
    }

    // One-action fallback for effects outside the team projection.
    internal static CombatMove? ChooseEffectFallback(Player player, int actionNumber)
    {
        var legal = LegalCombatMoves(player).Where(move => TeamCombatPlanner.NeedsEffectFallback(move.Card)).ToList();
        // A fallback is a one-action heuristic, not a rescue certificate.
        // Avoid speculative spending when any teammate already faces lethal damage.
        if (legal.Count == 0 || player.RunState.Players.Any(p => p.Creature.IsAlive && CombatAssessment.InDanger(p.Creature)))
            return null;
        var scored = GeniusCombatStrategy.ScoreLegalMoves(player, legal, actionNumber);
        return scored.Where(move => move.Score > 0
                && GeniusCombatStrategy.Analyze(move.Card, move.Target, player.PlayerCombatState!.Energy,
                    player.Creature.CombatState!.HittableEnemies.Count()).HpLoss < player.Creature.CurrentHp)
            .Select(move => (CombatMove?)move).FirstOrDefault();
    }

    // The live policy requires full legality. Search may also retain cards blocked
    // only by resources; its root action still requires full live legality.
    internal static List<CombatMove> LegalCombatMoves(Player player)
        => PlanningCombatMoves(player);

    internal static List<CombatMove> PlanningCombatMoves(Player player, bool allowFutureResources = false)
    {
        var state = player.Creature.CombatState;
        var combat = player.PlayerCombatState;
        if (state is null || combat is null)
            return new List<CombatMove>();

        var moves = new List<CombatMove>();
        foreach (var card in combat.Hand.Cards.Where(card =>
        {
            if (card.CanPlay(out var reason, out _)) return true;
            return allowFutureResources && (reason & ~(UnplayableReason.EnergyCostTooHigh
                | UnplayableReason.StarCostTooHigh)) == UnplayableReason.None;
        }))
        {
            var candidates = state.Creatures.Where(card.IsValidTarget).ToList();
            if (card.IsValidTarget(null))
                candidates.Add(null!);

            foreach (var target in candidates.Cast<Creature?>())
            {
                if (allowFutureResources || card.CanPlayTargeting(target))
                    moves.Add(new CombatMove(card, target, ScoreCombatCard(card, target)));
            }
        }

        return moves;
    }

    // The native entry points that permanently edit the deck. `FromDeckForUpgrade`
    // and `FromHandForUpgrade` are different things: the hand one upgrades a card
    // for the current combat only.
    private static IReadOnlyList<CardModel>? Permanent(
        Player player, List<CardModel> list, int count, string purpose)
    {
        if (!purpose.Contains("FromDeck", StringComparison.OrdinalIgnoreCase)) return null;
        if (purpose.Contains("ForRemoval", StringComparison.OrdinalIgnoreCase))
            return RemoveFromDeck(player, list, count, purpose);
        // Pael's Tooth shares the smith's path exactly: the cards it takes come back
        // upgraded after a later combat, so the question is which card is worth the most
        // upgrade — and, because the file's ranking is greedy over a context that swaps in
        // the upgraded card after each pick, the second choice sees the first upgrade.
        // Reported live 2026-09-30: the removal ranking spent it on five Strikes and
        // Defends while every card the deck was built around stayed unupgraded.
        if (purpose.Contains("ForUpgrade", StringComparison.OrdinalIgnoreCase) || purpose == PaelsToothPurpose)
        {
            var context = player.Deck.Cards.ToList();
            var remaining = list.ToList();
            // Owner 2026-10-05: a permanent upgrade must never be spent on a plain
            // basic Strike/Defend; enchanted basics are excepted. Live 2026-10-04 Bot1
            // spent three camps on DEFEND_SILENT while its shiv engines stayed
            // unupgraded. Pael's Tooth is a removal+upgrade that has to fill five, so
            // the rule is scoped to the plain upgrade screens; and if a deck somehow
            // has nothing else upgradable, the screen still fills rather than stalling.
            if (purpose.Contains("ForUpgrade", StringComparison.OrdinalIgnoreCase))
            {
                remaining = Building.BuildValue.EligibleUpgrades(remaining).ToList();
            }
            var chosen = new List<CardModel>();
            var trace = new List<(CardModel, double, string)>();
            while (chosen.Count < count && remaining.Count > 0)
            {
                var next = Building.BuildValue.RankUpgrades(remaining.Select(c =>
                    (Card: c, Value: Building.BuildValue.UpgradeForDecision(c, player, context)))).First();
                if (Building.ConstructionDecision.AwaitingValues) break;
                chosen.Add(next.Card); remaining.Remove(next.Card);
                trace.Add((next.Card, next.Value.Total, next.Value.Reason));
                var index = context.IndexOf(next.Card);
                if (index >= 0 && Building.CardProfile.Upgraded(next.Card, Building.BuildValue.StableEnergy(player)) is { } upgrade)
                    context[index] = upgrade.Card;
            }
            BuildTrace.LogDeckEdit(player, purpose, chosen, trace);
            return chosen;
        }
        if (!purpose.Contains("ForTransformation", StringComparison.OrdinalIgnoreCase)) return null;
        if (purpose.Split(':', 2) is { Length: 2 } fixedPurpose && !fixedPurpose[1].StartsWith("RNG_", StringComparison.Ordinal))
        {
            var ranked = Building.FixedTransform.Rank(player, list, fixedPurpose[1]);
            var selected = ranked.Take(count).Select(c => c.Card).ToList();
            BuildTrace.LogDeckEdit(player, purpose, selected,
                ranked.Select(c => (c.Card, c.Value, c.Reason)).ToList());
            return selected;
        }
        var transform = Building.TransformPlan.Choose(player, list, count,
            purpose.Contains(":RNG_NICHE", StringComparison.Ordinal) ? player.RunState.Rng.Niche : Building.TransformPlan.CurrentRng(player),
            upgrade: purpose.EndsWith("_UPGRADE", StringComparison.Ordinal));
        var chosenTransforms = transform.Steps.Select(step => step.Card).ToArray();
        BuildTrace.LogDeckEdit(player, purpose, chosenTransforms,
            transform.Steps.Select(step => (step.Card, step.Value,
                $"transform:{(transform.Exact ? "predicted" : "sampled")}:{step.Card.Id.Entry}->{step.Result},deck-delta:{step.Value:F2}")).ToList());
        return chosenTransforms;
    }

    /// <summary>
    /// A basic, unenchanted Strike or Defend may never be the target of a permanent
    /// upgrade. Owner decision 2026-10-05 (live 2026-10-04 Bot1 spent three camps on
    /// DEFEND_SILENT). An enchantment is the exception the owner named: an enchanted
    /// basic is no longer a vanilla starter, so it stays eligible.
    /// </summary>
    internal static bool IsForbiddenUpgrade(CardModel card)
        => card.Rarity == CardRarity.Basic
           && card.Enchantment is null
           && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend));

    // Removals are chosen against the deck as each earlier pick would leave it.
    // The real deck is never touched: `context` is a local copy of the full
    // actual deck, and only the instance actually selected leaves it, so the last
    // copy of a role becomes protected exactly when removing it would leave the
    // deck without that role. Protected candidates only pay when no unprotected
    // candidate is left, so a forced all-protected screen still fills its count
    // instead of stalling or short-picking.
    private static IReadOnlyList<CardModel> RemoveFromDeck(
        Player player, List<CardModel> candidates, int count, string purpose)
    {
        // The ordering lives in RemovalPlan so the rest-site Cook valuation
        // prices exactly the removals this path would actually make.
        var plan = Building.RemovalPlan.Choose(player, candidates, count);
        var selected = plan.Steps.Select(step => step.Card).ToList();
        var trace = plan.Steps.Select(step => (step.Card, step.Value, step.Reason)).ToList();
        BuildTrace.LogDeckEdit(player, purpose, selected, trace);
        if (plan.Fallback)
            BuildTrace.LogDeckEdit(player, purpose + ":fallback-protected", selected, trace);
        return selected;
    }

    public static IReadOnlyList<CardModel> SelectCards(
        Player player,
        IEnumerable<CardModel> cards,
        int min,
        int max,
        string purpose,
        bool maySkip = false)
    {
        var list = cards.Distinct().ToList();
        if (list.Count == 0)
            return Array.Empty<CardModel>();

        var count = Math.Clamp(Math.Max(0, min), 0, Math.Min(Math.Max(min, max), list.Count));
        // Native canSkip means choose zero OR one, not choose zero before
        // valuing the offer. Only permanent additions share reward valuation;
        // combat generation/discard screens keep their own semantics.
        if (purpose == PermanentAddPurpose)
            return WorthTaking(player, list, maySkip ? count : Math.Max(1, count), max, purpose);
        // The screen said it can be declined, so returning nothing is an answer
        // rather than a failure. The name test below is a stall guard for screens
        // that demand a pick; applying it to a declinable one made the choice
        // mandatory, and the bot had to take whatever scored highest — which is how
        // a Star card the deck could never pay for got picked out of an offer that
        // was meant to be skippable.
        if (count == 0 && !maySkip && purpose.Contains("ChooseA", StringComparison.OrdinalIgnoreCase))
            count = 1;

        // A screen offering "any number of these" is not asking for `min` cards: it is asking
        // WHICH of these the deck wants. Answering with `min` — which this shape always sets
        // to 0 — returns nothing, and it does so WITHOUT ever scoring a card. The ranking
        // further down had already run; `.Take(0)` threw the whole offer away.
        //
        // Measured live 2026-09-30 (OROBAS): the seat picked Sea Glass — "look at 15 cards
        // from another character, add any number of them to your deck", i.e.
        // `CardSelectorPrefs(prompt, 0, 15)` — took ZERO of the 15, and left the room.
        // Nothing in the log said why, because nothing had been decided.
        if (count < max && TakesAnyNumberOf(purpose))
            return WorthTaking(player, list, count, max);

        // Claws permits 0..6 replacements. Score each replacement against the
        // deck after prior picks; min=0 must not silently discard the blessing.
        if (purpose == "FromDeckForTransformation:MAUL")
            return AncientOptionValue.ClawPlan(player, list, max).Cards;
        if (purpose.StartsWith("FromDeckForEnchantment:", StringComparison.Ordinal))
        {
            var parts = purpose.Split(':');
            if (parts.Length == 3 && EnchantmentPriorities.Resolve(parts[1]) is { } enchantment
                && int.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var amount))
                return EnchantmentPriorities.Select(player, list, enchantment, amount, min, max);
        }

        // Permanent deck edits are decided by the same valuation the reward screen
        // and the shop use, so "is this worth doing" and "which card" cannot
        // disagree. Hand-based variants are combat effects and keep their own
        // scoring; the combat plan already has first refusal through
        // BotChoicePlanSync.
        if (Permanent(player, list, count, purpose) is { } permanent) return permanent;

        if (purpose == "FromChooseACardScreen" && count == 1
            && CoopBots.Kernel.KernelKnowledgeChoice.Select(player, list) is { } curse)
            return [curse];

        if (purpose == "FromChooseACardScreen"
            && MegaCrit.Sts2.Core.Runs.RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction
                is MegaCrit.Sts2.Core.GameActions.UsePotionAction
            && SelectGeneratedPotionCard(player, list) is { } generated)
            return [generated];

        var discard = purpose.Contains("Discard", StringComparison.OrdinalIgnoreCase);
        var preferWeak = discard
                         || purpose.Contains("Removal", StringComparison.OrdinalIgnoreCase)
                         || purpose.Contains("Transform", StringComparison.OrdinalIgnoreCase);
        return (discard
                ? list.OrderBy(card => CombatDiscardScore(card))
                : preferWeak
                    ? list.OrderBy(card => ScoreDeckCard(card))
                    : list.OrderByDescending(card => ScoreDeckCard(card)))
            .ThenBy(card => card.Id.Entry, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Screens whose preferences mean "take any number of these" (<c>min 0, max N</c>) where
    /// the cards would be ADDED to the deck — so every card taken is free upside and taking
    /// none is a pure loss.
    ///
    /// Deliberately narrow. A <c>0..N</c> screen that DISCARDS or EXHAUSTS is a cost, and
    /// there declining really is the right answer; those keep the old behaviour until each
    /// one has its own evidence.
    /// </summary>
    private static bool TakesAnyNumberOf(string purpose)
        => purpose.Contains("FromSimpleGridForRewards", StringComparison.OrdinalIgnoreCase);

    internal const string PermanentAddPurpose = "FromChooseACardScreen:permanent-add";

    /// <summary>
    /// Pael's Tooth's pickup screen. It is a <c>FromDeckForRemoval</c> call, but the cards it
    /// takes come BACK UPGRADED after a later combat, so the decision is the camp smith's —
    /// which card do I most want upgraded — not the removal ranking's "which can I spare".
    /// Kept as its own purpose so <see cref="Permanent"/> can rank it by upgrade delta and
    /// the removal path stays untouched. Note it must NOT contain "ForRemoval".
    /// </summary>
    internal const string PaelsToothPurpose = "FromDeckForPaelsTooth";

    /// <summary>
    /// The offer's cards the deck actually wants, best first, never fewer than
    /// <paramref name="min"/>.
    ///
    /// Greedy and marginal: each pick is scored against the deck as it would stand WITH the
    /// picks already made, through <c>BuildValue.Add</c>'s deck override, so the fifth good
    /// card is not priced as if it were the first. It stops at the first card that no longer
    /// pays for the draws it costs, which is the same "skipping is a real candidate" rule the
    /// reward screen uses. A mandatory floor (<paramref name="min"/> &gt; 0) is still honoured
    /// even when nothing scores.
    /// </summary>
    private static IReadOnlyList<CardModel> WorthTaking(Player player, List<CardModel> list, int min, int max,
        string purpose = "FromSimpleGridForRewards")
    {
        var (picked, ranked) = Building.BuildValue.GreedyPick(player, list, min, max);
        // The reward screen records what it took and what it scored; this one used to
        // record nothing at all, which is why "took zero of fifteen" was invisible.
        BuildTrace.LogDeckEdit(player, purpose, picked, ranked);
        return picked;
    }

    public static int ChooseCardReward(Player player, IReadOnlyList<CardModel> cards)
    {
        if (cards.Count == 0)
            return -1;
        // Selection and its diagnostic must share the same read-only deck
        // evaluations. Otherwise three offers and their log run S twelve times.
        using var evaluation = Building.DeckStrength.BeginEvaluation();
        // Every candidate is compared against the deck as it stands, so skipping
        // is a real candidate rather than a fallback: a card that takes more
        // draws from the deck than it contributes is worth less than nothing.
        // Research capture is default-off and must not change the scorer call.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pending = Building.ConstructionDecision.Current is null ? BuildDecisionCapture.CapturePreState(player, cards) : null;
        // The bar slides down over act 1: the community prior prices a finished
        // deck, so early on a card may be worth taking while scoring slightly
        // negative. It never changes which candidate wins, only whether we take one.
        var takeBar = Building.BuildValue.RewardTakeBar(player);
        // Owner policy 2026-10-04: the act-1 boss offers a rare-card menu. A
        // low-completion deck must not skip a free rare, so this menu is
        // take-one rather than take-or-skip. The all-rare shape is the game's
        // own marker for that menu; ordinary rewards can still be skipped.
        var forceActOneBossRare = player.RunState.CurrentActIndex == 0
            && cards.All(card => card.Rarity == CardRarity.Rare);
        var chosen = Building.BuildValue.BestReward(player, cards,
            allowSkip: !forceActOneBossRare, takeBar: takeBar);
        BuildDecisionCapture.CaptureOutcome(pending, chosen);
        BuildTrace.LogReward(player, cards, chosen, skip: chosen < 0, started, takeBar);
        return chosen;
    }

    internal static Task<int> ChooseCardRewardAsync(Player player, IReadOnlyList<CardModel> cards, CancellationToken cancellation = default)
        => Building.ConstructionDecision.RunAsync(player, () => ChooseCardReward(player, cards), () => {
            if (!AutoPilot.Drives(player.NetId) || player.Creature.IsDead) throw new OperationCanceledException();
            return Building.ConstructionDecision.Cards(cards);
        }, cancellation);

    /// <summary>
    /// A card reward answer: a card index, or — when the reward is skipped — the
    /// Pael's Wing SACRIFICE option id that replaces the skip. Owner request
    /// 2026-10-04: with the Wing, a skipped reward should become sacrifice progress
    /// instead of being thrown away, and <see cref="Building.BuildValue.RewardTakeBar"/>
    /// raises the bar so that is the common case. The alternative never displaces a
    /// card the brain actually chose.
    /// </summary>
    internal readonly record struct CardRewardDecision(int Index, string? AlternativeId);

    /// <summary>
    /// The skip half of <see cref="ChooseCardRewardDecision"/>, split out so the
    /// asynchronous reward path (which only receives the index) shares one policy.
    /// </summary>
    internal static CardRewardDecision ApplySkipPolicy(Player player, int index, IReadOnlyList<string>? alternativeIds)
    {
        if (index >= 0) return new(index, null);
        if (alternativeIds is not null
            && player.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.PaelsWing)
            && alternativeIds.FirstOrDefault(id => string.Equals(
                id, MegaCrit.Sts2.Core.Models.Relics.PaelsWing.sacrificeAlternativeKey,
                StringComparison.OrdinalIgnoreCase)) is { } sacrifice)
            return new(-1, sacrifice);
        return new(-1, null);
    }

    internal static CardRewardDecision ChooseCardRewardDecision(Player player, IReadOnlyList<CardModel> cards,
        IReadOnlyList<string>? alternativeIds)
        => ApplySkipPolicy(player, ChooseCardReward(player, cards), alternativeIds);

    internal static CardModel? SelectGeneratedPotionCard(Player player, IReadOnlyList<CardModel> cards)
    {
        if (player.Creature.CombatState is not MegaCrit.Sts2.Core.Combat.CombatState combat) return null;
        var root = KernelCombatCapture.Capture(combat);
        if (!root.CanFork) return null;
        var actors = combat.Players.Where(p => AutoPilot.Drives(p.NetId)).ToArray();
        var ranked = cards.Select(card => (Card:card, Value:root.GeneratedCardBenefit(card, actors)))
            .OrderByDescending(c => c.Value).ThenByDescending(c => ScoreDeckCard(c.Card))
            .ToArray();
        var chosen = ranked.FirstOrDefault().Card;
        MegaCrit.Sts2.Core.Logging.Log.Info($"CoopBots potion choice: bot={player.NetId} hand={root.Hand(player).Count}/10"
            + $" pick={chosen?.Id.Entry} offers=[{string.Join(',', ranked.Select(c => $"{c.Card.Id.Entry}:{c.Value:F1}"))}]");
        return chosen;
    }

    private static double ScoreCombatCard(CardModel card, Creature? target)
    {
        var damage = card.DynamicVars.Values.OfType<DamageVar>().Sum(variable => (double)variable.BaseValue);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(variable => (double)variable.BaseValue);
        var cost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
        var score = card.Type switch
        {
            CardType.Attack => 7.0 + damage,
            CardType.Skill => 5.0 + block * 0.9,
            CardType.Power => 11.0,
            CardType.Curse => -100.0,
            CardType.Status => -40.0,
            _ => 2.0,
        };

        score -= cost * 2.2;
        if (cost == 0)
            score += 2.5;
        if (card.GainsBlock && card.Owner.Creature.Block < 12)
            score += block * 0.5;
        if (target?.IsEnemy == true)
            score += 10.0 / Math.Max(1, target.CurrentHp + target.Block);

        score += card.Type == CardType.Power ? 5.0 : 0.0;
        if (target?.IsEnemy == true && damage >= target.CurrentHp + target.Block)
            score += 80.0;
        if (card.TargetType == TargetType.AllEnemies)
            score += damage * Math.Max(0, card.Owner.Creature.CombatState!.Enemies.Count - 1);

        return score;
    }

    /// <summary>
    /// Discard ranking for combat. Keep the printed-deck score as the base, then
    /// add the current-board value that <see cref="ScoreDeckCard"/> cannot see:
    /// a Power/poison engine, a block card under real incoming damage, and a
    /// strength-down answer such as PIERCING_WAIL against a multi-hit turn.
    /// Lower still means "pitch this first".
    /// </summary>
    internal static double CombatDiscardScore(CardModel card)
    {
        var score = ScoreDeckCard(card);
        try
        {
            if (card.Owner?.Creature.CombatState is not { } combat) return score;
            var owner = card.Owner.Creature;
            var incomingDamage = CombatAssessment.Incoming(owner);
            if (card.Type == CardType.Power) score += 8;
            if (card.GainsBlock && incomingDamage > 0)
                score += 4 + Math.Min(6, CombatAssessment.BlockFor(card, owner));
            var strengthDown = card.DynamicVars.TryGetValue("StrengthLoss", out _)
                || string.Equals(card.Id.Entry, "PIERCING_WAIL", StringComparison.OrdinalIgnoreCase);
            if (strengthDown)
                score += CombatAssessment.IncomingHits(owner) >= 2
                         || CombatAssessment.IncomingStrength(owner) > 0 ? 15 : 8;
            var poisonEngine = card.DynamicVars.TryGetValue("PoisonPerTurn", out _)
                || string.Equals(card.Id.Entry, "NOXIOUS_FUMES", StringComparison.OrdinalIgnoreCase);
            if (poisonEngine) score += 6;
        }
        catch { /* an unreadable combat state falls back to the deck score */ }
        return score;
    }

    internal static double ScoreDeckCard(CardModel card)
    {
        var damage = card.DynamicVars.Values.OfType<DamageVar>().Sum(variable => (double)variable.BaseValue);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(variable => (double)variable.BaseValue);
        var rarity = card.Rarity switch
        {
            CardRarity.Rare => 9.0,
            CardRarity.Uncommon => 4.0,
            CardRarity.Curse => -50.0,
            CardRarity.Status => -30.0,
            _ => 0.0,
        };
        var type = card.Type switch
        {
            CardType.Attack => 3.0,
            CardType.Skill => 2.5,
            CardType.Power => 5.0,
            CardType.Curse => -30.0,
            CardType.Status => -20.0,
            _ => 0.0,
        };
        var value = rarity + type + damage * 0.25 + block * 0.25;
        var cost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.Local));
        value -= cost * 1.25;
        if (cost == 0)
            value += 1.5;
        if (card.IsUpgraded)
            value += 3.0;
        return value;
    }
}
