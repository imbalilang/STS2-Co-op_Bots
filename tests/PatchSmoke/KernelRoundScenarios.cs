using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class KernelRoundScenarios
{
    internal static void Run()
    {
        void Check(bool value, string reason) { if (!value) throw new Exception("P1-1: " + reason); }
        var players = Enumerable.Range(1, 3).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(50 + i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(players, seed: "P1-ROUND"));
        foreach (var p in players)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(200); enemy.SetCurrentHpInternal(200);
        enemy.Monster.SetMoveImmediate(new MoveState("ATTACK", _ => Task.CompletedTask, new SingleAttackIntent(1)) { FollowUpStateId = "ATTACK" }, true);
        T Hand<T>(Player p) where T : CardModel { var c = combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(c); return c; }
        var headbutt = Hand<Headbutt>(players[0]);
        var strike = combat.CreateCard<StrikeIronclad>(players[0]);
        var defend = combat.CreateCard<DefendIronclad>(players[0]);
        players[0].PlayerCombatState!.DiscardPile.AddInternal(strike); players[0].PlayerCombatState!.DiscardPile.AddInternal(defend);
        var root = KernelSession.Capture(combat);
        var unresolved = root.Fork();
        Check(!unresolved.Play(headbutt, enemy, out _), "Direct replay without a choice must report its unresolved transaction.");
        var rejected = false;
        try { unresolved.Fork(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Never fork a partially resolved choice transaction.");
        var choices = root.CardBranches(headbutt, enemy).ToArray();
        Check(choices.Length >= 2 && choices.All(c => c.Boundary.Length == 0),
            "Headbutt must enumerate settled retrieval alternatives: " + string.Join(',', choices.Select(c => c.Boundary)));
        Check(choices.Select(c => c.Choices.Single().Cards.Single().Id).Distinct().Count() == 2, "Headbutt alternatives lost identity.");
        foreach (var branch in choices)
        {
            var choice = branch.Choices.Single();
            var serialized = System.Text.Json.JsonSerializer.Serialize(choice);
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<KernelChoice>(serialized)!;
            var selected = roundTrip.Resolve(players[0].NetId, [strike, defend], 1, 1);
            Check(selected?.Single().Id.Entry == choice.Cards.Single().Id, "Wire choice must resolve to the same live card.");
            Check(roundTrip.Resolve(players[1].NetId, [strike, defend], 1, 1) is null, "Reject another player's choice.");
        }
        Check(root.Hand(players[0]).Contains(headbutt) && enemy.CurrentHp == 200, "Choice replay mutated root/live state.");
        var strikeChoice = choices.Select(c => c.Choices.Single()).First(c => c.Cards.Single().Id == strike.Id.Entry);
        var secondStrike = combat.CreateCard<StrikeIronclad>(players[0]);
        var duplicate = strikeChoice with { Cards = [strikeChoice.Cards[0] with { Occurrence = 1 }] };
        Check(ReferenceEquals(duplicate.Resolve(players[0].NetId, [strike, secondStrike], 1, 1)?.Single(), secondStrike),
            "Equal-looking cards must preserve their physical occurrence.");
        var altered = strikeChoice with { Cards = [strikeChoice.Cards[0] with { StateKey = "changed" }] };
        Check(altered.Resolve(players[0].NetId, [strike], 1, 1) is null, "Reject changed semantic state even when card ID still matches.");
        Console.WriteLine("PASS: P1-1 Headbutt choice branching, wire roundtrip and owner validation.");

        foreach (var p in players) Hand<DefendIronclad>(p);
        root = KernelSession.Capture(combat);
        var ended = root.Fork();
        Check(ended.EndTurn(players[0], out var reason), "First ready: " + reason);
        Check(ended.IsReady(players[0]) && !ended.CanPlay(headbutt) && !ended.EnemyPhaseCompleted
            && ended.Hand(players[0]).Count == root.Hand(players[0]).Count, "Individual readiness must not flush any hand.");
        Check(ended.EndTurn(players[1], out reason), "Second ready: " + reason);
        Check(!ended.EnemyPhaseCompleted && ended.Hp(players[0].Creature) == 80, "Never implicitly finish the remaining player.");
        var complete = ended.EndTurn(players[2], out reason);
        Check(complete, "Full round: " + reason + " " + ended.LastRoundFailure);
        Check(ended.EnemyPhaseCompleted, "Complete enemy phase marker missing.");
        Check(players.All(p => ended.Hp(p.Creature) == 79), "Monster must damage every living player once: " + string.Join(',', players.Select(p => ended.Hp(p.Creature))));
        Check(players.All(p => ended.Hand(p).Count == 0), "Shared boundary must flush each hand once.");
        Check(!ended.EndTurn(players[2], out _) && players.All(p => ended.Hp(p.Creature) == 79), "Repeated end must not run enemies twice.");
        Check(root.Hp(players[0].Creature) == 80 && players.All(p => p.Creature.CurrentHp == 80), "Round replay leaked to root/live.");
        Console.WriteLine("PASS: P1-1 per-player readiness, shared hand flush, full-party monster hit and no repeated enemy phase.");

        players[0].Creature.SetCurrentHpInternal(1);
        ended = KernelSession.Capture(combat);
        foreach (var p in players) Check(ended.EndTurn(p, out reason), "Death round: " + reason);
        Check(ended.Hp(players[0].Creature) == 0 && ended.Hp(players[1].Creature) == 79 && ended.Hp(players[2].Creature) == 79,
            "One player's death must not stop damage to remaining teammates.");
        Console.WriteLine("PASS: P1-1 enemy attacks continue after the first teammate dies.");

        (CombatState Combat, Player[] Party, MegaCrit.Sts2.Core.Entities.Creatures.Creature Enemy) Encounter<T>(string moveId) where T : MonsterModel
        {
            var party = Enumerable.Range(1, 3).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(60 + i))).ToArray();
            var state = new CombatState(encounter: typeof(T) == typeof(LivingFog)
                ? ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.LivingFogNormal>().ToMutable() : null,
                runState: RunState.CreateForTest(party, seed: "P1-SPECIAL"));
            foreach (var p in party)
            {
                p.ResetCombatState(); state.AddPlayer(p); p.Creature.SetMaxHpInternal(100); p.Creature.SetCurrentHpInternal(100);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            }
            var foe = state.CreateCreature(ModelDb.Monster<T>().ToMutable(), CombatSide.Enemy,
                typeof(T) == typeof(LivingFog) ? "livingFog" : "0");
            state.AddCreature(foe); foe.Monster!.SetUpForCombat();
            foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States[moveId], true);
            return (state, party, foe);
        }
        KernelSession Finish(CombatState state, Player[] party)
        {
            var branch = KernelSession.Capture(state);
            foreach (var p in party)
            {
                var ok = branch.EndTurn(p, out var failure);
                Check(ok, "Special enemy phase: " + failure + " " + branch.LastRoundFailure);
            }
            return branch;
        }
        var rage = Encounter<SludgeSpinner>("RAGE_MOVE");
        var rageState = Finish(rage.Combat, rage.Party);
        Check(rageState.Power<StrengthPower>(rage.Enemy) == 3, "Three targets must not triple SludgeSpinner growth.");
        Check(rage.Party.All(p => rageState.Hp(p.Creature) == 94), "SludgeSpinner attack must precede its Strength gain.");
        var settledMetrics = new KernelCombatEvaluation(rage.Combat, rage.Party, null, futureRounds: 0).Evaluate(rageState);
        Check(settledMetrics.HpLoss == 18, "Completed enemy phase must not charge incoming intent twice: " + settledMetrics.HpLoss);
        var spray = Encounter<SludgeSpinner>("OIL_SPRAY_MOVE");
        var sprayState = Finish(spray.Combat, spray.Party);
        Check(spray.Party.All(p => sprayState.Power<WeakPower>(p.Creature) == 1), "Oil spray must weaken all three targets.");
        Check(spray.Party.All(p => p.Creature.Powers.Count == 0 && p.Creature.CurrentHp == 100), "Party debuffs leaked to live combat.");
        var fog = Encounter<LivingFog>("BLOAT_MOVE");
        var fogState = Finish(fog.Combat, fog.Party);
        var bombs = fogState.Enemies.Where(e => e.Monster is GasBomb).ToArray();
        Check(bombs.Length == 1 && fog.Combat.Enemies.Count == 1, "Fog summons duplicated or leaked to the live encounter: " + bombs.Length);
        Console.WriteLine("PASS: P1-1 real monster growth once, party-wide Weak, branch-local summons.");

        var worm = Encounter<TheInsatiable>("LIQUIFY_GROUND_MOVE");
        var wormState = Finish(worm.Combat, worm.Party);
        Check(worm.Party.All(p => wormState.SandpitTurnsRemaining(p.Creature) == 4), "Sandpit target-specific instances must exist for every player: "
            + string.Join(',', worm.Party.Select(p => wormState.SandpitTurnsRemaining(p.Creature)?.ToString() ?? "missing")));
        Console.WriteLine("PASS: P1-1 native sandworm phase applies each player's Sandpit/Escape transaction.");

        // End-turn candidates must not replace a discovered lethal with a worse
        // complete nonlethal round simply because lethal needs no explicit end.
        var lethal = Encounter<SludgeSpinner>("RAGE_MOVE");
        lethal.Enemy.SetCurrentHpInternal(6);
        var lethalStrike = lethal.Combat.CreateCard<StrikeIronclad>(lethal.Party[0]);
        lethal.Party[0].PlayerCombatState!.Hand.AddInternal(lethalStrike);
        var searchRoot = KernelSession.Capture(lethal.Combat);
        using var search = new KernelTeamSearch(searchRoot, lethal.Party,
            s => (s.HasWon ? 10000 : 0) - s.Hp(lethal.Enemy) * 10 + lethal.Party.Sum(p => s.Hp(p.Creature)),
            new(Depth: 6, Width: 12, MaxNodes: 200, IncludeEndTurns: true));
        while (!search.Advance(TimeSpan.FromMilliseconds(2), () => true)) { }
        Check(search.CompletedResult!.Actions.FirstOrDefault()?.Card == lethalStrike && search.CompletedState!.HasWon,
            "A complete nonlethal round must not displace an immediate lethal.");
        Console.WriteLine("PASS: P1-1 completed-round search preserves immediate lethal over premature ending.");

        void ChoiceCard<T>(Action<CombatState, Player> setup, int minBranches) where T : CardModel
        {
            var e = Encounter<SludgeSpinner>("RAGE_MOVE");
            var p = e.Party[0];
            var card = e.Combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card);
            setup(e.Combat, p);
            var alternatives = KernelSession.Capture(e.Combat).CardBranches(card, p.Creature).ToArray();
            Check(alternatives.Length >= minBranches && alternatives.All(a => a.Boundary == "" && a.Choices.Count > 0),
                typeof(T).Name + " choices: " + string.Join(',', alternatives.Select(a => a.Boundary)));
        }
        ChoiceCard<Hologram>((s, p) =>
        {
            p.PlayerCombatState!.DiscardPile.AddInternal(s.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.DiscardPile.AddInternal(s.CreateCard<DefendIronclad>(p));
        }, 2);
        ChoiceCard<Armaments>((s, p) =>
        {
            p.PlayerCombatState!.Hand.AddInternal(s.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.Hand.AddInternal(s.CreateCard<DefendIronclad>(p));
        }, 2);
        ChoiceCard<Prepared>((s, p) =>
        {
            p.PlayerCombatState!.DrawPile.AddInternal(s.CreateCard<StrikeIronclad>(p));
            p.PlayerCombatState.Hand.AddInternal(s.CreateCard<DefendIronclad>(p));
        }, 2);
        var message = new BotChoicePlanMessage { Sequence = 3, Action = 42, Bot = 51, Card = "HEADBUTT",
            Payload = System.Text.Json.JsonSerializer.Serialize(choices[0].Choices) };
        var writer = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter(); message.Serialize(writer);
        var reader = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader(); reader.Reset(writer.Buffer);
        var decoded = new BotChoicePlanMessage(); decoded.Deserialize(reader);
        Check(decoded.Sequence == 3 && decoded.Action == 42 && decoded.Bot == 51 && decoded.Card == "HEADBUTT"
            && decoded.Payload == message.Payload && !decoded.Ack && !decoded.Clear, "Network plan packet roundtrip.");
        Console.WriteLine("PASS: P1-1 retrieval/upgrade/draw-discard choice branches and network packet roundtrip.");

        var ethereal = Encounter<SludgeSpinner>("RAGE_MOVE");
        for (var i = 0; i < 2; i++)
        {
            var p = ethereal.Party[i];
            ((DarkEmbracePower)ModelDb.Power<DarkEmbracePower>().ToMutable()).ApplyInternal(p.Creature, 1, true);
            for (var j = 0; j <= i; j++) p.PlayerCombatState!.Hand.AddInternal(ethereal.Combat.CreateCard<AscendersBane>(p));
            for (var j = 0; j < 5; j++) p.PlayerCombatState!.DrawPile.AddInternal(ethereal.Combat.CreateCard<StrikeIronclad>(p));
        }
        var etherealState = Finish(ethereal.Combat, ethereal.Party);
        Check(etherealState.Hand(ethereal.Party[0]).Count == 1 && etherealState.Hand(ethereal.Party[1]).Count == 2,
            "Ethereal-triggered draw must use each owner's count, not the team total: "
                + string.Join(',', ethereal.Party.Select(p => etherealState.Hand(p).Count)));
        Console.WriteLine("PASS: P1-1 owner-specific ethereal counts and end-turn Dark Embrace draw.");

        var extra = Encounter<SludgeSpinner>("RAGE_MOVE");
        ((AmbergrisPower)ModelDb.Power<AmbergrisPower>().ToMutable()).ApplyInternal(extra.Party[0].Creature, 1, true);
        var extraState = KernelSession.Capture(extra.Combat);
        Check(extraState.EndTurn(extra.Party[0], out _) && extraState.EndTurn(extra.Party[1], out _), "Extra-turn readiness remains per actor.");
        Check(!extraState.EndTurn(extra.Party[2], out var extraBoundary) && extraBoundary == "extra-player-turn"
            && !extraState.EnemyPhaseCompleted && extra.Party.All(p => p.Creature.CurrentHp == 100),
            "Unsupported extra turns must stop explicitly before running an incorrect enemy phase.");
        Console.WriteLine("PASS: P1-1 unresolved choice/extra-turn boundaries fail closed without live mutation.");
    }
}
