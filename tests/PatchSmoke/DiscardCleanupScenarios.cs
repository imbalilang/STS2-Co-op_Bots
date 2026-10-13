using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// 用户 2026-09-29：主动弃牌时不要把"虚无且没有额外负面效果"的状态/诅咒（进阶之灾、
/// 眩晕）排在第一位——它们本来就会在回合末自己烧掉，丢掉等于把它留在牌组里等着再抽一次。
/// 该丢的是那些本回合确定打不出去的牌。非虚无的诅咒/状态（贪婪）与带回合末伤害的
/// WITHER 保持原有优先级不变（那是 V14–V18 定的"先扔掉伤手牌"）。
/// </summary>
// R2 2026-10-07: 3 discard-policy + 18 scoped/native-prefix production-mirror faults.
// Each final pair: build0, named RED1, exact source restore, build0, GREEN0.
// Raw commands/hashes/logs: work/incombat-repair-20261007/choice/discard-native-review/{r2-results,performance-prefix-r2-results}.json.
// First ordinal-content attempt expected a different guard; retained separately as invalid, never counted.
// The effective ordinal-content fault is caught by populated-child Commit, not independent physical-key-vector separation.
// The card identity fault collapses BOTH physical Card and Occurrence; it is not a Card-field-only proof.
// R2 future-pollution-wiring:
// System.Exception: ASCENDERS_BANE (ethereal, no downside) must not be pitched: it burns itself at end of turn
// R2 native-draw-effect-classification:
// System.Exception: harmless Ethereal classification must exclude native draw effects and playable cards
// R2 finite-cost-survival-victory:
// System.Exception: future Ethereal pollution must not prohibit the actual lifesaving mandatory discard
// R2 pure-hand-value:
// System.Exception: pure HandValue suffix scoring must bound nested choice previews and restore its scope
// R2 pure-investment:
// System.Exception: pure investment suffix scoring must bound nested choice previews and restore its scope
// R2 pure-defend-threat:
// System.Exception: pure Defend Threat scoring must bound nested choice previews and restore its scope
// R2 pure-then:
// System.Exception: pure sequence Then suffix must apply native effects without recursively comparing unpublished choices
// R2 deployable-open:
// System.Exception: deployable sequence Open must still compare complete native choices and retain the affordable Cut payoff
// R2 same-root-tactical:
// System.Exception: Tactical ordering must reuse its same-root fully compared native menu branches exactly once
// R2 empty-affordable-hand:
// System.Exception: no affordable card must resolve one complete native enemy boundary rather than three duplicate hands
// R2 zero-cost-hand:
// System.Exception: zero energy with an affordable native zero-cost card must keep all three hand policies
// R2 prefix-production-wiring:
// System.Exception: production choice Evaluate must use the exact prefix cache for all three complete native policies
// R2 prefix-owned-end:
// System.Exception: advancing an owned native cache hit must not mutate the saved EndTurn template
// R2 prefix-physical-target:
// System.Exception: native prefix targets must retain distinct physical enemy damage results
// R2 prefix-physical-card:
// System.Exception: native prefix keys must isolate physical card occurrences, targets and explicit native choice vectors
// R2 prefix-end-parameters:
// System.Exception: native EndTurn prefix keys must isolate maximum-round parameters and keep the seed immutable
// R2 prefix-commit-token:
// System.Exception: native prefix cache must disable reuse after a mismatched committed native token
// R2 prefix-vector-presence:
// System.Exception: native prefix keys must distinguish absent and explicitly empty choices and ordinal vectors
// R2 prefix-absent-commit:
// System.Exception: native prefix Commit must not accept absent metadata as an explicitly resolved empty vector
// R2 prefix-full-party-state:
// System.Exception: native prefix cache must preserve every policy's exact HP/powers/readiness/ordered physical piles and RNG after each action
// R2 prefix-explicit-token:
// System.Exception: native prefix cache must disable reuse after a mismatched committed native token
internal static class DiscardCleanupScenarios
{
    private static bool Format(ref string __result) { __result = ""; return false; }

    private static bool NoTutorial(ref Task __result) { __result = Task.CompletedTask; return false; }

    internal static void Run()
    {
        new Harmony("coopbots.discard.cleanup.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(DiscardCleanupScenarios), nameof(Format)));
        new Harmony("coopbots.discard.cleanup.tutorial").Patch(AccessTools.Method(typeof(CardPileCmd), "ShuffleFtueCheck"),
            prefix: new HarmonyMethod(typeof(DiscardCleanupScenarios), nameof(NoTutorial)));
        var errors = new List<string>();
        // A/B: 虚无且无额外副作用 → 不许占用这次弃牌。
        Check(errors, Discard("ETHE-BANE", "ASCENDERS_BANE STRIKE_SILENT") == "STRIKE_SILENT",
            "ASCENDERS_BANE (ethereal, no downside) must not be pitched: it burns itself at end of turn");
        Check(errors, Discard("ETHE-DAZED", "DAZED STRIKE_SILENT") == "STRIKE_SILENT",
            "DAZED (ethereal, no downside) must not be pitched: it burns itself at end of turn");
        // C/D: 不虚无的诅咒、以及会掉血的 WITHER，优先级不变。
        Check(errors, Discard("CURSE-GREED", "GREED STRIKE_SILENT") == "GREED",
            "a non-ethereal curse is still the first thing to pitch (it would clog the deck forever)");
        Check(errors, Discard("STATUS-WITHER", "WITHER STRIKE_SILENT") == "WITHER",
            "WITHER (turn-end damage, not ethereal) keeps its old top priority");
        // E: 其次要丢掉"本回合确定打不出去"的牌（3 费 Bludgeon 只剩 2 费）。
        Check(errors, Discard("DEAD-CARD", "DAZED STRIKE_SILENT BLUDGEON") == "BLUDGEON",
            "a card that cannot be played this turn outranks one that still can");
        // F: 实机 2026-10-05：对手多段攻击回合，PIERCING_WAIL 被 Acrobatics 弃掉。
        //    当前局面里它是唯一降力量答案，不能被当成低分普通牌先扔。
        Check(errors, Discard("KEEP-WAIL", "PIERCING_WAIL SHIV", enemyStrength: 12) == "SHIV",
            "PIERCING_WAIL must be kept over a Shiv on an incoming attack turn");
        Check(errors, LiveDiscard("LIVE-WAIL", "PIERCING_WAIL SHIV") == "SHIV",
            "the live discard fallback must also keep PIERCING_WAIL over a Shiv");
        // G: Power/poison 引擎同理，不能被 Shiv 挤掉。
        Check(errors, Discard("KEEP-FUMES", "NOXIOUS_FUMES SHIV") == "SHIV",
            "NOXIOUS_FUMES must be kept over a Shiv while the fight is still scaling");
        Check(errors, Discard("KEEP-BLOCK", "DEFEND_SILENT SHIV", enemyStrength: 12) == "SHIV",
            "a real block card must be kept over a Shiv when damage is incoming");
        NativeDiscardConsequences(errors);
        SequenceScoringScope(errors);
        EmptyAffordableHand(errors);
        NativePrefixCache(errors);
        if (errors.Count > 0)
            throw new Exception(string.Join(Environment.NewLine, errors));
        Console.WriteLine("PASS: discard pitches statuses/curse first, then cards that cannot be played this turn, and leaves ethereal no-downside cards to burn.");
    }

    /// <summary>
    /// Plays SURVIVOR (1 energy, gain block, discard 1) and reads back which card the
    /// bounded choice policy picked. The first branch is the default index vector, i.e.
    /// the head of the ordered option list.
    /// </summary>
    private static string Discard(string seed, string hand, int enemyStrength = 0)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 55, 1));
        var combat = new CombatState(runState: RunState.CreateForTest([bot], seed: seed));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
        // 3 energy: SURVIVOR takes one, so a 3-cost card is provably unplayable afterwards
        // while a 1-cost Strike is still affordable.
        bot.PlayerCombatState.GainEnergy(3);
        bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Survivor>(bot));
        foreach (var name in hand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = ModelDb.AllCards.First(c => c.Id.Entry == name);
            bot.PlayerCombatState.Hand.AddInternal(combat.CreateCard(canonical, bot));
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(200); foe.SetCurrentHpInternal(200);
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);

        // Survivor already grants eight block. These protection cases need
        // actual uncovered pressure; native Mock1 plus Strength12 is thirteen.
        if (enemyStrength != 0)
            ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>().ToMutable()
                .ApplyInternal(foe, enemyStrength, true);

        var root = KernelSession.Capture(combat);
        var survivor = root.Hand(bot).First(c => c is Survivor);
        var branch = root.CardBranches(survivor, null, 1).FirstOrDefault(b => b.Boundary.Length == 0)
            ?? throw new Exception($"{seed}: SURVIVOR could not resolve its discard choice.");
        return branch.Choices.Single().Cards.Single().Id;
    }

    /// <summary>
    /// The live fallback path (BotBrain.SelectCards) must use the same board-aware
    /// ranking as the kernel plan: a strength-down or engine card outranks a Shiv.
    /// </summary>
    private static string LiveDiscard(string seed, string hand)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 56, 1));
        var combat = new CombatState(runState: RunState.CreateForTest([bot], seed: seed));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
        bot.PlayerCombatState.GainEnergy(3);
        var cards = new List<CardModel>();
        foreach (var name in hand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = ModelDb.AllCards.First(c => c.Id.Entry == name);
            var card = combat.CreateCard(canonical, bot);
            cards.Add(card);
            bot.PlayerCombatState.Hand.AddInternal(card);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(200); foe.SetCurrentHpInternal(200);
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        return BotBrain.SelectCards(bot, cards, 1, 1, "FromHandForDiscard").Single().Id.Entry;
    }

    // Independent card commands/hooks, without ActionValue/cleanup weights.
    // Only the legal mandatory menu is forced; resources, card effects, natural
    // Ethereal exhaust, discard hooks, reshuffle/draw and monster attack are native.
    private static CardModel? forcedDiscard;
    private static bool ForceDiscard(AbstractModel source, ref Task<IEnumerable<CardModel>> __result)
    {
        if (forcedDiscard is null || source is not Survivor) return true;
        __result = Task.FromResult<IEnumerable<CardModel>>([forcedDiscard]);
        return false;
    }

    private sealed record DiscardWitness(decimal Hp, decimal EnemyHp, decimal Block,
        bool JunkExhausted, bool JunkInFutureBag, bool JunkRedrawn, int Drawn, int Playable,
        string Choices, string Costs);

    private static DiscardWitness NativeDiscard(string hand, string discard, int strength = 0,
        int hp = 80, int enemyHp = 200, bool futureDraw = false)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 198, 1));
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: "DISCARD-NATIVE-SAME-INPUT"));
        using var managerState = new NativeTurnState(combat);
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 3;
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(hp);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
        player.PlayerCombatState.GainEnergy(3);
        var survivor = combat.CreateCard<Survivor>(player);
        player.PlayerCombatState.Hand.AddInternal(survivor);
        foreach (var id in hand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            player.PlayerCombatState.Hand.AddInternal(combat.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        if (strength != 0) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, strength, true);
        var context = new ThrowingPlayerChoiceContext();
        var costs = new List<string>();
        void Play(CardModel card)
        {
            if (!card.CanPlay()) throw new Exception("discard native witness requires an affordable card " + card.Id.Entry);
            Creature? target = card.TargetType == TargetType.AnyEnemy ? enemy : null;
            if (!card.IsValidTarget(target)) throw new Exception("discard native witness requires a legal target");
            var energy = player.PlayerCombatState.Energy;
            var spent = card.SpendResources().GetAwaiter().GetResult();
            player.PlayerCombatState.Hand.RemoveInternal(card); player.PlayerCombatState.PlayPile.AddInternal(card);
            var result = card.Keywords.Contains(CardKeyword.Exhaust) ? PileType.Exhaust : PileType.Discard;
            var play = new CardPlay { Card = card, Player = player, Target = target, ResultPile = result,
                Resources = new ResourceInfo { EnergySpent = spent.Item1, EnergyValue = spent.Item1,
                    StarsSpent = spent.Item2, StarValue = spent.Item2 }, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
            ((Task)AccessTools.Method(card.GetType(), "OnPlay").Invoke(card, [context, play])!).GetAwaiter().GetResult();
            player.PlayerCombatState.PlayPile.RemoveInternal(card);
            (result == PileType.Exhaust ? player.PlayerCombatState.ExhaustPile : player.PlayerCombatState.DiscardPile).AddInternal(card);
            costs.Add(card.Id.Entry + ":" + energy + "->" + player.PlayerCombatState.Energy);
        }
        var patch = new Harmony("coopbots.discard.cleanup.native.counterfactual");
        var command = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard));
        patch.Patch(command, prefix: new HarmonyMethod(typeof(DiscardCleanupScenarios), nameof(ForceDiscard)) { priority = Priority.First });
        try
        {
            forcedDiscard = player.PlayerCombatState.Hand.Cards.Single(c => c.Id.Entry == discard);
            Play(survivor);
        }
        finally { forcedDiscard = null; patch.UnpatchAll(patch.Id); }
        foreach (var card in player.PlayerCombatState.Hand.Cards.ToArray())
            if (card.CanPlay() && enemy.CurrentHp > 0) Play(card);
        var block = player.Creature.Block;
        foreach (var card in player.PlayerCombatState.Hand.Cards.ToArray())
        {
            if (card.HasTurnEndInHandEffect) card.OnTurnEndInHandWrapper(context).GetAwaiter().GetResult();
            else if (card.Keywords.Contains(CardKeyword.Ethereal) && MegaCrit.Sts2.Core.Hooks.Hook.ShouldEtherealTrigger(combat, card))
                CardCmd.Exhaust(context, card, causedByEthereal: true).GetAwaiter().GetResult();
        }
        var exhausted = player.PlayerCombatState.ExhaustPile.Cards.Any(c => c.Id.Entry is "ASCENDERS_BANE" or "DAZED");
        foreach (var card in player.PlayerCombatState.Hand.Cards.ToArray())
            CardCmd.Discard(context, card).GetAwaiter().GetResult();
        var bag = player.PlayerCombatState.DiscardPile.Cards.Any(c => c.Id.Entry is "ASCENDERS_BANE" or "DAZED");
        if (enemy.CurrentHp > 0) enemy.Monster.NextMove.PerformMove([player.Creature]).GetAwaiter().GetResult();
        if (futureDraw)
        {
            // Native reshuffle/draw of the complete small bag exposes the
            // preserved unplayable physical instance, without assuming a top card.
            CardPileCmd.Draw(context, 10, player).GetAwaiter().GetResult();
        }
        var drawn = player.PlayerCombatState.Hand.Cards;
        var witness = new DiscardWitness(player.Creature.CurrentHp, enemy.CurrentHp, block,
            exhausted, bag, drawn.Any(c => c.Id.Entry is "ASCENDERS_BANE" or "DAZED"),
            drawn.Count, drawn.Count(c => !c.Keywords.Contains(CardKeyword.Unplayable)), discard, string.Join('|', costs));
        Console.WriteLine("Discard native witness " + witness);
        return witness;
    }

    private static void NativeDiscardConsequences(List<string> errors)
    {
        Check(errors, KernelRelicChoices.IsHarmlessEtherealJunk(ModelDb.Card<AscendersBane>())
            && KernelRelicChoices.IsHarmlessEtherealJunk(ModelDb.Card<Dazed>())
            && KernelRelicChoices.IsHarmlessEtherealJunk(ModelDb.Card<Clumsy>())
            && !KernelRelicChoices.IsHarmlessEtherealJunk(ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Void>())
            && !KernelRelicChoices.IsHarmlessEtherealJunk(ModelDb.Card<Apparition>()),
            "harmless Ethereal classification must exclude native draw effects and playable cards");
        var held = NativeDiscard("ASCENDERS_BANE STRIKE_SILENT", "STRIKE_SILENT", futureDraw: true);
        var pitched = NativeDiscard("ASCENDERS_BANE STRIKE_SILENT", "ASCENDERS_BANE", futureDraw: true);
        Check(errors, held.JunkExhausted && !held.JunkInFutureBag && !held.JunkRedrawn
            && !pitched.JunkExhausted && pitched.JunkInFutureBag && pitched.JunkRedrawn
            && pitched.Drawn == pitched.Playable + 1 && held.Drawn == held.Playable,
            "natural Ethereal exhaust must remove the native junk; discard must return its unplayable instance to a future hand slot");
        foreach (var keep in new[] { "PIERCING_WAIL", "DEFEND_SILENT" })
        {
            var protect = NativeDiscard(keep + " SHIV", "SHIV", strength: 12);
            var attack = NativeDiscard(keep + " SHIV", keep, strength: 12);
            Check(errors, protect.Hp == 80 && attack.Hp == 75 && protect.EnemyHp == 200 && attack.EnemyHp == 196,
                "native protection witness must prevent five actual HP loss rather than a fake intent: " + keep);
        }
        var survival = NativeDiscard("ASCENDERS_BANE DEFEND_SILENT", "ASCENDERS_BANE", strength: 12, hp: 3);
        var death = NativeDiscard("ASCENDERS_BANE DEFEND_SILENT", "DEFEND_SILENT", strength: 12, hp: 3);
        Check(errors, survival.Hp == 3 && death.Hp == 0,
            "native affordable block must survive when retaining the naturally expiring junk would kill");
        Check(errors, BoundaryChoice("ETHEREAL-LIFESAVING", "ASCENDERS_BANE DEFEND_SILENT", 3, 200, 12) == "ASCENDERS_BANE",
            "future Ethereal pollution must not prohibit the actual lifesaving mandatory discard");
        var lethal = NativeDiscard("ASCENDERS_BANE STRIKE_SILENT", "ASCENDERS_BANE", enemyHp: 6);
        Check(errors, lethal.EnemyHp == 0 && BoundaryChoice("ETHEREAL-WIN", "ASCENDERS_BANE STRIKE_SILENT", 80, 6, 0) == "ASCENDERS_BANE",
            "future Ethereal pollution must not displace a native immediate victory");
        Console.WriteLine("PASS: discard native future draw pollution, real protection pressure, lifesaving and lethal choices.");
    }

    // The real native death path reports removal through CombatManager. A
    // console fixture installs its own ordinary turn state, then restores the
    // prior one; card damage/death/removal hooks are not patched or skipped.
    private sealed class NativeTurnState : IDisposable
    {
        private readonly System.Reflection.FieldInfo field = AccessTools.Field(typeof(CombatManager), "_turnState");
        private readonly object? previous;
        internal NativeTurnState(CombatState combat)
        {
            previous = field.GetValue(CombatManager.Instance);
            field.SetValue(CombatManager.Instance, Activator.CreateInstance(field.FieldType,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, [combat], null));
        }
        public void Dispose() => field.SetValue(CombatManager.Instance, previous);
    }

    private static string BoundaryChoice(string seed, string hand, int hp, int enemyHp, int strength)
    {
        // Same production CardBranches path as the original policy guards.
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 198, 2));
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: seed));
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 3;
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(hp);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        var survivor = combat.CreateCard<Survivor>(player); player.PlayerCombatState.Hand.AddInternal(survivor);
        foreach (var id in hand.Split(' ')) player.PlayerCombatState.Hand.AddInternal(combat.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(enemyHp);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        if (strength != 0) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, strength, true);
        return KernelSession.Capture(combat).CardBranches(survivor, null, 1).Single().Choices.Single().Cards.Single().Id;
    }

    private static int evaluations;
    private static void CountEvaluations() => evaluations++;

    private static void SequenceScoringScope(List<string> errors)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 198, 3));
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: "SCORING-ONLY-CHOICE-SCOPE"));
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 4;
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(4);
        var survivor = combat.CreateCard<Survivor>(player); player.PlayerCombatState.Hand.AddInternal(survivor);
        var cut = combat.CreateCard<PreciseCut>(player); player.PlayerCombatState.Hand.AddInternal(cut);
        foreach (var id in new[] { "STRIKE_SILENT", "STRIKE_SILENT", "DEFEND_SILENT", "SHIV", "SHIV" })
            player.PlayerCombatState.Hand.AddInternal(combat.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemy, 8, true);
        ModelDb.Power<AccuracyPower>().ToMutable().ApplyInternal(player.Creature, 6, true);
        var prepared = combat.CreateCard<Prepared>(player); player.PlayerCombatState.Hand.AddInternal(prepared);
        var root = KernelSession.Capture(combat); var before = root.StateText(player);
        var observer = new Harmony("coopbots.discard.cleanup.scoring-scope");
        observer.Patch(AccessTools.Method(typeof(KernelChoiceContinuation), "Evaluate"),
            prefix: new HarmonyMethod(typeof(DiscardCleanupScenarios), nameof(CountEvaluations)));
        try
        {
            evaluations = 0;
            AccessTools.Method(typeof(KernelSequenceOrder), "HandValue").Invoke(null,
                [root, root, new[] { player }, new[] { player }]);
            Check(errors, evaluations == 0 && !KernelChoiceContinuation.Active,
                "pure HandValue suffix scoring must bound nested choice previews and restore its scope");
            evaluations = 0;
            var investment = new KernelInvestmentProbe(root, [player], [player],
                new(RolloutPolicyKind.Tactical, MaxSteps: 80) { SequenceLookahead = false });
            for (var i = 0; !investment.Done && i < 1000; i++) investment.Step();
            Check(errors, investment.Done && investment.Completed > 0 && evaluations == 0 && !KernelChoiceContinuation.Active,
                "pure investment suffix scoring must bound nested choice previews and restore its scope");
            evaluations = 0;
            AccessTools.Method(typeof(RolloutRun), "Score").Invoke(null,
                [root, new[] { player }, RolloutPolicyKind.Defend, player, prepared, null, new Random(1)]);
            Check(errors, evaluations == 0 && !KernelChoiceContinuation.Active,
                "pure Defend Threat scoring must bound nested choice previews and restore its scope");
            evaluations = 0;
            var action = new KernelTeamSearch.Action(player, survivor, null, CardStateKey: root.CardStateKey(survivor));
            var cache = new KernelSequenceOrder.ProbeCache(root, [player], [player]);
            var suffix = cache.Then(root.Fork(), action);
            Check(errors, suffix is not null && suffix.Block(player.Creature) == 8 && evaluations == 0
                && !KernelChoiceContinuation.Active,
                "pure sequence Then suffix must apply native effects without recursively comparing unpublished choices");
            evaluations = 0;
            var branch = cache.Open(action)!;
            Check(errors, evaluations > 0 && branch.Choices.Single().Cards.Single().Id != cut.Id.Entry,
                "deployable sequence Open must still compare complete native choices and retain the affordable Cut payoff");
            var replay = root.ForkForReplay();
            Check(errors, replay.ReplayPlanned(action with { Choices = branch.Choices, ChoiceOrdinals = branch.ChoiceOrdinals }, 1, out _)
                && replay.StateText(player) == branch.State.StateText(player) && root.StateText(player) == before,
                "deployable native sequence choice tokens must replay the exact branch without polluting the parent");
        }
        finally { observer.UnpatchAll(observer.Id); }
        TacticalNativeReuse(errors, root, player);
        Console.WriteLine("PASS: pure sequence scoring limits nested menus; deployable native choices and tokens remain fully compared.");
    }

    private static readonly List<(KernelSession Root, CardModel Card, Creature? Target)> nativeQueries = new();
    private static void ObserveNativeQuery(KernelSession __instance, CardModel card, Creature? target)
    {
        if (!KernelChoiceContinuation.Active && card is Survivor or Prepared)
            nativeQueries.Add((__instance, card, target));
    }

    private static void TacticalNativeReuse(List<string> errors, KernelSession root, Player player)
    {
        var observer = new Harmony("coopbots.discard.cleanup.tactical-native-reuse");
        observer.Patch(AccessTools.Method(typeof(KernelSession), "RawCardBranches"),
            prefix: new HarmonyMethod(typeof(DiscardCleanupScenarios), nameof(ObserveNativeQuery)));
        var before = root.StateText(player);
        var line = new RolloutRun(root.Fork(), [player], [player],
            new(RolloutPolicyKind.Tactical, MaxSteps: 2) { InvestmentLookahead = false, SequenceLookahead = true });
        try
        {
            nativeQueries.Clear(); var firstRoot = line.State;
            line.Step();
            var firstQueries = nativeQueries.Count(q => ReferenceEquals(q.Root, firstRoot));
            Check(errors, firstQueries > 0 && nativeQueries.Where(q => ReferenceEquals(q.Root, firstRoot))
                .GroupBy(q => (q.Card, q.Target)).All(g => g.Count() == 1),
                "Tactical ordering must reuse its same-root fully compared native menu branches exactly once");
            Check(errors, !ReferenceEquals(firstRoot, line.State),
                "native Tactical commit must advance to the selected physical branch");
            var secondRoot = line.State;
            while (!line.Done) line.Step();
            Check(errors, nativeQueries.Count(q => ReferenceEquals(q.Root, firstRoot)) == firstQueries
                && nativeQueries.Any(q => ReferenceEquals(q.Root, secondRoot)),
                "Tactical native branch cache must expire at commit and compare menus on the new physical root");
            var replay = root.ForkForReplay();
            foreach (var action in line.Result!.Actions)
                Check(errors, replay.ReplayPlanned(action, 1, out _), "cached Tactical native action tokens must remain executable");
            Check(errors, replay.StateText(player) == line.State.StateText(player) && root.StateText(player) == before,
                "cached Tactical native actions must replay the exact final state without changing the original root");
        }
        finally { observer.UnpatchAll(observer.Id); nativeQueries.Clear(); }
        Console.WriteLine("PASS: Tactical reuses exact native menus only within one physical root and publishes replayable tokens.");
    }

    private static void EmptyAffordableHand(List<string> errors)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 198, 4));
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: "EMPTY-AFFORDABLE-HAND"));
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 1;
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(1);
        var survivor = combat.CreateCard<Survivor>(player); player.PlayerCombatState.Hand.AddInternal(survivor);
        player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(player));
        player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendSilent>(player));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 8, true);
        var root = KernelSession.Capture(combat); var branch = root.CardBranches(survivor, null, 1).Single();
        var score = KernelChoiceContinuation.Evaluate(root, branch.State, player, 2);
        var boundary = branch.State.Fork(); boundary.PrepareChoiceHand(player);
        Check(errors, boundary.EndTurn(player, root.RoundsAdvanced + 1, out _) && boundary.EnemyPhaseCompleted
            && boundary.Hp(player.Creature) == 79 && score.CompleteHands == 1 && score.Steps == 1,
            "no affordable card must resolve one complete native enemy boundary rather than three duplicate hands");
        var expected = KernelTacticalPolicy.ActionValue(root, boundary, [player], [player])
            + boundary.FutureDebuffValue([player], elapsedTurns: 0, reference: root)
            - root.ExhaustOpportunityCost(boundary, player) - boundary.ChoicePollutionCost(player);
        Check(errors, score.Value == expected,
            "empty affordable hand optimization must retain the exact native HP/block/held-effect boundary score");
        var shiv = combat.CreateCard<Shiv>(player); player.PlayerCombatState.Hand.AddInternal(shiv);
        var freeRoot = KernelSession.Capture(combat);
        var freeBranch = freeRoot.CardBranches(survivor, null, 16).First(b => b.State.CanPlay(shiv));
        var freeScore = KernelChoiceContinuation.Evaluate(freeRoot, freeBranch.State, player, 3);
        Check(errors, freeBranch.State.Energy(player) == 0 && freeScore.CompleteHands == 3,
            "zero energy with an affordable native zero-cost card must keep all three hand policies");
        Console.WriteLine("PASS: no affordable cards share one complete native enemy-boundary witness; playable zero-cost cards retain three policies.");
    }

    private static void NativePrefixCache(List<string> errors)
    {
        for (var scene = 0; scene < 4; scene++)
        {
            var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 198, 5 + scene));
            var teammate = scene == 3 ? Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 198, 9)) : null;
            var party = teammate is null ? new[] { player } : new[] { player, teammate };
            var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "EXACT-NATIVE-PREFIX-" + scene));
            if (teammate is not null)
            {
                teammate.ResetCombatState(); combat.AddPlayer(teammate); teammate.MaxEnergy = 3;
                teammate.Creature.SetMaxHpInternal(80); teammate.Creature.SetCurrentHpInternal(61);
                teammate.PlayerCombatState!.Phase = PlayerTurnPhase.Play; teammate.PlayerCombatState.GainEnergy(3);
                teammate.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendSilent>(teammate));
                teammate.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeSilent>(teammate));
                teammate.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<Prepared>(teammate));
                teammate.PlayerCombatState.DiscardPile.AddInternal(combat.CreateCard<Survivor>(teammate));
                teammate.PlayerCombatState.DiscardPile.AddInternal(combat.CreateCard<DefendSilent>(teammate));
                ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(teammate.Creature, 3, true);
                ModelDb.Power<SetupStrikePower>().ToMutable().ApplyInternal(teammate.Creature, 3, true);
                ModelDb.Power<PlatingPower>().ToMutable().ApplyInternal(teammate.Creature, 4, true);
            }
            player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 4;
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(4);
            var hand = scene == 0 ? new[] { "STRIKE_SILENT", "STRIKE_SILENT" }
                : scene == 1 ? new[] { "PREPARED", "STRIKE_SILENT", "DEFEND_SILENT", "SURVIVOR" }
                : scene == 2 ? new[] { "SETUP_STRIKE", "STRIKE_SILENT" } : new[] { "STRIKE_SILENT", "STRIKE_SILENT" };
            foreach (var id in hand) player.PlayerCombatState.Hand.AddInternal(combat.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player));
            foreach (var id in new[] { "STRIKE_SILENT", "DEFEND_SILENT" })
                player.PlayerCombatState.DiscardPile.AddInternal(combat.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player));
            ModelDb.Power<PlatingPower>().ToMutable().ApplyInternal(player.Creature, 3, true);
            if (scene == 2) ModelDb.Power<AmbergrisPower>().ToMutable().ApplyInternal(player.Creature, 1, true);
            var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
            combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
            enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
            if (scene == 0)
            {
                var other = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "1");
                combat.AddCreature(other); other.Monster!.SetUpForCombat(); other.SetMaxHpInternal(200); other.SetCurrentHpInternal(200);
                other.Monster.SetMoveImmediate((MoveState)other.Monster.MoveStateMachine!.States["ATTACK"], true);
            }
            var root = KernelSession.Capture(combat); var rootBefore = PrefixState(root, player);
            var seed = root.Fork(); seed.SampleChoiceDrawPool(player); seed.PrepareChoiceHand(player);
            var seedBefore = PrefixState(seed, player); var cache = new KernelChoicePrefixCache(seed);
            using (KernelChoiceContinuation.Enter())
            foreach (var policy in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth })
            {
                var options = new RolloutOptions(policy, MaxSteps: 16, MaxRounds: seed.RoundsAdvanced + 1)
                    { InvestmentLookahead = false, SequenceLookahead = false };
                var actual = new RolloutRun(cache.ForkSeed(), party, [player], options) { ChoicePrefixCache = cache };
                var control = new RolloutRun(seed.Fork(), party, [player], options);
                var replay = seed.ForkForReplay();
                while (!actual.Done || !control.Done)
                {
                    actual.Step(); control.Step();
                    Check(errors, actual.Done == control.Done && PrefixState(actual.State, player) == PrefixState(control.State, player),
                        "native prefix cache must preserve every policy's exact HP/powers/readiness/ordered physical piles and RNG after each action");
                }
                Check(errors, actual.State.HasTerminal || actual.State.EnemyPhaseCompleted,
                    "native prefix cached policy must reach a real complete boundary");
                Check(errors, PrefixActions(actual.Result!.Actions) == PrefixActions(control.Result!.Actions)
                    && actual.Result.AliveStates.Select(x => string.Join(',', x)).SequenceEqual(control.Result.AliveStates.Select(x => string.Join(',', x)))
                    && actual.Result.EnemyStates.Zip(control.Result.EnemyStates).All(pair => pair.First.SequenceEqual(pair.Second)),
                    "native prefix cache must retain exact native choices, targets and before-action evidence without skipping planner decisions");
                foreach (var action in actual.Result.Actions)
                    Check(errors, replay.ReplayPlanned(action, options.MaxRounds, out _), "native prefix cached choices must replay through actual native effects");
                Check(errors, PrefixState(replay, player) == PrefixState(actual.State, player),
                    "native prefix cached actions must replay the same complete physical state and combat RNG");
                Check(errors, PrefixState(seed, player) == seedBefore && PrefixState(root, player) == rootBefore,
                    "native prefix cache must never mutate its common seed or parent while settling another policy");
            }
            if (scene == 0)
            {
                var evaluated = KernelChoiceContinuation.Evaluate(root, root, player, 2);
                Check(errors, cache.Hits >= 6 && evaluated.NativePrefixHits > 0 && evaluated.CompleteHands == 3,
                    "production choice Evaluate must use the exact prefix cache for all three complete native policies");
                var separate = new KernelChoicePrefixCache(seed);
                Check(errors, separate.Hits == 0, "native prefix cache must remain local to a single prepared Evaluate seed");
                NativePrefixIdentity(errors, root, player, enemy);
            }
            Console.WriteLine($"PASS: native prefix cache scene{scene} hits={cache.Hits}, three complete policies exact at every native action.");
        }
    }

    private static string PrefixActions(IReadOnlyList<KernelTeamSearch.Action> actions)
        => string.Join(";", actions.Select(a => $"{a.Player.NetId}:{a.EndTurn}:{a.Card?.Id.Entry}:{a.CardStateKey}:{a.CardStateOccurrence}:{a.Target?.CombatId}:{System.Text.Json.JsonSerializer.Serialize(a.Choices)}:{string.Join(',', a.ChoiceOrdinals ?? [])}"));

    private static string PrefixState(KernelSession state, Player player)
    {
        string Cards(IReadOnlyList<CardModel> cards) => string.Join(',', cards.Select(c =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c) + ":" + state.CardStateKey(c)));
        var rng = state.Simulator.Rng;
        var extra = (HashSet<ulong>)AccessTools.Field(typeof(KernelSession), "extraTurnParticipants").GetValue(state)!;
        var piles = string.Join(";", state.ChoiceParty.Select(p => $"{p.NetId}|H:{Cards(state.Hand(p))}|D:{Cards(state.DrawPile(p))}|C:{Cards(state.Discard(p))}|X:{Cards(state.Exhaust(p))}|ready:{state.IsReady(p)}"));
        return $"{state.PartyStateText()}|{piles}"
            + $"|extra:{string.Join(',', extra.Order())}|phase:{state.EnemyPhaseCompleted}|horizon:{state.HorizonClosed}|round:{state.RoundsAdvanced}"
            + $"|rng:{rng.ShuffleState}:{rng.CombatCardGenerationState}:{rng.CombatCardSelectionState}:{rng.CombatTargetsState}:{rng.MonsterAiState}:{rng.NicheState}:{rng.CombatPotionGenerationState}:{rng.CombatEnergyCostsState}:{rng.CombatOrbGenerationState}";
    }

    private static void NativePrefixIdentity(List<string> errors, KernelSession root, Player player, Creature target)
    {
        var first = root.Hand(player)[0]; var second = root.Hand(player)[1];
        var action = new KernelTeamSearch.Action(player, first, target, CardStateKey: root.CardStateKey(first));
        var cache = new KernelChoicePrefixCache(root); var nativeCalls = 0;
        KernelSession.CardBranch? Resolve(KernelTeamSearch.Action a)
        { nativeCalls++; return root.CardBranches(a.Card!, a.Target, 1).Single(); }
        using var guard = KernelChoiceContinuation.Enter();
        cache.CreateCursor().Card(0, action, () => Resolve(action));
        cache.CreateCursor().Card(0, action, () => Resolve(action));
        Check(errors, nativeCalls == 1, "same prepared native action prefix must reuse its exact complete branch");
        var duplicate = action with { Card = second, CardStateOccurrence = 1 };
        cache.CreateCursor().Card(0, duplicate, () => Resolve(duplicate));
        var otherTarget = action with { Target = root.Enemies[1] };
        var aimed = cache.CreateCursor().Card(0, otherTarget, () => Resolve(otherTarget))!;
        Check(errors, aimed.State.Hp(root.Enemies[1]) == 194 && aimed.State.Hp(target) == 200,
            "native prefix targets must retain distinct physical enemy damage results");
        var tokens = action with { ChoiceOrdinals = [1] };
        cache.CreateCursor().Card(0, tokens, () => Resolve(tokens));
        Check(errors, nativeCalls == 4, "native prefix keys must isolate physical card occurrences, targets and explicit native choice vectors");
        // Populate the true next-prefix edge before testing invalid commits;
        // an empty child node would call native even if a bad commit was accepted.
        var canonical = cache.CreateCursor(); var branch = canonical.Card(0, action, () => Resolve(action))!;
        canonical.Commit(action with { Choices = branch.Choices, ChoiceOrdinals = branch.ChoiceOrdinals });
        var followup = duplicate with { CardStateKey = branch.State.CardStateKey(second), CardStateOccurrence = 0 };
        KernelSession.CardBranch? ResolveFollowup()
        { nativeCalls++; return branch.State.CardBranches(second, target, 1).Single(); }
        canonical.Card(1, followup, ResolveFollowup);
        var beforeWrongCommit = nativeCalls;
        var wrongCommit = cache.CreateCursor(); wrongCommit.Card(0, action, () => Resolve(action));
        wrongCommit.Commit(action with { Choices = branch.Choices, ChoiceOrdinals = [99] });
        wrongCommit.Card(1, followup, ResolveFollowup);
        Check(errors, nativeCalls == beforeWrongCommit + 1, "native prefix cache must disable reuse after a mismatched committed native token");
        var empty = action with { Choices = [], ChoiceOrdinals = [] };
        var beforePresence = nativeCalls;
        cache.CreateCursor().Card(0, empty, () => Resolve(empty));
        Check(errors, nativeCalls == beforePresence + 1,
            "native prefix keys must distinguish absent and explicitly empty choices and ordinal vectors");
        var absentCommit = cache.CreateCursor(); var recorded = absentCommit.Card(0, action, () => Resolve(action))!;
        absentCommit.Commit(action with { Choices = null, ChoiceOrdinals = null });
        var beforeAbsent = nativeCalls;
        absentCommit.Card(1, followup, ResolveFollowup);
        Check(errors, recorded.Choices.Count == 0 && recorded.ChoiceOrdinals.Count == 0 && nativeCalls == beforeAbsent + 1,
            "native prefix Commit must not accept absent metadata as an explicitly resolved empty vector");
        NativeChoicePresence(errors);
        var endCache = new KernelChoicePrefixCache(root);
        var before = PrefixState(root, player);
        var c1 = endCache.CreateCursor(); c1.End(0, root.Fork(), player, 2, out var after, out _, out _);
        var c2 = endCache.CreateCursor(); c2.End(0, root.Fork(), player, 2, out var hit, out _, out _);
        Check(errors, endCache.Hits == 1 && !ReferenceEquals(after, hit) && PrefixState(after, player) == PrefixState(hit, player),
            "native EndTurn prefix hit must provide an independent complete owned state");
        var captured = PrefixState(hit, player);
        Check(errors, hit.EndTurn(player, 3, out _), "native cached EndTurn branch must remain independently advanceable");
        var again = endCache.CreateCursor(); again.End(0, root.Fork(), player, 2, out var untouched, out _, out _);
        Check(errors, PrefixState(untouched, player) == captured,
            "advancing an owned native cache hit must not mutate the saved EndTurn template");
        var c3 = endCache.CreateCursor(); c3.End(0, root.Fork(), player, 1, out _, out _, out _);
        Check(errors, endCache.Hits == 2 && PrefixState(root, player) == before,
            "native EndTurn prefix keys must isolate maximum-round parameters and keep the seed immutable");
    }

    private static void NativeChoicePresence(List<string> errors)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 198, 10));
        var combat = new CombatState(runState: RunState.CreateForTest([player], seed: "NATIVE-EMPTY-CHOICE-VECTOR"));
        player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 3;
        player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
        var survivor = combat.CreateCard<Survivor>(player); player.PlayerCombatState.Hand.AddInternal(survivor);
        player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(player));
        player.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendSilent>(player));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        var root = KernelSession.Capture(combat); var before = PrefixState(root, player);
        var action = new KernelTeamSearch.Action(player, survivor, null, CardStateKey: root.CardStateKey(survivor));
        var absent = root.ForkForReplay(); var supplied = root.ForkForReplay();
        Check(errors, !absent.ReplayPlanned(action, 1, out var pending) && pending.Contains("pending-choice")
            && supplied.ReplayPlanned(action with { Choices = [], ChoiceOrdinals = [] }, 1, out _, out var resolved)
            && supplied.Block(player.Creature) == 8 && resolved.Choices?.Count == 1,
            "native absent ordinal cursor must remain distinct from a captured empty default vector");
        Check(errors, PrefixState(root, player) == before,
            "native null versus empty vector replay must not pollute its original parent");
    }

    private static void Check(List<string> errors, bool ok, string message)
    {
        if (!ok) errors.Add(message);
    }
}
