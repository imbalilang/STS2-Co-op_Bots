using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.CardSelection;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Owner requested Touch only, 2026-10-11. Its native paid-prefix effect is
// verified independently; complete native terminal remains a Steam gate.
// R2 actual evidence 2026-10-11: all 14 production-source faults built 0,
// failed 1, physically restored 13 sources + 6 DLL/PDB, rebuilt 0 and passed 0.
// Commands/raws: work/v15-touch-only-private-preparation-20261011-v1/r2-v2/results.json.
// First nine use their original fixture; last five use both native target menus
// with spare eligible cards. The first group-10 mutant returned 0 and is retained
// as nonqualifying evidence, not a RED. Production source is unchanged by that refinement.
// missing-before-mutation: "System.InvalidOperationException: Touch contract: missing Touch choices must soft-refuse before native Prepare"
// native-combat-free-duration: "System.InvalidOperationException: Touch runtime: real potion consumption and selected recipient cost must match full projected future state"
// physical-kernel-bottle: "System.InvalidOperationException: Touch runtime: real potion consumption and selected recipient cost must match full projected future state"
// physical-core-emitter: "System.InvalidOperationException: Touch runtime: the actual Core emitter must retain the original selected physical bottle and its exact choices"
// captured-potion-source: "System.InvalidOperationException: Touch runtime: captured potion model ID must agree with the actual source even when payload MenuSource is unchanged"
// typed-potion-ack-resume: "System.InvalidOperationException: Touch runtime: the actual current ACK must return the same potion rather than repolling or mutating the board"
// potion-kind-wire: "System.InvalidOperationException: Touch runtime: native packet roundtrip must retain potion binding and the recipient's complete choice contract"
// stopped-prefix-admission: "System.InvalidOperationException: Touch contract: a stopped request must not start even the first new potion lifecycle prefix"
// native-choice-cost-order: "System.InvalidOperationException: Touch contract: Touch must free only the recipient's selected Dash and consume the owner's bottle"
// physical-bottle-topk-coverage: "System.InvalidOperationException: Touch contract: one Touch bottle's many menu alternatives must not crowd an ordinary same-seat bottle out of TopK"
// formal-first-action-choice-wiring: "System.InvalidOperationException: Touch contract: production first-actions must discover recipient Dash Touch without a manual paid prefix"
// human-recipient-exclusion: "System.InvalidOperationException: Touch contract: an undelegated human recipient must retain their native menu"
// potion-action-target-binding: "System.InvalidOperationException: Touch runtime: wrong target for the matching potion transaction must invalidate"
// capture-before-removal-slot: "System.InvalidOperationException: Touch runtime: planned-menu synchronization must bind the original selected physical potion slot"
internal static class KernelTouchPotionScenarios
{
    internal static void Run(string? only = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.touch-choice.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(KernelTouchPotionScenarios), nameof(Format)));
        try
        {
            if (only is null or "missing") MissingContract();
            if (only is null or "recipient") RecipientBranches();
            if (only is null or "menu") MenuContract();
            if (only is null or "first-actions") FirstActions();
            if (only is null or "stopped") StoppedPrefix();
            if (only is null or "native")
            { TouchRuntimeFixtureSnippet.NativePaidRuntime(); Console.WriteLine("PASS: Touch production native native/action/choice contract."); }
            if (only is null or "duplicate")
            { TouchRuntimeFixtureSnippet.NativeSecondSlotPaidRuntime(); Console.WriteLine("PASS: Touch production duplicate native/action/choice contract."); }
            if (only is null or "ordinary-duplicate")
            { TouchRuntimeFixtureSnippet.NativeOrdinarySecondSlotPaidRuntime(); Console.WriteLine("PASS: ordinary potion second-slot Core/native/full-Facts contract."); }
            if (only is null or "runtime-menu")
            { TouchRuntimeFixtureSnippet.NativeMenuGuards(); Console.WriteLine("PASS: Touch production runtime-menu native/action/choice contract."); }
            if (only is null or "ack")
            { TouchRuntimeFixtureSnippet.ActualPrepareAckResume(); Console.WriteLine("PASS: Touch production ack native/action/choice contract."); }
            if (only is null or "wire")
            { TouchRuntimeFixtureSnippet.PotionWireRoundtrip(); Console.WriteLine("PASS: Touch production wire native/action/choice contract."); }
            if (only is null or "human")
            { TouchRuntimeFixtureSnippet.NativeHumanRecipientControl(); Console.WriteLine("PASS: Touch production human native/action/choice contract."); }
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }

    private static void MissingContract()
    {
        var (combat, party, potion, _) = Board();
        var root = KernelSession.Capture(combat); var ids = party.Select(p => p.NetId).ToArray();
        var before = root.PlanningState(ids);
        Check(KernelSession.CanModelPotion(potion), "Touch is now modelable with its explicit choice adapter");
        Check(!root.UsePotion(potion, party[0].Creature, out var reason) && reason == "potion-choice-required",
            "missing Touch choices must soft-refuse before native Prepare");
        Check(root.HoldsPotion(potion) && root.PlanningState(ids).Semantics == before.Semantics
            && root.CanFork, "missing Touch choices must preserve bottle, complete root facts and forkability");
        Console.WriteLine("PASS: Touch missing contract leaves the physical root and bottle untouched.");
    }

    private static void RecipientBranches()
    {
        var (combat, party, potion, dash) = Board();
        var root = KernelSession.Capture(combat); var ids = party.Select(p => p.NetId).ToArray();
        var frozen = root.PlanningState(ids);
        Check(root.PotionActions(potion, party[1].Creature, controlledActors: [party[0]]).Count == 0,
            "an undelegated human recipient must retain their native menu");
        var branches = root.PotionBranches(potion, party[1].Creature, controlledActors: party)
            .Where(b => b.Boundary.Length == 0).ToArray();
        Check(branches.Length == 3, "Touch must enumerate all three eligible physical recipient choices");
        var dashBranch = branches.Single(b => b.Choices[0].Cards[0].Id == dash.Id.Entry);
        Check(dashBranch.Choices[0].Owner == party[1].NetId && dashBranch.ChoiceOrdinals.SequenceEqual(new[] { 2 }),
            "recipient ownership and native filtered menu ordinal must survive cost-priority ordering");
        Check(branches[0] == dashBranch && Cost(dashBranch.State, dash) == 0
            && Cost(dashBranch.State, root.Hand(party[0]).Single()) == 1
            && !dashBranch.State.HoldsPotion(potion), "Touch must free only the recipient's selected Dash and consume the owner's bottle");
        var action = new KernelTeamSearch.Action(party[0], null, party[1].Creature, potion,
            Choices: dashBranch.Choices, ChoiceOrdinals: dashBranch.ChoiceOrdinals);
        var replay = root.Fork();
        Check(replay.ReplayPlanned(action, 80, out var why) && replay.PlanningState(ids).Semantics
            == dashBranch.State.PlanningState(ids).Semantics, "exact planned Touch replay equals its settled native-mirror branch: " + why);
        Check(replay.CanFork && replay.Fork().PlanningState(ids).Semantics == replay.PlanningState(ids).Semantics,
            "completed Touch branch must remain reusable by the continuation");
        Check(root.PlanningState(ids).Semantics == frozen.Semantics && potion.Owner.Potions.Contains(potion)
            && dash.EnergyCost.GetWithModifiers(CostModifiers.All) == 2,
            "all Touch branches and replay preserve live cards, inventory and the original root");
        Console.WriteLine("PASS: Touch uses recipient-aware native menu branches, original ordinal and exact replay.");
    }

    private static void MenuContract()
    {
        var (combat, party, potion, _) = Board();
        var root = KernelSession.Capture(combat);
        var action = root.PotionActions(potion, party[1].Creature, controlledActors: party).First();
        var choice = action.Choices!.Single();
        foreach (var changed in new[] { choice with { Owner = party[0].NetId },
            choice with { MenuSource = "OTHER_POTION" }, choice with { Options = choice.Options!.Reverse().ToArray() },
            choice with { Cards = [choice.Cards[0] with { Occurrence = 4 }] } })
        {
            var fork = root.Fork();
            Check(!fork.ReplayPlanned(action with { Choices = [changed] }, 80, out _),
                "Touch rejects owner/source/complete-menu/physical-occurrence mismatches");
        }
        var missing = root.Fork();
        Check(!missing.ReplayPlanned(action with { Choices = null }, 80, out _)
            && missing.HoldsPotion(potion) && missing.CanFork,
            "a transported action missing choices must not mutate inventory");
        Console.WriteLine("PASS: Touch exact source, recipient, complete menu and physical selected-token guards.");
    }

    private static void FirstActions()
    {
        var (combat, party, potion, dash) = Board();
        var root = KernelSession.Capture(combat);
        var options = new TournamentOptions(TopK: 6, IncludePotions: true, PotionsOnly: true,
            IncludeEndTurns: false, ContinuationActors: party);
        var actions = KernelTournament.FirstActions(root, party, 6, options);
        Check(actions.Any(a => a.Potion == potion && a.Target == party[1].Creature
            && a.Choices?.Single().Cards.Single().Id == dash.Id.Entry),
            "production first-actions must discover recipient Dash Touch without a manual paid prefix");
        Check(actions.Where(a => a.Potion == potion).All(a => a.Choices is { Count: 1 }
            && a.ChoiceOrdinals is { Count: 1 }), "every offered Touch opening must carry its executable choice contract");
        for (var i = 0; i < 8; i++) party[1].PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeSilent>(party[1]));
        // Both native target menus need spare alternatives: a one-card owner menu
        // is exhausted by role coverage and would let old target coverage retain Energy.
        for (var i = 0; i < 8; i++) party[0].PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(party[0]));
        party[0].AddPotionInternal(ModelDb.Potion<EnergyPotion>().ToMutable(), silent: true);
        root = KernelSession.Capture(combat);
        actions = KernelTournament.FirstActions(root, party, 6, options);
        Check(actions.Any(a => a.Potion is EnergyPotion),
            "one Touch bottle's many menu alternatives must not crowd an ordinary same-seat bottle out of TopK");
        Console.WriteLine("PASS: production first-actions discover concrete Touch choices under existing TopK.");
    }

    private static void StoppedPrefix()
    {
        var (combat, party, potion, _) = Board();
        var root = KernelSession.Capture(combat);
        var request = new KernelDecisionRequest(15000, 9600, 6); request.Cancel("fixture-native-prefix-not-started");
        using var scope = KernelSearchBudgetScope.Enter(request);
        Check(!root.PotionBranches(potion, party[1].Creature, controlledActors: party).Any(),
            "a stopped request must not start even the first new potion lifecycle prefix");
        Check(root.HoldsPotion(potion) && root.CanFork, "stopped new-prefix admission preserves the original bottle/root");
        Console.WriteLine("PASS: Touch refuses to start a new lifecycle prefix after the shared request stops.");
    }

    private static (CombatState Combat, Player[] Party, PotionModel Potion, Dash Dash) Board()
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var recipient = Player.CreateForNewRun<Deprived>(UnlockState.all, 71001);
        Player[] party = [owner, recipient];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "TOUCH-CONTRACT")) { RoundNumber = 1 };
        foreach (var player in party)
        {
            player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 3;
            player.PlayerCombatState!.Phase = PlayerTurnPhase.Play; player.PlayerCombatState.GainEnergy(3);
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        owner.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        recipient.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeSilent>(recipient));
        recipient.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(recipient));
        var dash = combat.CreateCard<Dash>(recipient); recipient.PlayerCombatState.Hand.AddInternal(dash);
        owner.AddPotionInternal(ModelDb.Potion<TouchOfInsanity>().ToMutable(), silent: true);
        return (combat, party, owner.Potions.Single(), dash);
    }
    private static decimal Cost(KernelSession session, CardModel card)
        => session.Simulator.State.GetPlayerCombatState(card.Owner).Hand.Cards.Single(c => c.References(card))
            .Preview.EnergyCost.GetWithModifiers(CostModifiers.All);
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("Touch contract: " + message); }

    internal static void RunF28(string entry)
    {
        TestEnvironment.Ensure();
        var handed = AutoPilot.HandedOver.ToArray();
        var presentation = new Harmony("coopbots.touch-f28-production-choice.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(KernelTouchPotionScenarios), nameof(Format)));
        try
        {
            var save = JsonSerializer.Deserialize(File.ReadAllText(entry), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
            var run = RunState.FromSerializable(save);
            Check(run.Players.Count == 4 && run.AscensionLevel == 10, "actual save has four seats and A10");
            using var manager = new SavedManager(run, save);
            var encounter = ModelDb.All.OfType<EncounterModel>().Single(e => e.Id.Entry == "ENTOMANCER_ELITE").ToMutable();
            var room = new CombatRoom(encounter, run);
            run.PushRoom(room);
            var combat = room.CombatState;
            foreach (var p in run.Players) combat.AddPlayer(p);
            using var native = NativeScope(combat);
            encounter.GenerateMonstersWithSlots(run);
            foreach (var (model, slot) in encounter.MonstersWithSlots)
            {
                var creature = combat.CreateCreature(model, CombatSide.Enemy, slot);
                combat.AddCreature(creature);
            }
            // Actual combat setup resets/clone/shuffles every saved player's deck.
            AccessTools.Field(typeof(CombatManager), "_turnState").SetValue(CombatManager.Instance, null);
            CombatManager.Instance.SetUpCombat(combat);
            AccessTools.Property(AccessTools.Field(typeof(CombatManager), "_turnState").FieldType, "IsInProgress")
                .SetValue(manager.Turn, true);
            AccessTools.Property(AccessTools.Field(typeof(CombatManager), "_turnState").FieldType, "IsStarting")
                .SetValue(manager.Turn, false);
            foreach (var creature in combat.Creatures) CombatManager.Instance.AfterCreatureAdded(creature).GetAwaiter().GetResult();
            Hook.AfterRoomEntered(run, room).GetAwaiter().GetResult();
            Hook.BeforeCombatStart(run, combat).GetAwaiter().GetResult();
            var seat4 = run.Players[3];
            var discarded = seat4.PlayerCombatState!.DrawPile.Cards.Where(c => c is AscendersBane
                || c is StrikeIronclad && c.CurrentUpgradeLevel == 0
                || c is PommelStrike && c.CurrentUpgradeLevel == 0).GroupBy(c => c.Id.Entry).Select(g => g.First()).ToArray();
            Check(discarded.Length == 3, "saved deck has the three archived GamblingChip identities");
            Console.WriteLine("NATIVE pre-draw piles=" + string.Join(";", run.Players.Select(p => p.NetId + ":" + string.Join(',', p.PlayerCombatState!.DrawPile.Cards.Select(Card)))));
            var selector = new TestCardSelector(); selector.PrepareToSelect(discarded);
            using (CardSelectCmd.PushSelector(selector)) manager.Call("StartTurn", manager.Turn, null);
            var enemy = combat.Enemies.Single();
            var poison = run.Players[1].Potions.Single(p => p.Id.Entry == "POISON_POTION");
            poison.OnUseWrapper(new ThrowingPlayerChoiceContext(), enemy).GetAwaiter().GetResult();
            var swift = seat4.Potions.Single(p => p.Id.Entry == "SWIFT_POTION");
            swift.OnUseWrapper(new ThrowingPlayerChoiceContext(), seat4.Creature).GetAwaiter().GetResult();
            var root = KernelSession.Capture(combat);
            var ids = run.Players.Select(p => p.NetId).ToArray();
            var facts = root.PlanningState(ids);
            var record = new {
                RootLabel = "native-saved-entry-new-same-root-not-historical-hidden-Facts-equivalence",
                LoadedGameAssembly = typeof(Creature).Assembly.Location,
                LoadedGameSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(Creature).Assembly.Location))),
                Hp = run.Players.Select(p => p.Creature.CurrentHp).ToArray(),
                Hands = run.Players.Select(p => new { p.NetId, Energy = p.PlayerCombatState!.Energy, Cards = p.PlayerCombatState.Hand.Cards.Select(Card).ToArray(), Powers = p.Creature.Powers.Select(v => v.Id.Entry + ":" + v.Amount).ToArray(), Potions = p.Potions.Select(v => v.Id.Entry).ToArray() }).ToArray(),
                Enemy = new { Hp = enemy.CurrentHp, Move = enemy.Monster!.NextMove.Id, Powers = enemy.Powers.Select(v => v.Id.Entry + ":" + v.Amount).ToArray() },
                facts.Reusable, FullSemantics = facts.Semantics, SimulationDeltaHistory = root.Simulator.History.Entries.Count,
                NativeHistoryEntries = CombatManager.Instance.History.Entries.Count(),
                Rng = JsonSerializer.Serialize(run.Rng.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableRunRngSet>())
            };
            File.WriteAllText("native-entry.json", JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            Check(record.Hp.SequenceEqual(new[] { 50, 39, 32, 38 }), "new native entry matches four archived HP");
            Check(enemy.CurrentHp == 792 && enemy.Monster.NextMove.Id == "BEES_MOVE", "new native entry matches archived 792HP BEES");
            Check(root.Fork().PlanningState(ids).Matches(facts), "captured native root preserves full fork facts");
            Console.WriteLine("NATIVE F28 ENTRY hp=" + string.Join(',', record.Hp) + " move=" + record.Enemy.Move + " enemyHP=" + enemy.CurrentHp + " reusable=" + facts.Reusable);

            foreach(var player in run.Players) AutoPilot.Set(player.NetId,true);
            Check(run.Players.All(p=>AutoPilot.Drives(p.NetId)),"F28 formal search delegates all four actual seats including NetId1");
            MeasureF28(root,run.Players);
            Check(root.PlanningState(ids).Matches(facts),"formal F28 search leaves its qualified physical root untouched");
        }
        finally
        {
            AutoPilot.Clear(); foreach(var id in handed) AutoPilot.Set(id,true);
            presentation.UnpatchAll(presentation.Id);
        }
    }

    private static void MeasureF28(KernelSession root,IReadOnlyList<Player> party)
    {
        var request=new KernelDecisionRequest(152801,9600,6);
        var options=new TournamentOptions(TopK:20,IncludePotions:false,IncludeEndTurns:true,
            MaxDegreeOfParallelism:6,Objective:KernelPotionPolicy.Objective,Rollout:new(RolloutPolicyKind.Kill,Seed:1),
            ContinuationActors:party,EnableJointSearch:true){Request=request};
        using var cards=new TournamentRun(root.Fork(),party,party,options,4800);
        while(!cards.Advance(TimeSpan.FromMilliseconds(6)))Thread.Sleep(1);
        var first=cards.Result!;
        var replay=new KernelPotionContinuationRun(root,party,party,first,1200,request:request);
        while(!replay.Advance(TimeSpan.FromMilliseconds(6)))Thread.Sleep(1);
        using var potions=new TournamentRun(root.Fork(),party,party,
            options with{IncludePotions=true,PotionsOnly=true,ReferenceResult=replay.Result},request.RemainingMs);
        var openings=(IReadOnlyList<KernelTeamSearch.Action>)AccessTools.Field(typeof(TournamentRun),"candidates").GetValue(potions)!;
        while(!potions.Advance(TimeSpan.FromMilliseconds(6)))Thread.Sleep(1);
        var audit=new KernelPotionPlanAudit(root,party,potions.Result!,request);
        while(!audit.Advance(TimeSpan.FromMilliseconds(6)))Thread.Sleep(1);
        var candidate=audit.Result!;
        var immediate=KernelPotionPolicy.ImmediateBenefit(root,party,candidate.FirstAction,candidate.Script);
        var improves=KernelPotionPolicy.Improves(first.Record,candidate.Record,immediate);
        var chosen=improves?candidate:first;
        object Summary(TournamentResult r)=>new{r.Record,r.StopReason,r.JointDiagnostics,r.Rollouts,r.Cutoffs,
            First=r.FirstAction?.Card?.Id.Entry,Potion=r.FirstAction?.Potion?.Id.Entry,
            Target=r.FirstAction?.Target?.Player?.NetId,Choices=r.FirstAction?.Choices,r.FirstAction?.ChoiceOrdinals,
            ScriptCount=r.Script?.Count};
        File.WriteAllText("formal-search.json",JsonSerializer.Serialize(new{
            Scope="new reconstructed native F28 root; not historical hidden-RNG identity; no paid Touch prefix",
            request.BudgetMs,request.ElapsedMs,request.RemainingMs,request.MaxConcurrency,request.PeakActiveJobs,
            CardBudgetMs=4800,AdvanceMs=6,SleepMs=1,
            CommonPaidBottles=2,RootInventory=party.Sum(p=>root.HeldPotionCount(p)),
            ModeledRootInventory=party.Sum(p=>root.UsablePotions(p).Count(KernelSession.CanModelPotion)),
            Openings=openings.Select(a=>new{Owner=a.Player.NetId,Potion=a.Potion?.Id.Entry,
                Target=a.Target?.Player?.NetId,a.Choices,a.ChoiceOrdinals}).ToArray(),
            Cards=Summary(first),Potions=Summary(candidate),Chosen=Summary(chosen),immediate,improves,
            AuditRemoved=audit.Removed,NativeTerminal=false,SimulationVictory=chosen.Record?.Victory,
            TotalBottles=2+(chosen.Record?.ResourcesConsumed??0)
        },new JsonSerializerOptions{WriteIndented=true}));
        Check(openings.Any(a=>a.Potion is TouchOfInsanity && a.Target==party[0].Creature
            && a.Choices?.Single().Cards.Single().Id=="DASH"),"formal F28 TopK must contain self Dash Touch without a manually paid prefix");
        Check(candidate.FirstAction?.Potion is TouchOfInsanity && candidate.FirstAction.Target==party[0].Creature
            && candidate.FirstAction.Choices?.Single().Cards.Single().Id=="DASH",
            "formal fixed-budget F28 potion comparison must itself select self Dash Touch");
        var exact=root.Fork();
        Check(exact.ReplayPlanned(candidate.FirstAction!,80,out var why),"formal selected Touch must exact replay: "+why);
        Check(exact.CanFork,"formal selected Touch must settle to a reusable continuation root");
        Check(improves,"formal selected Touch line must pass the unchanged potion improvement policy");
        Console.WriteLine("F28 formal cards "+first.JointDiagnostics);
        Console.WriteLine("F28 formal potion "+candidate.JointDiagnostics);
        Console.WriteLine("PASS: formal fixed-budget F28 search discovers executable self Dash Touch; terminal remains simulated.");
    }
    private sealed class SavedManager : IDisposable
    {
        private readonly RunManager run = RunManager.Instance;
        private readonly Dictionary<FieldInfo, object?> old;
        internal object Turn => AccessTools.Field(typeof(CombatManager), "_turnState").GetValue(CombatManager.Instance)!;
        internal SavedManager(RunState state, SerializableRun save)
        {
            old = typeof(RunManager).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).ToDictionary(f => f, f => f.GetValue(run));
            var host = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
            AccessTools.Property(typeof(RunManager), "State").SetValue(run, state);
            AccessTools.Method(typeof(RunManager), "InitializeShared").Invoke(run, new object?[] { host, new PeerInputSynchronizer(host), false, save.DailyTime, save.StartTime, save.RunTime, save.WinTime, save.NumReloads });
            AccessTools.Method(typeof(RunManager), "InitializeRunLobby").Invoke(run, new object[] { host, state, state.Players.Select(p => new RunLobbyPlayer { id = p.NetId, isModded = host.LocalVersion.IsModded() }) });
            AccessTools.Method(typeof(RunManager), "InitializeSavedRun").Invoke(run, new object[] { save });
            run.CombatStateSynchronizer.IsDisabled = true;
            Check(!run.IsSingleplayerOrFakeMultiplayer, "native saved manager retains true multiplayer semantics");
        }
        internal void Call(string name, params object?[] args)
        {
            var method = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(m => m.Name == name && m.GetParameters().Length == args.Length);
            if (method.Invoke(CombatManager.Instance, args) is Task task) task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            run.ActionExecutor.Pause();
            foreach (var disposable in old.Where(p => !ReferenceEquals(p.Value, p.Key.GetValue(run))).Select(p => p.Key.GetValue(run)).OfType<IDisposable>().Distinct()) disposable.Dispose();
            foreach (var row in old.Where(p => !ReferenceEquals(p.Value, p.Key.GetValue(run)))) row.Key.SetValue(run, row.Value);
        }
    }

    private static IDisposable NativeScope(CombatState combat) => (IDisposable)Activator.CreateInstance(
        AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [combat], null)!;

    private static string Card(CardModel c)=>c.Id.Entry+(c.CurrentUpgradeLevel>0?"+":"");
}

internal static class TouchRuntimeFixtureSnippet
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string detail)
    { if (!value) throw new InvalidOperationException("Touch runtime: " + detail); }

    // Call these from the authorized Touch case registry; the enclosing runner
    // records actual command/exit/raw output and later actual production-fault RED.
    internal static void NativePaidRuntime()
        => NativePaidRuntimeCore(duplicatePotion: false);
    internal static void NativeSecondSlotPaidRuntime()
        => NativePaidRuntimeCore(duplicatePotion: true);
    private static void NativePaidRuntimeCore(bool duplicatePotion)
    {
        TestEnvironment.Ensure(); var b = Board(duplicatePotion: duplicatePotion); using var scope = new Scope(b);
        var root = KernelSession.Capture(b.Combat); var planned = Selected(root, b);
        var predicted = root.ForkForReplay();
        Check(predicted.ReplayPlanned(planned, 1, out var reason, out _), "recorded Touch must settle: " + reason);
        var plan = scope.EmitPotion(planned);
        Check(BotChoicePlanSync.Prepare(plan, b.Combat), "no-peer preparation must be ready immediately");
        Check(((BotChoicePlanMessage)GetStatic("installed")!).PotionIndex == (duplicatePotion ? 1u : 0u),
            "planned-menu synchronization must bind the original selected physical potion slot");
        var action = scope.Enqueue(plan.Potion, plan.Target!);
        // If the production prefix is absent/broken, native selector picks Bash.
        // It supplies presentation only, never replaces the planned-menu matcher.
        var selector = new Selector(b.Bash);
        using (CardSelectCmd.PushSelector(selector)) action.Execute().GetAwaiter().GetResult();
        Check(action.CompletionTask.IsCompleted && action.Exception is null && scope.Index == 1
            && !scope.Diverged && scope.CacheIntact && selector.Calls == 0,
            "real UsePotionAction must consume the transported recipient choice through the production prefix");
        Check(!b.Owner.Potions.Contains(b.Potion) && b.Owner.PlayerCombatState!.Hand.Cards.Count == 0
            && predicted.PlanningState([b.Owner.NetId, b.Recipient.NetId]).Matches(
                KernelSession.Capture(b.Combat).PlanningState([b.Owner.NetId, b.Recipient.NetId])),
            "real potion consumption and selected recipient cost must match full projected future state");
        if (duplicatePotion)
            Check(action.PotionIndex == 1 && ReferenceEquals(b.Owner.GetPotionAtSlotIndex(0), b.RetainedPotion)
                && b.Owner.GetPotionAtSlotIndex(1) is null && b.Owner.Potions.Count() == 1 && !b.RetainedPotion!.HasBeenRemovedFromState,
                "actual slot-one Touch must leave the same-ID slot-zero bottle physically held and untouched");
        Check(predicted.Play(b.Strike, b.Enemy, out reason), "the selected projected card must play: " + reason);
        var paid = b.Strike.SpendResources().GetAwaiter().GetResult();
        Check(paid.Item1 == 0 && paid.Item2 == 0, "the chosen native card must actually pay zero energy and stars");
        b.Strike.OnPlayWrapper(new ThrowingPlayerChoiceContext(), b.Enemy, false,
            new ResourceInfo { EnergySpent = paid.Item1, EnergyValue = paid.Item1,
                StarsSpent = paid.Item2, StarValue = paid.Item2 }, skipCardPileVisuals: true).GetAwaiter().GetResult();
        Check(predicted.PlanningState([b.Owner.NetId, b.Recipient.NetId]).Matches(
            KernelSession.Capture(b.Combat).PlanningState([b.Owner.NetId, b.Recipient.NetId])),
            "paid native play after Touch must retain exact full future-state parity");
    }

    // R2 actual 2026-10-12: remove only ordinary held-original priority.
    // Mutant build 0, native RED 1, exact 2-source/10-DLL+PDB restore, rebuild 0, native GREEN 0.
    // The fault really consumed slot zero and applied native Energy before comparison:
    // "DIAG: ordinary four-seat plannedSlot=1 emittedSlot=0 nativeSlot=0 energy=3->5 fullFacts=False slot0Retained=False slot1Empty=False"
    // "System.InvalidOperationException: Touch runtime: ordinary second-slot native consume must preserve the exact physical belt and full future Facts"
    // Raw receipts: work/v15-ordinary-bottle-private-20261012-v1/r2/result.json.
    internal static void NativeOrdinarySecondSlotPaidRuntime()
    {
        TestEnvironment.Ensure(); var b = Board(duplicatePotion: true, ordinaryEnergy: true);
        using var scope = new Scope(b);
        var actors = b.Combat.Players.ToArray(); var ids = actors.Select(p => p.NetId).ToArray();
        Check(actors.Length == 4 && b.Owner.GetPotionSlotIndex(b.Potion) == 1,
            "ordinary native control must start with four seats and the original second bottle");
        var root = KernelSession.Capture(b.Combat); var predicted = root.ForkForReplay();
        Check(predicted.UsePotion(b.Potion, b.Owner.Creature, out var reason),
            "ordinary second-slot projection must settle: " + reason);
        var planned = new KernelTeamSearch.Action(b.Owner, null, b.Owner.Creature, b.Potion);
        // Do not stop at an emitter-only assertion: the negative control must
        // actually consume its emitted native slot before full physical comparison.
        var plan = scope.EmitPotion(planned, verifyPhysicalBottle: false);
        var emittedSlot = b.Owner.GetPotionSlotIndex(plan.Potion);
        var energyBefore = b.Owner.PlayerCombatState!.Energy;
        Check(BotChoicePlanSync.Prepare(plan, b.Combat), "ordinary no-menu preparation must be immediately ready");
        var action = scope.Enqueue(plan.Potion, plan.Target!);
        action.Execute().GetAwaiter().GetResult();
        var futureMatches = predicted.PlanningState(ids).Matches(KernelSession.Capture(b.Combat).PlanningState(ids));
        var slotZeroRetained = ReferenceEquals(b.Owner.GetPotionAtSlotIndex(0), b.RetainedPotion);
        var slotOneEmpty = b.Owner.GetPotionAtSlotIndex(1) is null;
        var receipt = new { FourSeats = actors.Length, PlannedSlot = 1, EmittedSlot = emittedSlot,
            NativeSlot = action.PotionIndex, EnergyBefore = energyBefore, EnergyAfter = b.Owner.PlayerCombatState.Energy,
            NativeCompleted = action.CompletionTask.IsCompleted, NativeException = action.Exception?.ToString(),
            SlotZeroRetained = slotZeroRetained, SlotOneEmpty = slotOneEmpty, FullFutureFactsMatch = futureMatches };
        File.WriteAllText("ordinary-second-slot-native.json", JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DIAG: ordinary four-seat plannedSlot=1 emittedSlot={emittedSlot} nativeSlot={action.PotionIndex} energy={energyBefore}->{b.Owner.PlayerCombatState.Energy} fullFacts={futureMatches} slot0Retained={slotZeroRetained} slot1Empty={slotOneEmpty}");
        Check(action.CompletionTask.IsCompleted && action.Exception is null && energyBefore == 3
            && b.Owner.PlayerCombatState.Energy == 5 && ReferenceEquals(plan.Potion, b.Potion)
            && emittedSlot == 1 && action.PotionIndex == 1 && slotZeroRetained && slotOneEmpty
            && b.Owner.Potions.Count() == 1 && !b.RetainedPotion!.HasBeenRemovedFromState && futureMatches,
            "ordinary second-slot native consume must preserve the exact physical belt and full future Facts");
    }

    internal static void NativeMenuGuards()
    {
        TestEnvironment.Ensure(); var b = Board(); using var scope = new Scope(b);
        var root = KernelSession.Capture(b.Combat); var planned = Selected(root, b);
        var menu = b.Recipient.PlayerCombatState!.Hand.Cards.ToArray();
        var action = new UsePotionAction(b.Potion, b.Recipient.Creature, true);
        scope.Install(planned, action);
        Check(BotChoicePlanSync.SelectMenu(b.Recipient, menu, 1, 1, b.Potion.Id.Entry)?.Single() == b.Strike
            && scope.Index == 1 && !scope.Diverged && scope.CacheIntact,
            "native potion action owner and recipient chooser must remain distinct and correctly bound");
        scope.Install(planned, action);
        Check(BotChoicePlanSync.SelectMenu(b.Owner, menu, 1, 1, b.Potion.Id.Entry) is null
            && scope.Diverged && !scope.CacheIntact, "wrong chooser must revoke dependent continuation");
        scope.Install(planned, action);
        Check(BotChoicePlanSync.SelectMenu(b.Recipient, [b.Strike], 1, 1, b.Potion.Id.Entry) is null
            && scope.Diverged && !scope.CacheIntact,
            "changed unselected full-menu option must revoke the suffix while the selected card remains");
        scope.Install(planned, action);
        Check(BotChoicePlanSync.SelectMenu(b.Recipient, menu, 1, 1, "LIQUID_MEMORIES") is null
            && scope.Diverged && !scope.CacheIntact, "wrong native menu source must revoke dependent continuation");
        scope.Install(planned, action);
        ((BotChoicePlanMessage)GetStatic("installed")!).Card = "LIQUID_MEMORIES";
        Check(BotChoicePlanSync.SelectMenu(b.Recipient, menu, 1, 1, b.Potion.Id.Entry) is null
            && scope.Diverged && !scope.CacheIntact,
            "captured potion model ID must agree with the actual source even when payload MenuSource is unchanged");
        scope.Install(planned with { Choices = [planned.Choices!.Single() with { Source = "LIQUID_MEMORIES" }] }, action);
        Check(BotChoicePlanSync.SelectMenu(b.Recipient, menu, 1, 1, b.Potion.Id.Entry) is null
            && scope.Diverged && !scope.CacheIntact,
            "recorded choice Source must agree with the captured potion ID as well as MenuSource");
        scope.Install(planned, action);
        var wrong = new UsePotionAction(b.Potion, b.Owner.Creature, true);
        Set(wrong, nameof(GameAction.Id), (uint?)41); scope.Running(wrong);
        Check(BotChoicePlanSync.SelectMenu(b.Recipient, menu, 1, 1, b.Potion.Id.Entry) is null
            && scope.Diverged && !scope.CacheIntact, "wrong target for the matching potion transaction must invalidate");
    }

    internal static void ActualPrepareAckResume()
    {
        TestEnvironment.Ensure(); var b = Board(); using var scope = new Scope(b, peer: 97);
        var planned = Selected(KernelSession.Capture(b.Combat), b);
        var plan = new KernelCombatPlanner.PotionPlan(b.Potion, b.Recipient.Creature, "runtime-ack", planned.Choices);
        var next = scope.Queues.NextActionId; var before = KernelSession.Capture(b.Combat).PlanningState([b.Owner.NetId, b.Recipient.NetId]);
        Check(b.Owner.PlayerCombatState!.Hand.Cards.Count == 0 && !BotChoicePlanSync.Prepare(plan, b.Combat),
            "empty-hand owner with a ready native peer must wait for acknowledgement before enqueue");
        Check(BotChoicePlanSync.Resume(b.Combat, out var card, out var potion) && card is null && potion is null
            && scope.Queues.IsEmpty && scope.Queues.NextActionId == next && b.Owner.Potions.Contains(b.Potion),
            "pending ACK must preserve the exact potion step without an action or queue-ID advance");
        var installed = (BotChoicePlanMessage)GetStatic("installed")!;
        Receive(new() { Ack = true, Sequence = installed.Sequence + 1, Location = installed.Location }, 97);
        Check(BotChoicePlanSync.Resume(b.Combat, out card, out potion) && card is null && potion is null,
            "a stale-sequence ACK must not release the current potion transaction");
        Receive(new() { Ack = true, Sequence = installed.Sequence, Location = installed.Location }, 97);
        Check(BotChoicePlanSync.Resume(b.Combat, out card, out potion) && card is null && ReferenceEquals(potion, plan)
            && before.Matches(KernelSession.Capture(b.Combat).PlanningState([b.Owner.NetId, b.Recipient.NetId])),
            "the actual current ACK must return the same potion rather than repolling or mutating the board");
        Check(BotChoicePlanSync.Prepare(potion!, b.Combat), "ACK-ready Prepare must release the exact stored step");
        var action = scope.Enqueue(b.Potion, b.Recipient.Creature);
        Check(action.Id == next && action.OwnerId == b.Owner.NetId && action.TargetId == b.Recipient.Creature.CombatId,
            "the first real queued action after ACK must retain owner, action ID and recipient");
        using (CardSelectCmd.PushSelector(new Selector(b.Bash))) action.Execute().GetAwaiter().GetResult();
        Check(action.Exception is null && scope.Index == 1 && !scope.Diverged && !b.Owner.Potions.Contains(b.Potion),
            "the acknowledged real potion action must subsequently execute its exact recorded menu");
    }

    internal static void PotionWireRoundtrip()
    {
        TestEnvironment.Ensure(); var b = Board(); using var scope = new Scope(b);
        var planned = Selected(KernelSession.Capture(b.Combat), b);
        foreach (var target in new uint?[] { b.Recipient.Creature.CombatId, null })
        {
            var message = new BotChoicePlanMessage { Sequence = 8, Action = 42, Bot = b.Owner.NetId,
                Card = b.Potion.Id.Entry, Potion = true, PotionIndex = 2, Target = target,
                Payload = JsonSerializer.Serialize(planned.Choices), Ack = true, Clear = false };
            var writer = new PacketWriter(); message.Serialize(writer);
            var reader = new PacketReader(); reader.Reset(writer.Buffer);
            var decoded = new BotChoicePlanMessage(); decoded.Deserialize(reader);
            var choices = JsonSerializer.Deserialize<KernelChoice[]>(decoded.Payload)!;
            Check(decoded.Sequence == message.Sequence && decoded.Action == message.Action && decoded.Bot == b.Owner.NetId
                && decoded.Card == message.Card && decoded.Potion && decoded.PotionIndex == 2 && decoded.Target == target
                && decoded.Ack && !decoded.Clear && decoded.Location.Equals(message.Location) && decoded.Payload == message.Payload
                && choices.Single().Owner == b.Recipient.NetId && choices.Single().Options is { Count: 2 }
                && choices.Single().MenuSource == b.Potion.Id.Entry,
                "native packet roundtrip must retain potion binding and the recipient's complete choice contract");
        }
    }

    internal static void NativeHumanRecipientControl()
    {
        TestEnvironment.Ensure(); var b = Board(humanRecipient: true); using var scope = new Scope(b);
        var root = KernelSession.Capture(b.Combat);
        Check(root.PotionActions(b.Potion, b.Recipient.Creature, controlledActors: [b.Owner]).Count == 0,
            "an uncontrolled human recipient must not become a planned Touch action");
        // An injected/stale menu payload must not steal a human's selection.
        // This invokes the native action directly; the full BotRuntime pre-enqueue
        // human guard remains a separate real Tick / Steam acceptance obligation.
        var planned = Selected(root, b); var action = new UsePotionAction(b.Potion, b.Recipient.Creature, true);
        scope.Install(planned, action); Set(action, nameof(GameAction.Id), null);
        scope.Queues.EnqueueWithoutSynchronizing(action);
        ((BotChoicePlanMessage)GetStatic("installed")!).Action = action.Id!.Value;
        var selector = new Selector(b.Bash);
        using (CardSelectCmd.PushSelector(selector)) action.Execute().GetAwaiter().GetResult();
        Check(!AutoPilot.Drives(b.Recipient.NetId) && selector.Calls == 1 && scope.Index == 0 && action.Exception is null
            && b.Bash.SpendResources().GetAwaiter().GetResult().Item1 == 0
            && b.Strike.SpendResources().GetAwaiter().GetResult().Item1 == 1,
            "production bot prefix must leave the native human menu and chosen physical card under human control");
    }

    private sealed record BoardState(CombatState Combat, Player Owner, Player Recipient, Creature Enemy,
        PotionModel Potion, CardModel Strike, CardModel Bash, PotionModel? RetainedPotion = null);
    private static BoardState Board(bool humanRecipient = false, bool duplicatePotion = false, bool ordinaryEnergy = false)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var recipient = Player.CreateForNewRun<Deprived>(UnlockState.all,
            humanRecipient ? 76561198999999157UL : BotRegistry.CreateId(BotDifficulty.Pro, 251, 0));
        Player[] party = ordinaryEnergy
            ? [owner, recipient, Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 252, 0)),
                Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 253, 0))]
            : [owner, recipient];
        var c = new CombatState(runState: RunState.CreateForTest(party, seed: "TOUCH-RUNTIME", ascensionLevel: 10)) { RoundNumber = 1 };
        foreach (var p in party)
        { p.ResetCombatState(); c.AddPlayer(p); p.MaxEnergy = 3; p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(3); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80); }
        var strike = c.CreateCard<StrikeIronclad>(recipient); var bash = c.CreateCard<Bash>(recipient);
        recipient.PlayerCombatState!.Hand.AddInternal(strike); recipient.PlayerCombatState.Hand.AddInternal(bash);
        PotionModel potion = ordinaryEnergy ? ModelDb.Potion<EnergyPotion>().ToMutable() : ModelDb.Potion<TouchOfInsanity>().ToMutable();
        owner.AddPotionInternal(potion, silent: true);
        PotionModel? retained = null;
        if (duplicatePotion)
        { retained = potion; potion = ordinaryEnergy ? ModelDb.Potion<EnergyPotion>().ToMutable() : ModelDb.Potion<TouchOfInsanity>().ToMutable();
            owner.AddPotionInternal(potion, silent: true); }
        var enemy = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(100); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return new(c, owner, recipient, enemy, potion, strike, bash, retained);
    }
    private static KernelTeamSearch.Action Selected(KernelSession root, BoardState b)
        => root.PotionActions(b.Potion, b.Recipient.Creature, controlledActors: [b.Owner, b.Recipient])
            .Single(a => a.Choices!.Single().Cards.Single().StateKey == root.CardStateKey(b.Strike));
    private static void Receive(BotChoicePlanMessage message, ulong sender)
        => AccessTools.Method(typeof(BotChoicePlanSync), "Receive").Invoke(null, [message, sender]);
    private static object? GetStatic(string name) => AccessTools.Field(typeof(BotChoicePlanSync), name).GetValue(null);
    private static void Set(object obj, string name, object? value)
    {
        for (var type = obj.GetType(); type is not null; type = type.BaseType)
        { var flags = Instance | BindingFlags.DeclaredOnly;
            if (type.GetField(name, flags) is { } f) { f.SetValue(obj, value); return; }
            if (type.GetProperty(name, flags) is { SetMethod: not null } p) { p.SetValue(obj, value); return; }
            if (type.GetField("<" + name + ">k__BackingField", flags) is { } b) { b.SetValue(obj, value); return; } }
        throw new MissingMemberException(obj.GetType().Name, name);
    }
    private sealed class Selector(CardModel selected) : ICardSelector
    {
        internal int Calls;
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> cards, int minSelect, int maxSelect)
        { Calls++; return Task.FromResult<IEnumerable<CardModel>>([selected]); }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> cards,
            IReadOnlyList<CardRewardAlternative> alternatives) => throw new NotSupportedException();
    }
    private sealed class Scope : IDisposable
    {
        private readonly Harmony harmony = new("coopbots.touch-runtime-snippet");
        private readonly IDisposable native;
        private readonly Dictionary<PropertyInfo, object?> managerSaved;
        private readonly Dictionary<FieldInfo, object?> syncSaved, plannerSaved;
        private readonly HashSet<ulong> waiting = (HashSet<ulong>)GetStatic("waiting")!;
        private readonly ulong[] waitingSaved;
        private readonly KernelCombatPlanner planner = (KernelCombatPlanner)AccessTools.Field(typeof(BotRuntime), "KernelPlanner").GetValue(null)!;
        private readonly object? identity; private readonly BoardState board; private readonly bool ownerDelegated, recipientDelegated;
        private readonly ActionExecutor executor = (ActionExecutor)RuntimeHelpers.GetUninitializedObject(typeof(ActionExecutor));
        private KernelTeamSearch.Action[] script = [];
        internal ActionQueueSet Queues { get; }
        internal int Index => (int)GetStatic("index")!;
        internal bool Diverged => (bool)GetStatic("choiceDiverged")!;
        internal bool CacheIntact => ReferenceEquals(AccessTools.Field(typeof(KernelCombatPlanner), "tournamentScript").GetValue(planner), script);
        internal Scope(BoardState b, ulong? peer = null)
        {
            board = b; identity = AccessTools.Field(typeof(BotRuntime), "_combatIdentity").GetValue(null);
            syncSaved = typeof(BotChoicePlanSync).GetFields(Static).Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(null));
            waitingSaved = waiting.ToArray(); plannerSaved = typeof(KernelCombatPlanner).GetFields(Instance)
                .Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(planner));
            ownerDelegated = AutoPilot.IsAutopiloted(b.Owner.NetId); recipientDelegated = AutoPilot.IsAutopiloted(b.Recipient.NetId);
            AutoPilot.Set(b.Owner.NetId, true); AutoPilot.Set(b.Recipient.NetId, false);
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"), Instance, null, [b.Combat], null)!;
            var manager = RunManager.Instance; string[] properties = ["NetService", "RunLocationTargetedBuffer", "ActionQueueSet", "ActionExecutor"];
            managerSaved = properties.Select(n => AccessTools.Property(typeof(RunManager), n)).ToDictionary(p => p, p => p.GetValue(manager));
            INetGameService net;
            if (peer.HasValue)
            { var host = new NetHostGameService(default); host.ConnectedPeers.Add(new NetClientData { peerId = peer.Value, readyForBroadcasting = true }); net = host;
                var send = typeof(NetHostGameService).GetMethods().Single(m => m.Name == "SendMessage" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                harmony.Patch(send.MakeGenericMethod(typeof(BotChoicePlanMessage)), prefix: new HarmonyMethod(typeof(Scope), nameof(NoTransport))); }
            else net = (NetSingleplayerGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetSingleplayerGameService));
            Queues = new ActionQueueSet(b.Combat.Players); Queues.SetUpForCombat(); Queues.CombatStarted();
            Set(manager, "NetService", net); Set(manager, "RunLocationTargetedBuffer", new RunLocationTargetedMessageBuffer(net));
            Set(manager, "ActionQueueSet", Queues); Set(manager, "ActionExecutor", executor); BotChoicePlanSync.Initialize(manager);
            AccessTools.Field(typeof(BotRuntime), "_combatIdentity").SetValue(null, b.Combat);
            harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"), prefix: new HarmonyMethod(typeof(Scope), nameof(Format)));
            var prefix = AccessTools.Method(typeof(BotMultipleCardSelectPatch), "Prefix");
            foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(Task<IEnumerable<CardModel>>)))
                if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.PatchMethod == prefix) != true) harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            Seed();
        }
        internal UsePotionAction Enqueue(PotionModel potion, Creature target)
        { var action = new UsePotionAction(potion, target, true); Queues.EnqueueWithoutSynchronizing(action); Running(action); return action; }
        internal KernelCombatPlanner.PotionPlan EmitPotion(KernelTeamSearch.Action planned, bool verifyPhysicalBottle = true)
        {
            Seed(); script = [planned];
            Set(planner, "tournamentScript", script); Set(planner, "tournamentScriptIndex", 0);
            Set(planner, "tournamentScriptRound", board.Combat.RoundNumber);
            Set(planner, "tournamentScriptAliveStates", new ulong[][] { board.Combat.Players.Select(p => p.NetId).ToArray() });
            Set(planner, "tournamentScriptEnemyStates", new Creature[][] { [board.Enemy] });
            object?[] arguments = [board.Combat, board.Combat.Players.ToArray(), true, null];
            Check((bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryEmitFromTournamentScript")
                .Invoke(planner, arguments)! && arguments[3] is null && planner.ConfirmedPotion is { } plan
                && (!verifyPhysicalBottle || ReferenceEquals(plan.Potion, planned.Potion)) && ReferenceEquals(plan.Choices, planned.Choices),
                "the actual Core emitter must retain the original selected physical bottle and its exact choices");
            return planner.ConfirmedPotion!;
        }
        internal void Running(GameAction action) => Set(executor, nameof(ActionExecutor.CurrentlyRunningAction), action);
        internal void Install(KernelTeamSearch.Action planned, UsePotionAction action)
        {
            Seed(); Set(action, nameof(GameAction.Id), (uint?)41); Running(action);
            AccessTools.Field(typeof(BotChoicePlanSync), "installed").SetValue(null, new BotChoicePlanMessage {
                Action = 41, Bot = board.Owner.NetId, Card = board.Potion.Id.Entry, Potion = true,
                PotionIndex = action.PotionIndex, Target = action.TargetId, Payload = JsonSerializer.Serialize(planned.Choices) });
            AccessTools.Field(typeof(BotChoicePlanSync), "choices").SetValue(null, planned.Choices!.ToArray());
            AccessTools.Field(typeof(BotChoicePlanSync), "index").SetValue(null, 0);
            AccessTools.Field(typeof(BotChoicePlanSync), "choiceDiverged").SetValue(null, false);
        }
        private void Seed()
        { planner.Reset(); script = [new(board.Recipient, board.Strike, board.Enemy), new(board.Recipient, board.Bash, board.Enemy)];
            Set(planner, "tournamentScript", script); Set(planner, "tournamentScriptCombat", board.Combat); Set(planner, "rootCombat", board.Combat); }
        private static bool NoTransport() => false; // Transport only; Prepare, ACK handler and Resume stay production code.
        private static bool Format(ref string __result) { __result = ""; return false; }
        public void Dispose()
        {
            planner.Reset(); foreach (var item in plannerSaved) item.Key.SetValue(planner, item.Value);
            foreach (var item in syncSaved) item.Key.SetValue(null, item.Value); waiting.Clear(); waiting.UnionWith(waitingSaved);
            foreach (var item in managerSaved) item.Key.SetValue(RunManager.Instance, item.Value);
            AccessTools.Field(typeof(BotRuntime), "_combatIdentity").SetValue(null, identity);
            AutoPilot.Set(board.Owner.NetId, ownerDelegated); AutoPilot.Set(board.Recipient.NetId, recipientDelegated);
            harmony.UnpatchAll(harmony.Id); native.Dispose();
        }
    }
}
