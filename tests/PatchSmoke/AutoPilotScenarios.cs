using CoopBots;

// The seat-handover feature has to be invisible until somebody uses it. Every gate
// it widened now reads AutoPilot.Drives instead of BotRegistry.IsBot, so the whole
// guarantee rests on one property: with nothing handed over, Drives must be exactly
// IsBot. These checks pin that, plus the two structural rules the design depends on
// (a synthetic bot is never a handover target, and taking a seat back restores the
// old behaviour rather than leaving a flag behind).
//
// This is a property test, not an integration test: it cannot prove the widened
// gates behave identically on a live board — no harness drives a human seat — but it
// does prove the predicate they all funnel through cannot change an answer on its own.
internal static class AutoPilotScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        AutoPilot.Clear();

        var botIds = new[]
        {
            BotRegistry.CreateId(BotDifficulty.Pro, 1, 0),
            BotRegistry.CreateId(BotDifficulty.Flash, 2, 1),
            BotRegistry.CreateId(BotDifficulty.Cheated, 3, 2),
        };
        // Real net ids: none of these has the reserved bot prefix in its top bits.
        var humanSeats = new[] { 76561198109201343UL, 1UL, ulong.MaxValue };

        foreach (var id in botIds)
        {
            Check(BotRegistry.IsBot(id), $"fixture: {id} must be a synthetic bot id.");
            Check(AutoPilot.Drives(id), $"a synthetic bot must always be driven ({id}).");
            Check(!AutoPilot.IsAutopiloted(id), "a synthetic bot is never a handed-over seat.");
            Check(AutoPilot.Label(id) == BotRegistry.DisplayName(id),
                "a synthetic bot must keep its tier name in logs.");
        }

        foreach (var id in humanSeats)
        {
            Check(!BotRegistry.IsBot(id), $"fixture: {id} must not look like a bot id.");
            Check(!AutoPilot.Drives(id),
                $"a real player must not be driven before the seat is handed over ({id}).");
            Check(!AutoPilot.IsAutopiloted(id), $"nothing is handed over yet ({id}).");
            Check(AutoPilot.Label(id) == id.ToString(),
                "an untouched seat must label as the plain player, never through the bot tier bits.");
        }
        Check(!AutoPilot.Any, "a cleared registry must report itself as unused.");

        var seat = humanSeats[0];
        Check(!AutoPilot.CanToggle(seat, isHost: false), "a non-host must not be offered the toggle.");
        Check(AutoPilot.CanToggle(seat, isHost: true), "the host must be offered the toggle.");
        Check(!AutoPilot.CanToggle(botIds[0], isHost: true),
            "a synthetic bot is added and removed through the roster, never handed over.");

        // A bot id must not be able to enter the handover set: Set is the only writer,
        // and if it accepted one, Drives would report a seat as both at once.
        AutoPilot.Set(botIds[0], true);
        Check(!AutoPilot.IsAutopiloted(botIds[0]), "Set must refuse a synthetic bot id.");
        Check(!AutoPilot.Any, "a refused write must not leave the registry in use.");

        AutoPilot.Set(seat, true);
        Check(AutoPilot.Drives(seat) && AutoPilot.IsAutopiloted(seat), "a handed-over seat is driven.");
        Check(AutoPilot.Any, "the registry must report itself in use while a seat is handed over.");
        Check(AutoPilot.Label(seat).Contains("Bot", StringComparison.Ordinal),
            "a handed-over seat must be distinguishable from an untouched one in logs.");
        foreach (var id in botIds)
            Check(AutoPilot.Drives(id), "handing one seat over must not disturb the synthetic bots.");
        foreach (var other in humanSeats.Skip(1))
            Check(!AutoPilot.Drives(other), "handing one seat over must not drive any other real seat.");

        AutoPilot.Set(seat, false);
        foreach (var id in humanSeats)
            Check(!AutoPilot.Drives(id), $"taking the seat back must restore the old behaviour ({id}).");
        Check(!AutoPilot.Any, "nothing may be left handed over after taking the seat back.");

        Console.WriteLine("PASS: seat handover is invisible until used — Drives equals IsBot for every seat "
            + "when nothing is handed over, a synthetic bot can never be handed over, and taking a seat "
            + "back restores the previous behaviour exactly.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("AutoPilot: " + message);
    }
}
