using System.Reflection;
using CoopBots;

internal static class PacingScenarios
{
    // Difficulty is a pacing preset for the human-facing phase: Pro waits 1.5s
    // between cards and Flash 0.5s. Once every human has finished there is no
    // wait at all — the team plays its remaining cards as fast as it thinks, so
    // the player is not kept watching a trickle of bot actions.
    private const int Pro = 1500;
    private const int Flash = 500;

    internal static void Run()
    {
        var type = typeof(BotBrain).Assembly.GetType("CoopBots.CombatPacing")!;
        var pace = Activator.CreateInstance(type, true)!;
        var observe = type.GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var due = type.GetMethod("IsDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mark = type.GetMethod("MarkAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reset = type.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var combat = new object();
        void Observe(int round, long now) => observe.Invoke(pace, new object[] { combat, round, now });
        bool Due(long now, bool finished = false, bool paused = false, int interval = Pro)
            => (bool)due.Invoke(pace, new object[] { now, finished, paused, interval })!;
        void Mark(long now, long planStarted = 0) => mark.Invoke(pace, new object[] { now, planStarted == 0 ? now : planStarted });
        Observe(1, 1000);
        Check(!Due(2499) && Due(2500), "Pro bots must start after 1.5 seconds while a human is still deciding.");
        Mark(2500);
        Observe(1, 2500);
        Check(!Due(2500) && Due(4000), "Repeated ticks must not reset the timer or permit multiple bots within 1.5 seconds.");
        Check(Due(2500, true), "Once every human has finished the team must act with no wait at all.");
        Check(Due(10_000, true), "A finished team stays immediately due; the caller submits one action per tick.");
        Check(!Due(10_000, true, true) && !Due(10_000, false, true), "Pause must override both phases.");
        Mark(10_000);
        Check(!Due(11_499) && Due(11_500), "If a human keeps deciding, the Pro interval applies again.");
        Observe(2, 11_000);
        Check(!Due(12_499) && Due(12_500), "A new round must reset the team's pacing.");
        reset.Invoke(pace, null);
        Check(!Due(20_000, true), "A reset or closed run must not retain an active timer.");
        observe.Invoke(pace, new object[] { new object(), 1, 21_000L });
        Check(!Due(22_499) && Due(22_500), "Reload/new combat must start a fresh clock.");

        // Flash is the same algorithm at a shorter human-facing cadence, and the
        // finished phase is unpaced for both tiers.
        Observe(3, 40_000);
        Check(!Due(40_499, interval: Flash) && Due(40_500, interval: Flash), "Flash bots must act every 500 ms.");
        Mark(40_500);
        Check(Due(40_500, true, false, Flash), "Flash cleanup must not wait either.");

        // A slow plan is not charged twice: in the human-facing phase the wait is
        // shortened by how long the search actually took.
        Observe(4, 50_000);
        Mark(50_000, 50_000);
        Check(!Due(51_499) && Due(51_500), "Pro mode must keep 1.5 seconds between actions when the plan was short.");
        Mark(51_500, 50_700);
        Check(!Due(52_199) && Due(52_200), "An 800 ms plan must shorten the wait to the remaining 700 ms, not pay 1.5 s on top.");
        Mark(52_000, 0);
        Check(!Due(53_499) && Due(53_500), "A missing plan stamp must not shorten the human-facing wait.");
        Console.WriteLine("PASS: Pro 1.5s / Flash 0.5s while a human decides, no wait once everyone has finished, pause override, round/reload resets, plan time overlaps the wait.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
