// V2 focused R2 2026-10-24: actual production build0/RED1 -> exact restore -> build0/GREEN0.
// Receipt: work/v2-validation/r2/focus-pass/receipts.json
// focus-before-prepared-return: System.InvalidOperationException: Mixed combat auxiliary: changing manual focus must retire the old script and both replay proposals without a live action
// aux-prepared-not-consumed: System.InvalidOperationException: Mixed combat auxiliary: an adopted mixed proposal must retain its controlled opening and pre-action lease without consuming a merely prepared step
// aux-end-waits-human: System.InvalidOperationException: Mixed combat auxiliary: a legal historical EndTurn must remain parked while humans act instead of forcing a foreground rescue search
// mixed-end-no-blocking-comparison: System.InvalidOperationException: Mixed combat auxiliary: human completion must release the same historical owner's recorded EndTurn contract without mandatory card or bottle comparison
using System.Diagnostics;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// These regressions enter the production tournament through Poll. Cached
// forecasts are fixture inputs. V2 retains legal historical proposals while
// optional quality/rescue work cannot preempt them; preparation is not payment.
// Potion/card lease R2 lines below are historical 0.39.3 policy receipts,
// superseded by the V2 cases; new mutation evidence is recorded separately.
// R2 actual 2026-10-24: production mutant build0 / RED1 / exact restore / GREEN0.
// Receipt: work/mixed-fixes-20261023T164339Z/r2-valid-proofs.json
// manual-focus: System.InvalidOperationException: Mixed combat auxiliary: default mixed tournament must attack the human's focused enemy before the human ends the turn
// manual-focus-change: System.InvalidOperationException: Mixed combat auxiliary: production Poll exceeded bounded fixture time
// focus-survival: System.InvalidOperationException: Mixed combat auxiliary: manual focus must yield to the actual lethal opening that prevents the mixed party's death
// focus-no-seat-trade: System.InvalidOperationException: Mixed combat auxiliary: equal objective totals cannot let a focus tiebreak trade the surviving human for a Bot
// potion-rescue-lease: System.InvalidOperationException: Mixed combat auxiliary: each adopted mixed rescue must retain a continuation lease at the actually emitted opening cursor
// card-rescue-lease: System.InvalidOperationException: Mixed combat auxiliary: a freshly adopted native card rescue must not be unnecessarily dropped and re-rooted before its first action: native emergency card repair;committed action window requires a new root
// rescue-pre-action-root: System.InvalidOperationException: Mixed combat auxiliary: the rescue lease must capture the pre-action live root rather than its already-consumed ending: cacheMissAxis=semantics field=p1.hp expected=71 actual=80 offset=13
internal static class MixedCombatAuxiliaryScenarios
{
    private static KernelCombatPlanner? observedRescuePlanner;
    private static readonly List<string> rescueDropCauses = new();
    private static PlayCardAction? leaseAction;
    private static TeamCombatPlanner.Decision? leaseDecision;
    private static bool LeaseCardBody(PlayCardAction __instance, ref Task __result)
    {
        if (!ReferenceEquals(__instance, leaseAction)) return true;
        var card = leaseDecision!.Move.Card;
        var paid = card.SpendResources().GetAwaiter().GetResult();
        card.OnPlayWrapper(new ThrowingPlayerChoiceContext(), leaseDecision.Move.Target, false,
            new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
        __result = Task.CompletedTask; return false;
    }

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.mixed-combat-aux.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedCombatAuxiliaryScenarios), nameof(Format)));
        presentation.Patch(AccessTools.Method(typeof(RunManager), nameof(RunManager.HasAscension)),
            prefix: new HarmonyMethod(typeof(MixedCombatAuxiliaryScenarios), nameof(A10)));
        presentation.Patch(AccessTools.Method(typeof(KernelCombatPlanner), "ClearTournamentScript"),
            prefix: new HarmonyMethod(typeof(MixedCombatAuxiliaryScenarios), nameof(ObserveScriptDrop)));
        var forcedTournament = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null; // Exercise the actual default.
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("focus", ManualFocus), ("focus-change", FocusChange), ("focus-safety", FocusSafety),
                ("potion-lease", PotionLease), ("card-lease", CardLease) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: mixed combat auxiliary " + test.Name); }
        }
        finally
        {
            observedRescuePlanner = null; rescueDropCauses.Clear();
            KernelCombatPlanner.TournamentOverride = forcedTournament;
            presentation.UnpatchAll(presentation.Id);
        }
    }

    private sealed record Board(CombatState Combat, Player Human, Player Bot, Creature[] Enemies) : IDisposable
    {
        private readonly IDisposable native = (IDisposable)Activator.CreateInstance(
            AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic, null, [Combat], null)!;
        public void Dispose() => native.Dispose();
    }
    private static Board Create(int botHp = 80, bool prism = false, int enemyCount = 2, int attack = 1)
    {
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 1, 123));
        var combat = new CombatState(runState: RunState.CreateForTest([human, bot], ascensionLevel: 10,
            seed: "MIXED-COMBAT-AUXILIARY")) { RoundNumber = 1 };
        foreach (var p in new[] { human, bot })
        {
            p.MaxEnergy = 3; p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(p == bot ? botHp : 80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(p == bot ? 1 : 0);
        }
        var enemies = Enumerable.Range(0, enemyCount).Select(index => {
            MonsterModel monster = prism ? ModelDb.Monster<InfestedPrism>().ToMutable()
                : ModelDb.Monster<MockAttackMonster>().ToMutable();
            var enemy = combat.CreateCreature(monster, CombatSide.Enemy, "aux-" + index);
            combat.AddCreature(enemy); monster.SetUpForCombat();
            enemy.SetMaxHpInternal(820); enemy.SetCurrentHpInternal(prism ? 820 : 36);
            monster.SetMoveImmediate((MoveState)monster.MoveStateMachine!.States[prism ? "RADIATE_MOVE" : "ATTACK"], true);
            if (prism)
            {
                ModelDb.Power<VitalSparkPower>().ToMutable().ApplyInternal(enemy, 3, true);
                ModelDb.Power<WeakPower>().ToMutable().ApplyInternal(enemy, 2, true);
            }
            else if (attack > 1) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, attack - 1, true);
            return enemy;
        }).ToArray();
        return new(combat, human, bot, enemies);
    }

    private static CardModel Hand<T>(Board board, bool tainted = false) where T : CardModel
    {
        var card = board.Combat.CreateCard<T>(board.Bot);
        board.Bot.PlayerCombatState!.Hand.AddInternal(card);
        if (tainted) card.AfflictInternal(ModelDb.Affliction<Tainted>().ToMutable(), 3);
        return card;
    }

    private static TeamCombatPlanner.Decision? Poll(KernelCombatPlanner planner, Board board, uint? focus,
        uint actionVersion = 0, bool humansFinished = false)
    {
        // This regression measures production routing and target intent, not
        // process startup/JIT cost. Warm the native candidate/settlement code
        // outside the actual short interaction request, on private forks only.
        var warmRoot = KernelCombatCapture.Capture(board.Combat);
        _ = KernelTournament.Run(warmRoot, board.Combat.Players.ToArray(), [board.Bot],
            new TournamentOptions(ReserveMs: 0, IncludePotions: false, IncludeEndTurns: false,
                UsePolicyPortfolio: false, Rollout: new(MaxRounds: 1, MaxSteps: 32)), 5000);
        if (Field<IReadOnlyList<KernelTeamSearch.Action>?>(planner, "tournamentScript") is { } cached
            && board.Bot.Potions.Any())
        {
            var warmPlanner = new KernelCombatPlanner();
            var trial = (IEnumerable<bool>)AccessTools.Method(typeof(KernelCombatPlanner), "EmergencyPotionComparisons")
                .Invoke(warmPlanner, [warmRoot, new[] { board.Bot }, cached])!;
            foreach (var _ in trial) { }
        }
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 10000)
        {
            var status = planner.Poll(board.Combat, [board.Bot], actionVersion, focus,
                humansFinished, BotDifficulty.Pro, out var decision);
            if (status == KernelCombatPlanner.Status.Ready) return decision;
            Check(status != KernelCombatPlanner.Status.Fallback, "default production Poll must retain tournament authority");
        }
        throw new InvalidOperationException("Mixed combat auxiliary: production Poll exceeded bounded fixture time");
    }

    private static void ManualFocus()
    {
        foreach (var targetIndex in new[] { 1, 0 })
        {
            using var b = Create(); Hand<StrikeIronclad>(b);
            var planner = new KernelCombatPlanner();
            var decision = Poll(planner, b, b.Enemies[targetIndex].CombatId);
            Check(decision?.Move.Target == b.Enemies[targetIndex] && b.Human.PlayerCombatState!.Phase == PlayerTurnPhase.Play,
                "default mixed tournament must attack the human's focused enemy before the human ends the turn");
            planner.Reset();
        }
    }

    private static void FocusChange()
    {
        using var b = Create(); Hand<StrikeIronclad>(b);
        var planner = new KernelCombatPlanner();
        Check(Poll(planner, b, b.Enemies[0].CombatId)?.Move.Target == b.Enemies[0],
            "first human target command must reach the production opening");
        var script = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        var old = Field<TerminalRecord>(planner, "tournamentScriptRecord");
        var proposal = new TournamentResult(script[0], old, [], 0, 0, "old-focus-proposal") { Script = script };
        Set(planner, "mixedProposal", proposal); Set(planner, "mixedReadyProposal", proposal);
        // The board and queue version intentionally stay unchanged: target
        // intent itself must invalidate the old cached result and replay offer.
        var changed = Poll(planner, b, b.Enemies[1].CombatId);
        Check(changed?.Move.Target == b.Enemies[1]
            && Field<object?>(planner, "mixedProposal") is null && Field<object?>(planner, "mixedReadyProposal") is null,
            "changing manual focus must retire the old script and both replay proposals without a live action");
        planner.Reset();
    }

    private static void FocusSafety()
    {
        using var b = Create(enemyCount: 2, attack: 20); Hand<StrikeIronclad>(b);
        b.Bot.Creature.SetCurrentHpInternal(21); b.Human.Creature.SetCurrentHpInternal(21);
        b.Enemies[1].SetCurrentHpInternal(6);
        var planner = new KernelCombatPlanner();
        var decision = Poll(planner, b, b.Enemies[0].CombatId);
        Check(decision?.Move.Target == b.Enemies[1],
            "manual focus must yield to the actual lethal opening that prevents the mixed party's death");
        var root = KernelCombatCapture.Capture(b.Combat);
        var focused = new KernelTeamSearch.Action(b.Bot, decision!.Move.Card, b.Enemies[0]);
        var safe = focused with { Target = b.Enemies[1] };
        var options = new TournamentOptions { ManualFocusTargetCombatId = b.Enemies[0].CombatId };
        var saveHuman = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [1, 0], 1, 0);
        var tradeHuman = saveHuman with { PostCombatHp = new[] { 0, 1 } };
        Check(options.Compare(tradeHuman, focused, saveHuman, safe) == 0,
            "equal objective totals cannot let a focus tiebreak trade the surviving human for a Bot");
        planner.Reset();
    }

    private static void PotionLease()
    {
        using var b = Create(botHp: 6, prism: true, enemyCount: 1);
        var defend = Hand<DefendSilent>(b, tainted: true);
        var potion = ModelDb.Potion<DexterityPotion>().ToMutable(); b.Bot.AddPotionInternal(potion, silent: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        var losing = CachedActions(b, root, defend);
        var planner = Cached(b, root, losing);
        try
        {
            var status = planner.Poll(b.Combat, [b.Bot], 0, null, false, BotDifficulty.Pro, out var decision);
            Check(status == KernelCombatPlanner.Status.Ready && decision?.Move.Card == defend
                && planner.ConfirmedPotion is null && b.Bot.Potions.Contains(potion)
                && Field<int>(planner, "tournamentPasses") == 0
                && Field<int>(planner, "emergencyPotionCaptures") == 0,
                "a legal historical Defend must be prepared immediately while optional potion rescue cannot preempt or block it");
            VerifyLease(planner, b, root, losing[0]);
            var bridge = new Harmony("coopbots.mixed-aux.lease-native-body");
            bridge.Patch(AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"),
                prefix: new HarmonyMethod(typeof(MixedCombatAuxiliaryScenarios), nameof(LeaseCardBody)));
            var action = new PlayCardAction(b.Bot, NetCombatCard.ForTesting(0), defend.Id, decision!.Move.Target?.CombatId);
            var queue = new ActionQueueSet([b.Human, b.Bot]); queue.CombatStarted();
            leaseAction = action; leaseDecision = decision;
            try
            {
                planner.ConfirmActionQueued(action); queue.EnqueueWithoutSynchronizing(action);
                action.Execute().GetAwaiter().GetResult();
            }
            finally { leaseAction = null; leaseDecision = null; bridge.UnpatchAll(bridge.Id); }
            Check(Field<int>(planner, "tournamentScriptIndex") == 1 && !planner.HasPreparedSubmission
                && b.Bot.PlayerCombatState!.Energy == 0 && !b.Bot.PlayerCombatState.Hand.Cards.Contains(defend)
                && b.Bot.Potions.Contains(potion),
                "the prepared historical card must actually pay and settle once before its lease cursor advances, without spending the optional bottle");
            var lease = Field<KernelPlanContinuation>(planner, "mixedContinuation");
            Check(lease.AdvanceTo(1, TimeSpan.FromSeconds(1)) && lease.Failure.Length == 0,
                "the pre-action lease must replay the actually settled card rather than start from its already-consumed ending");
        }
        finally { planner.Reset(newCombat: true); }
    }

    private static void CardLease()
    {
        using var b = Create(botHp: 6, enemyCount: 1, attack: 8);
        var defend = Hand<DefendSilent>(b);
        var potion = ModelDb.Potion<DexterityPotion>().ToMutable(); b.Bot.AddPotionInternal(potion, silent: true);
        var root = KernelCombatCapture.Capture(b.Combat);
        var parked = CachedActions(b, root);
        var planner = Cached(b, root, parked);
        rescueDropCauses.Clear(); observedRescuePlanner = planner;
        try
        {
            for (var tick = 0; tick < 20; tick++)
                Check(planner.Poll(b.Combat, [b.Bot], 0, null, false, BotDifficulty.Pro, out var decision)
                    == KernelCombatPlanner.Status.Pending && decision is null && planner.ConfirmedEndTurn is null
                    && Field<int>(planner, "tournamentScriptIndex") == 0
                    && ReferenceEquals(Field<object>(planner, "tournamentScript"), parked),
                    "a legal historical EndTurn must remain parked while humans act instead of forcing a foreground rescue search");
            Check(planner.Poll(b.Combat, [b.Bot], 0, null, true, BotDifficulty.Pro, out var noCard)
                == KernelCombatPlanner.Status.Ready && noCard is null && planner.ConfirmedEndTurn == b.Bot
                && ReferenceEquals(planner.ConfirmedEndTurnChoices, parked[0].TurnStartChoices)
                && planner.ConfirmedEndTurnChoices is { Complete: true }
                && Field<int>(planner, "tournamentPasses") == 0
                && Field<int>(planner, "emergencyPotionCaptures") == 0,
                "human completion must release the same historical owner's recorded EndTurn contract without mandatory card or bottle comparison");
            Check(b.Bot.PlayerCombatState!.Hand.Cards.Contains(defend) && b.Bot.Potions.Contains(potion)
                && rescueDropCauses.Count == 0,
                "parking and preparing EndTurn cannot spend a live card, bottle or silently replace the historical line");
            VerifyLease(planner, b, root, parked[0]);
            // Fresh EndTurn native receipt consumption is exercised by the
            // multi-frame MixedStableScript fresh-end case, not duplicated here.
        }
        finally { observedRescuePlanner = null; planner.Reset(newCombat: true); }
    }

    private static KernelTeamSearch.Action[] CachedActions(Board b, KernelSession root, params CardModel[] cards)
        => TurnStartFixtureCapture.Record(root, cards.Select(card => new KernelTeamSearch.Action(b.Bot, card,
            root.Targets(card).FirstOrDefault())).Concat(new[] {
                new KernelTeamSearch.Action(b.Bot, null, null, EndTurn: true),
                new KernelTeamSearch.Action(b.Human, null, null, EndTurn: true) }), maxRounds: 1);

    private static KernelCombatPlanner Cached(Board b, KernelSession root, KernelTeamSearch.Action[] script)
    {
        var planner = new KernelCombatPlanner();
        Set(planner, "tournamentRoot", root);
        var forecast = new TerminalRecord(false, EvidenceKind.VerifiedTerminal, "fixture-cached-loss", [0, 0], 2, 0);
        var result = new TournamentResult(script[0], forecast, [forecast], 1, 0, "fixture") {
            Script = script, ScriptAliveStates = script.Select(_ => b.Combat.Players.Select(p => p.NetId).Order().ToArray()).ToArray(),
            ScriptEnemyStates = script.Select(_ => b.Enemies).ToArray() };
        AccessTools.Method(typeof(KernelCombatPlanner), "AdoptTournamentScript").Invoke(planner, [result, b.Combat]);
        Set(planner, "tournamentScriptIndex", 0);
        return planner;
    }

    private static void VerifyLease(KernelCombatPlanner planner, Board b, KernelSession root,
        KernelTeamSearch.Action opening)
    {
        var script = Field<IReadOnlyList<KernelTeamSearch.Action>>(planner, "tournamentScript");
        var continuation = Field<KernelPlanContinuation?>(planner, "mixedContinuation");
        var cursor = Field<int>(planner, "tournamentScriptIndex");
        Check(continuation is not null && cursor == 0 && planner.HasPreparedSubmission
            && ReferenceEquals(script[0], opening) && opening.Player == b.Bot,
            "an adopted mixed proposal must retain its controlled opening and pre-action lease without consuming a merely prepared step");
        Check(continuation!.Evidence.Matches(root.PlanningState([b.Bot.NetId])),
            "the mixed proposal lease must capture the actual pre-action root rather than its already-consumed ending: "
            + continuation.Evidence.FirstMismatch(root.PlanningState([b.Bot.NetId])));
        Check(continuation.AdvanceTo(0, TimeSpan.Zero) && continuation.Cursor == 0
            && continuation.Failure.Length == 0 && continuation.Evidence.Matches(root.PlanningState([b.Bot.NetId])),
            "reviewing an unsubmitted opening must perform zero prefix actions and retain its pre-action evidence");
    }

    private static T Field<T>(object owner, string name) => (T)AccessTools.Field(owner.GetType(), name).GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => AccessTools.Field(owner.GetType(), name).SetValue(owner, value);
    private static void ObserveScriptDrop(KernelCombatPlanner __instance, string reason)
    { if (ReferenceEquals(__instance, observedRescuePlanner)) rescueDropCauses.Add(reason); }
    private static void Check(bool okay, string reason)
    { if (!okay) throw new InvalidOperationException("Mixed combat auxiliary: " + reason); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool A10(AscensionLevel level, ref bool __result) { __result = (int)level <= 10; return false; }
}
