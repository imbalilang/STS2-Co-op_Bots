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
        var mark = type.GetMethod("MarkCardSubmitted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reset = type.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var combat = new object();
        void Observe(int round, long now) => observe.Invoke(pace, new object[] { combat, round, now });
        bool Due(long now, bool finished = false, bool paused = false, int interval = Pro)
            => (bool)due.Invoke(pace, new object[] { now, finished, paused, interval })!;
        void Mark(long now) => mark.Invoke(pace, new object[] { now });
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

        // Planning and the submit clock overlap in Runtime; computation never
        // earns a discount on the spacing of two successfully queued cards.
        Observe(4, 50_000);
        foreach (var planMs in new[] { 0, 200, 800 })
        {
            var submittedAt = 50_000 + planMs;
            Mark(submittedAt);
            Check(!Due(submittedAt + 1499) && Due(submittedAt + 1500),
                $"A {planMs} ms plan must retain a full 1500 ms between actual card submissions.");
        }
        Mark(60_000);
        Check(Due(70_000), "A delayed card must be immediately eligible once preparation or animation settles.");
        Mark(70_000);
        Check(!Due(70_001) && Due(71_500), "A late card restarts the clock without catch-up bursts.");
        Console.WriteLine("PASS: Pro 1.5s / Flash 0.5s while a human decides, no wait once everyone has finished, pause override, round/reload resets, no planning discount or catch-up bursts.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
