using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Controlled native models and the real BotChoicePlanSync.Select caller chain.
// Reflection installs an in-flight transaction/executor without running a UI or
// a network queue. It does not replace Resolve, notification, or invalidation.
// R2 2026-10-07: 9 production faults RED exit 1, exact restoration GREEN exit 0.
// Commands and raw logs: work/decision-repair-20261007/choice-divergence-r2-results.json.
// System.Exception: Latest choice divergence: an unexpected second native menu in the same action must revoke dependent caches
// System.Exception: Latest choice divergence: changed native selection must revoke the legacy continuation
// System.Exception: Latest choice divergence: changed native selection must cancel the in-flight tournament search
// System.Exception: Latest choice divergence: changed native selection must revoke dependent refinement
// System.Exception: Latest choice divergence: changed native selection must clear all cached replay and end-turn comparisons
// System.Exception: Latest choice divergence: a fallback from another combat must preserve the active planner caches
// System.Exception: Latest choice divergence: a diverged action must notify dependent cache invalidation only once
// System.Exception: Latest choice divergence: changed native selection must revoke the tournament suffix through the actual Select path
internal static class LatestChoiceDivergenceScenarios
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why)
    { if (!ok) throw new Exception("Latest choice divergence: " + why); }

    internal static void Run()
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.latest-choice-divergence");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestChoiceDivergenceScenarios), nameof(Format)));
        var planner = (KernelCombatPlanner)GetStatic(typeof(BotRuntime), "KernelPlanner")!;
        var savedPlanner = typeof(KernelCombatPlanner).GetFields(Instance).Where(f => !f.IsInitOnly)
            .ToDictionary(f => f, f => f.GetValue(planner));
        var savedIdentity = GetStatic(typeof(BotRuntime), "_combatIdentity");
        var savedSync = typeof(BotChoicePlanSync).GetFields(Static).Where(f => !f.IsInitOnly)
            .ToDictionary(f => f, f => f.GetValue(null));
        try
        {
            var (p, c, enemy) = Board(140, "CHOICE-DIVERGENCE");
            var survivor = c.CreateCard<Survivor>(p); p.PlayerCombatState!.Hand.AddInternal(survivor);
            var strike = c.CreateCard<StrikeSilent>(p); p.PlayerCombatState.Hand.AddInternal(strike);
            var defend = c.CreateCard<DefendSilent>(p); p.PlayerCombatState.Hand.AddInternal(defend);
            var potion = ModelDb.Potion<Ambergris>().ToMutable(); p.AddPotionInternal(potion, silent: true);
            var root = KernelSession.Capture(c);
            var planned = new KernelTeamSearch.Action(p, strike, enemy, CardStateKey: root.CardStateKey(strike));
            var script = new[] { planned, planned };
            var record = TerminalRecord.Capture(root, [p], false, "fixture") with { Victory = true };
            var continuation = new KernelContinuation {
                Result = new(script, 0, 0, new Dictionary<string, int>(), "fixture", false, [], "", ""),
                Combat = c, Round = 1, Index = 1,
            };
            var executor = (ActionExecutor)RuntimeHelpers.GetUninitializedObject(typeof(ActionExecutor));
            var manager = (RunManager)RuntimeHelpers.GetUninitializedObject(typeof(RunManager));
            Set(manager, nameof(RunManager.ActionExecutor), executor);
            SetStatic(typeof(BotChoicePlanSync), "manager", manager);
            SetStatic(typeof(BotRuntime), "_combatIdentity", c);
            var keyed = new KernelChoice(p.NetId, survivor.Id.Entry, "Discard", PileType.Hand, "", 1, 1,
                [new(strike.Id.Entry, strike.CurrentUpgradeLevel, root.CardStateKey(strike), 0)]);
            var action = new PlayCardAction(p, NetCombatCard.ForTesting(0), survivor.Id, null);
            Set(action, nameof(GameAction.Id), (uint?)41);
            Set(executor, nameof(ActionExecutor.CurrentlyRunningAction), action);

            void Install(KernelChoice choice, PlayCardAction running, uint transaction = 41)
            {
                Set(executor, nameof(ActionExecutor.CurrentlyRunningAction), running);
                SetStatic(typeof(BotChoicePlanSync), "installed", new BotChoicePlanMessage {
                    Action = transaction, Bot = p.NetId, Card = running.CardModelId.Entry,
                });
                SetStatic(typeof(BotChoicePlanSync), "choices", new[] { choice });
                SetStatic(typeof(BotChoicePlanSync), "index", 0);
                SetStatic(typeof(BotChoicePlanSync), "choiceDiverged", false);
            }
            void Seed(bool tournament = true)
            {
                planner.Reset();
                Set(planner, "tournamentScript", tournament ? script : null);
                Set(planner, "tournamentScriptCombat", tournament ? c : null);
                Set(planner, "tournamentScriptIndex", 1);
                Set(planner, "tournamentScriptRecord", record);
                Set(planner, "tournamentScriptAliveStates", new[] { new[] { p.NetId }, new[] { p.NetId } });
                Set(planner, "tournamentScriptEnemyStates", new[] { new[] { enemy }, new[] { enemy } });
                Set(planner, "ContinuationSource", continuation);
                Set(planner, "planCommitted", true);
                Set(planner, "rootCombat", c); Set(planner, "rootSession", root);
                Set(planner, "search", new KernelTeamSearch(root, [p], _ => 0));
                Set(planner, "tournamentRoot", root); Set(planner, "tournamentParty", new[] { p });
                Set(planner, "tournamentRun", new TournamentRun(root, [p], [p], new(ReserveMs: 0), 100));
                Set(planner, "refinement", new KernelScriptRefinementRun(root, [p], [p], script, new(), 100));
                Set(planner, "refinementScript", script);
                Set(planner, "victoryReview", new KernelScriptReplayRun(root, [p], new(planned, record, [record], 0, 0, "fixture") { Script = script }, 80));
                Set(planner, "victoryReviewRoot", root); Set(planner, "victoryReviewStamp", "fixture");
                Set(planner, "shortVictory", new KernelShortVictoryRun(root, [p])); Set(planner, "shortVictoryStamp", "fixture");
                Set(planner, "endTurnComparison", new KernelEndTurnRun(root, p));
                Set(planner, "endTurnComparisonStamp", "fixture");
                Set(planner, "ConfirmedEndTurn", p);
                Set(planner, "ConfirmedPotion", new KernelCombatPlanner.PotionPlan(potion, p.Creature, "fixture"));
                Set(planner, "tournamentDeclined", "fixture"); Set(planner, "noActionStamp", "fixture");
            }
            void Intact(string why)
            {
                Check(ReferenceEquals(Get(planner, "tournamentScript"), script)
                    && ReferenceEquals(Get(planner, "ContinuationSource"), continuation)
                    && Get(planner, "refinement") is not null && planner.IsSearching
                    && planner.ConfirmedEndTurn == p, why);
            }
            void Cleared(bool tournament)
            {
                Check(!tournament || Get(planner, "tournamentScript") is null,
                    "changed native selection must revoke the tournament suffix through the actual Select path");
                Check(Get(planner, "tournamentScriptRecord") is null && Get(planner, "tournamentScriptAliveStates") is null
                    && Get(planner, "tournamentScriptEnemyStates") is null,
                    "changed native selection must revoke cached terminal and step evidence");
                Check(Get(planner, "ContinuationSource") is null && !(bool)Get(planner, "planCommitted")!,
                    "changed native selection must revoke the legacy continuation");
                Check(Get(planner, "refinement") is null && Get(planner, "refinementScript") is null,
                    "changed native selection must revoke dependent refinement");
                Check(!planner.IsSearching && Get(planner, "rootSession") is null && Get(planner, "rootCombat") is null,
                    "changed native selection must cancel the in-flight ordinary search");
                Check(Get(planner, "tournamentRun") is null && Get(planner, "tournamentRoot") is null
                    && Get(planner, "tournamentParty") is null,
                    "changed native selection must cancel the in-flight tournament search");
                Check(Get(planner, "victoryReview") is null && Get(planner, "victoryReviewRoot") is null
                    && Get(planner, "shortVictory") is null && Get(planner, "endTurnComparison") is null,
                    "changed native selection must clear all cached replay and end-turn comparisons");
                Check(planner.ConfirmedPotion is null && planner.ConfirmedEndTurn is null
                    && Get(planner, "tournamentDeclined") is null && (string)Get(planner, "noActionStamp")! == "",
                    "changed native selection must withdraw old confirmed actions and no-action verdicts");
            }

            Seed(); Install(keyed, action);
            Check(BotChoicePlanSync.Select(p, [strike], 1, 1)?.Single() == strike,
                "an exact planned native selection must keep its selected card");
            Intact("an exact planned native selection must preserve dependent caches");

            var brand = c.CreateCard<Brand>(p);
            var brandAction = new PlayCardAction(p, NetCombatCard.ForTesting(1), brand.Id, null);
            Set(brandAction, nameof(GameAction.Id), (uint?)41);
            Seed(); Install(new(p.NetId, brand.Id.Entry, "Exhaust", PileType.Hand, "", 0, 0, []), brandAction);
            Check(BotChoicePlanSync.Select(p, [], 1, 1) is { Count: 0 },
                "last-card Brand must resolve its legal native empty selection");
            Intact("last-card Brand must preserve dependent caches");

            Seed(); Install(keyed, action, transaction: 42);
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null
                && (int)GetStatic(typeof(BotChoicePlanSync), "index")! == 0,
                "a different running action must leave the transaction untouched");
            Intact("a different running action must preserve dependent caches");

            Seed(); Install(keyed, action); SetStatic(typeof(BotChoicePlanSync), "installed", null);
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null,
                "a free native menu without an installed transaction must keep fallback available");
            Intact("a free native menu without an installed transaction must preserve dependent caches");

            var (_, otherCombat, _) = Board(141, "CHOICE-OTHER-COMBAT");
            Seed(); SetStatic(typeof(BotRuntime), "_combatIdentity", otherCombat); Install(keyed, action);
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null, "a stale transaction still yields fallback");
            Intact("a fallback from another combat must preserve the active planner caches");
            SetStatic(typeof(BotRuntime), "_combatIdentity", c);

            Seed(); Install(keyed, action);
            Check(BotChoicePlanSync.Select(p, [strike], 1, 1)?.Single() == strike,
                "the first native menu must consume its one planned choice normally");
            Intact("the first planned menu must preserve the continuation before an unexpected extra menu");
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null
                && Get(planner, "tournamentScript") is null,
                "an unexpected second native menu in the same action must revoke dependent caches");
            Cleared(tournament: true);
            Check((bool)GetStatic(typeof(BotChoicePlanSync), "choiceDiverged")!,
                "unexpected native choices must record the one-shot divergence notification");
            // A third menu in this same diverged action must keep fallback but
            // must not clear work again after the original notification.
            Seed();
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null,
                "later menus in a diverged action must still permit native fallback");
            Intact("a diverged action must notify dependent cache invalidation only once");

            Seed(); Install(keyed, action);
            var hp = p.Creature.CurrentHp; var hand = p.PlayerCombatState.Hand.Cards.ToArray();
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null
                && (int)GetStatic(typeof(BotChoicePlanSync), "index")! == 1,
                "changed options must end the remaining planned transaction and leave native fallback available");
            Cleared(tournament: true);
            Check(ReferenceEquals(executor.CurrentlyRunningAction, action) && !action.CompletionTask.IsCompleted
                && p.Creature.CurrentHp == hp && p.PlayerCombatState.Hand.Cards.SequenceEqual(hand),
                "choice invalidation must preserve the running action and wait for native settlement before replanning");

            // ClearTournamentScript early-returns for this legacy-only path. The
            // other comparison caches must still be invalidated by the notification.
            Seed(tournament: false); Install(keyed, action);
            Check(BotChoicePlanSync.Select(p, [defend], 1, 1) is null, "legacy-only changed choice must yield fallback");
            Cleared(tournament: false);
            Console.WriteLine("PASS: actual native planned-choice fallback clears dependent scripts, searches and comparisons while preserving legal, empty, other-action and other-combat choices.");
        }
        finally
        {
            planner.Reset();
            foreach (var pair in savedPlanner) pair.Key.SetValue(planner, pair.Value);
            foreach (var pair in savedSync) pair.Key.SetValue(null, pair.Value);
            SetStatic(typeof(BotRuntime), "_combatIdentity", savedIdentity);
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static (Player, CombatState, Creature) Board(ulong id, string seed)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, id);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: seed));
        p.ResetCombatState(); c.AddPlayer(p); c.RoundNumber = 1; p.MaxEnergy = 3;
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        var enemy = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (p, c, enemy);
    }
    private static object? Get(object obj, string member) => obj.GetType().GetField(member, Instance)?.GetValue(obj)
        ?? obj.GetType().GetProperty(member, Instance)?.GetValue(obj);
    private static void Set(object obj, string member, object? value)
    {
        for (var type = obj.GetType(); type is not null; type = type.BaseType)
        {
            var flags = Instance | BindingFlags.DeclaredOnly;
            if (type.GetField(member, flags) is { } field) { field.SetValue(obj, value); return; }
            if (type.GetProperty(member, flags) is { SetMethod: not null } property)
            { property.SetValue(obj, value); return; }
            if (type.GetField("<" + member + ">k__BackingField", flags) is { } backing)
            { backing.SetValue(obj, value); return; }
        }
        throw new MissingMemberException(obj.GetType().Name, member);
    }
    private static object? GetStatic(Type type, string field) => type.GetField(field, Static)!.GetValue(null);
    private static void SetStatic(Type type, string field, object? value) => type.GetField(field, Static)!.SetValue(null, value);
}
