using CoopBots;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// THE CONTINUE BUTTON LIES IN TWO ROOMS.
//
// BotRoomProceedDriver reads `button.IsEnabled` as "the room is done", which is true for
// every room except the two whose proceed button is enabled from the FIRST frame. Both
// must hold the continue until their own driver has settled.
//
// The reward-screen half is the one that regressed. Measured live 2026-09-22
// (godot.log:12851-12857, all-bot act 1 → act 2):
//
//   CoopBots room: pressed the continue for the handed-over seat (Boss)
//   [RewardsSetSynchronizer] Skipping remaining rewards ... because we're exiting the room
//   Reward set Id: 6 ... Rewards: GoldReward,PotionReward,CardReward   ← potion + card dropped
//   Run location changed to act 1 coord (null) room                    ← no event room
//
// `Creating NEventRoom` never appeared anywhere in that run, so the act-start NPC event and
// its 80 % heal were lost with the rewards. The policy is pinned here as a pure predicate
// because the wiring around it is UI-bound (an NProceedButton in a live Godot scene) and can
// only be read off a live log; the policy itself is the part that regressed.
internal static class RoomProceedScenarios
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("RoomProceed: " + message);
    }

    internal static void Run()
    {
        Check(!BotRoomProceedDriver.MustWaitForDriver(shopPending: false, rewardsPending: false),
            "an ordinary finished room must be allowed to proceed");

        Check(BotRoomProceedDriver.MustWaitForDriver(shopPending: true, rewardsPending: false),
            "the shop must hold the continue until BotShopDriver has settled every seat it shops for");

        // CHECKED (R2): spelling the predicate `shopPending || rewardsPending` as `shopPending`
        // alone turns this red with, verbatim:
        //
        //   System.InvalidOperationException: RoomProceed: the reward screen must hold the
        //   continue until every reward is taken — pressing it early drops the reward set AND
        //   the act-start event
        //      at RoomProceedScenarios.Check(...) RoomProceedScenarios.cs:line 25
        //      at RoomProceedScenarios.Run()     RoomProceedScenarios.cs:line 39
        Check(BotRoomProceedDriver.MustWaitForDriver(shopPending: false, rewardsPending: true),
            "the reward screen must hold the continue until every reward is taken — pressing it "
            + "early drops the reward set AND the act-start event");

        Check(BotRoomProceedDriver.MustWaitForDriver(shopPending: true, rewardsPending: true),
            "both drivers pending must still hold the continue");

        // CHECKED (R2): widening SyntheticBots to `state.Players` turns this red, verbatim:
        //   System.InvalidOperationException: RoomProceed: only synthetic bots may take the
        //   host-side chest reward
        // It would pay the handed-over human seat a second time through the host path while
        // its own local chest already runs the reward.
        var botA = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 7, 1));
        var botB = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 7, 2));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 76561198109201343UL);
        var state = RunState.CreateForTest(new[] { botA, botB, human }, seed: "TREASURE-REWARD");
        AutoPilot.Clear();
        var targets = BotTreasureRewardDriver.SyntheticBots(state);
        Check(targets.Count == 2 && targets.All(player => BotRegistry.IsBot(player.NetId)),
            "only synthetic bots may take the host-side chest reward");
        AutoPilot.Set(human.NetId, true);
        try
        {
            targets = BotTreasureRewardDriver.SyntheticBots(state);
            Check(targets.All(player => BotRegistry.IsBot(player.NetId)),
                "a handed-over human seat already has a local chest and must not be paid twice");
        }
        finally { AutoPilot.Clear(); }

        // The reward-screen driver takes every REWARD it can, but some leftover rewards
        // cannot be taken: a potion with every belt slot full, or (on a mixed table) a
        // card reward whose nested selection only exists behind the machine-wide selector.
        // The native RewardsSet.Offer task is awaiting that reward set's completion, so
        // the screen has to be finished by pressing its non-terminal Skip button or the
        // shop/room holds forever. The policy is pure because the button lookup itself
        // needs a live Godot scene; the guards matter: no skip when nothing remains (the
        // screen is about to remove itself), and no skip when there is no usable skip
        // button (a terminal reward screen's Proceed must not be pressed as a skip).
        //
        // CHECKED (R2, 2026-09-23): changing ShouldPressSkip to `=> hasEnabledSkipButton`
        // turns the second assertion below red, verbatim:
        //   System.InvalidOperationException: RoomProceed: an empty reward screen must be
        //   left to remove itself, not skip an already completed set
        //     at RoomProceedScenarios.Check(...) RoomProceedScenarios.cs:line 29
        //     at RoomProceedScenarios.Run()      RoomProceedScenarios.cs:line 91
        // Dropping the `!isTerminal` term turns the last assertion red, verbatim:
        //   System.InvalidOperationException: RoomProceed: a terminal reward screen's
        //   Proceed button is not a skip; never press it as one
        //     at RoomProceedScenarios.Check(...) RoomProceedScenarios.cs:line 29
        //     at RoomProceedScenarios.Run()      RoomProceedScenarios.cs:line 105
        Check(BotRewardsScreenDriver.ShouldPressSkip(hasRemainingRewardButton: true, hasEnabledSkipButton: true,
                isTerminal: false),
            "a non-terminal reward screen with an untakeable reward and a usable Skip button must be "
            + "completed, or RewardsSet.Offer never returns and the shop deadlocks");
        Check(!BotRewardsScreenDriver.ShouldPressSkip(hasRemainingRewardButton: false, hasEnabledSkipButton: true,
                isTerminal: false),
            "an empty reward screen must be left to remove itself, not skip an already completed set");
        Check(!BotRewardsScreenDriver.ShouldPressSkip(hasRemainingRewardButton: true, hasEnabledSkipButton: false,
                isTerminal: false),
            "a screen without a usable Skip button must not be skipped");
        Check(!BotRewardsScreenDriver.ShouldPressSkip(hasRemainingRewardButton: true, hasEnabledSkipButton: true,
                isTerminal: true),
            "a terminal reward screen's Proceed button is not a skip; never press it as one");

        Console.WriteLine("PASS: treasure-room reward selection pays synthetic bots only, "
            + "never the handed-over local seat; and a reward screen with an untakeable "
            + "reward is completed through its Skip button, never through terminal Proceed.");
    }
}
