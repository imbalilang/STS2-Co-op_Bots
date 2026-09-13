using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class MultiHumanScenarios
{
    internal static void Run()
    {
        var host = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var guest = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Genius, 1, 2));
        var bot2 = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Genius, 2, 3));
        var party = new[] { host, guest, bot, bot2 };
        var run = RunState.CreateForTest(party, seed: "TWO-HUMAN-COOP");
        var combat = new CombatState(runState: run);
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetCurrentHpInternal(50);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetCurrentHpInternal(100);
        enemy.Monster.SetMoveImmediate(new MoveState("DANGER", _ => Task.CompletedTask, new SingleAttackIntent(10)), true);
        var policy = typeof(BotBrain).Assembly.GetType("CoopBots.MultiHumanCooperation")!;
        var readyMethod = policy.GetMethod("HumansReady", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Ready(params ulong[] ended) => (bool)readyMethod.Invoke(null,
            new object[] { party, (Func<Player, bool>)(p => ended.Contains(p.NetId)) })!;
        Check(!Ready(), "Unfinished humans must keep the normal speed.");
        Check(!Ready(1), "Host end-turn alone must not trigger fast mode.");
        Check(Ready(1, 2), "All humans ending must trigger fast mode regardless of unfinished bots.");
        Check(!Ready(2), "Guest end-turn alone must not trigger fast mode.");
        guest.Creature.SetCurrentHpInternal(0);
        Check(Ready(1), "A dead human must not block accelerated cleanup.");
        guest.Creature.SetCurrentHpInternal(50);
        var local = policy.GetMethod("LocalHuman", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(ReferenceEquals(local.Invoke(null, new object[] { party, 2UL }), guest), "Guest advice must inspect the guest's hand.");
        var vote = policy.GetMethod("Vote", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(uint));
        uint? Vote(uint?[] votes, int count, int index) => (uint?)vote.Invoke(null, new object[] { votes, count, index });
        Check(Vote(new uint?[] { 0, 1 }, 2, 0) == 0 && Vote(new uint?[] { 0, 1 }, 2, 1) == 1,
            "Split human votes with two bots must stay 2:2, never 3:1.");
        Check(Vote(new uint?[] { 0, 1, 1 }, 1, 0) == 1, "Three-human majority must not be reversed by the first player's bot vote.");
        Check(Vote(new uint?[] { 1, null }, 2, 0) is null && Vote(new uint?[] { 1, 1 }, 2, 1) == 1,
            "Bots must wait for all humans and follow unanimous votes.");

        var advisor = typeof(BotBrain).Assembly.GetType("CoopBots.HumanCoopAdvisor")!;
        var opening = advisor.GetMethod("Opening", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bash = combat.CreateCard<Bash>(bot); bot.PlayerCombatState!.Hand.AddInternal(bash);
        guest.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(guest));
        Check(((BotBrain.CombatMove?)opening.Invoke(null, new object[] { bot, party }))?.Card == bash,
            "A guest's attack alone must justify Vulnerable setup even when the host has no attacks.");
        guest.PlayerCombatState.LoseEnergy(3);
        Check(opening.Invoke(null, new object[] { bot, party }) is null, "Guest energy changes must invalidate stale follow-up potential.");
        bot.PlayerCombatState.Hand.RemoveInternal(bash);
        bot.Creature.GainBlockInternal(12);
        var shield = combat.CreateCard<DemonicShield>(bot); bot.PlayerCombatState.Hand.AddInternal(shield);
        guest.Creature.SetCurrentHpInternal(5);
        var rescue = BotBrain.ChooseCombatMove(bot, 0);
        Check(rescue?.Target == guest.Creature, $"Emergency support must rescue the guest, not favor a healthy host. Target={rescue?.Target?.Player?.NetId}, score={rescue?.Score}, reason={rescue?.Reason}");

        var hostNet = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        var clientNet = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        ((ReconnectNetProxy)clientNet).Kind = NetGameType.Client;
        var maps = new MapSelectionSynchronizer(clientNet, null!, run);
        var a = new MapVote { coord = new MapCoord(0, 1), mapGenerationCount = maps.MapGenerationCount };
        var b = new MapVote { coord = new MapCoord(1, 1), mapGenerationCount = maps.MapGenerationCount };
        maps.PlayerVotedForMapCoord(host, run.MapLocation, a);
        maps.PlayerVotedForMapCoord(guest, run.MapLocation, b);
        maps.PlayerVotedForMapCoord(bot, run.MapLocation, a);
        maps.PlayerVotedForMapCoord(bot2, run.MapLocation, b);
        maps.PlayerVotedForMapCoord(guest, run.MapLocation, a);
        Check(maps.GetVote(bot) is null && maps.GetVote(bot2) is null && maps.GetVote(host).Equals(a),
            "A guest changing their vote must invalidate all bot votes while preserving the other human's vote.");
        var eventVotes = new List<uint?> { 0, 1, 0, 1 };
        typeof(BotBrain).Assembly.GetType("CoopBots.HumanEventVotePatch")!
            .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { guest, 1U, 1U, eventVotes });
        Check(eventVotes[0] == 0 && eventVotes[1] == 1 && eventVotes[2] is null && eventVotes[3] is null,
            "Guest event votes must invalidate derived bot votes without changing either human vote.");
        var combatSync = new MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer(clientNet, null, run);
        typeof(BotBrain).Assembly.GetType("CoopBots.CombatSyncBotPatch")!
            .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { combatSync });
        var syncData = (System.Collections.IDictionary)typeof(MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer)
            .GetField("_syncData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combatSync)!;
        Check(syncData.Count == 2 && syncData.Contains(bot.NetId) && syncData.Contains(bot2.NetId)
            && !syncData.Contains(host.NetId) && !syncData.Contains(guest.NetId),
            "Bot sync seeding must never manufacture synchronization data for either human peer.");

        var hostQueue = new ActionQueueSet(party); var clientQueue = new ActionQueueSet(party);
        var hostBuffer = new RunLocationTargetedMessageBuffer(hostNet);
        var clientBuffer = new RunLocationTargetedMessageBuffer(clientNet);
        var hostSync = new ActionQueueSynchronizer(run, hostQueue, hostBuffer, hostNet);
        var clientSync = new ActionQueueSynchronizer(run, clientQueue, clientBuffer, clientNet);
        hostSync.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
        clientSync.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
        var hostActions = new List<GameAction>(); var clientActions = new List<GameAction>();
        hostQueue.ActionEnqueued += hostActions.Add; clientQueue.ActionEnqueued += clientActions.Add;
        var enqueue = typeof(ActionQueueSynchronizer).GetMethod("EnqueueAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        enqueue.Invoke(hostSync, new object[] { new EndPlayerTurnAction(bot, bot.PlayerCombatState.TurnNumber), bot.NetId });
        var message = ((ReconnectNetProxy)hostNet).Sent.Select(s => s.Message).OfType<ActionEnqueuedMessage>().Single();
        var receive = typeof(ActionQueueSynchronizer).GetMethod("HandleActionEnqueuedMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        receive.Invoke(clientSync, new object[] { message, 1UL });
        Check(hostActions.Count == 1 && clientActions.Count == 1 && clientActions[0].OwnerId == bot.NetId
            && hostActions[0].Id == clientActions[0].Id, "Host bot action must replay once on the client with the same identity and action ID.");
        Check(!((ReconnectNetProxy)clientNet).Sent.Any(s => s.Message is ActionEnqueuedMessage or RequestEnqueueActionMessage),
            "Replaying the host action on the client must not rebroadcast it or create another bot request.");
        hostSync.Dispose(); clientSync.Dispose();
        Console.WriteLine("PASS: all-human fast-mode readiness, per-human advice, proportional map/event votes, guest Vulnerable synergy and rescue, guest vote invalidation, host-to-client bot action identity/order without rebroadcast.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
