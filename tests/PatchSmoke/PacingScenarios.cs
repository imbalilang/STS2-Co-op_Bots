using System.Reflection;
using CoopBots;

internal static class PacingScenarios
{
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
        bool Due(long now, bool fast = false, bool paused = false) => (bool)due.Invoke(pace, new object[] { now, fast, paused })!;
        void Mark(long now, long planStarted = 0) => mark.Invoke(pace, new object[] { now, planStarted == 0 ? now : planStarted });
        Observe(1, 1000);
        Check(!Due(1999) && Due(2000), "AI must start after one second even when humans have not ended.");
        Mark(2000);
        Observe(1, 2500);
        Check(!Due(2500) && Due(3000), "Repeated ticks must not reset the timer or permit multiple bots within a second.");
        Check(!Due(2199, true) && Due(2200, true), "The final human end-turn must shorten the existing interval immediately to 200 ms.");
        Mark(2200);
        Check(!Due(2399, true) && Due(2400, true), "Fast mode must remain paced, not queue a burst.");
        Check(!Due(10000, true, true) && !Due(10000, false, true), "Pause must override both speed modes.");
        Mark(10000);
        Check(!Due(10000, true), "After a long stall only one action may be submitted; no catch-up burst.");
        Check(!Due(10500), "If a human un-ends their turn, restore the normal interval.");
        Observe(2, 11000);
        Check(!Due(11999) && Due(12000), "A new round must reset the team's normal pacing.");
        reset.Invoke(pace, null);
        Check(!Due(20000, true), "A reset or closed run must not retain an active timer.");
        observe.Invoke(pace, new object[] { new object(), 1, 21000L });
        Check(!Due(21999) && Due(22000), "Reload/new combat must start a fresh clock.");

        // Planning happens after the wait, so the search time is subtracted from
        // the next wait instead of being added to it: the cadence is
        // max(interval, plan), not interval + plan.
        Observe(3, 30000);
        Mark(30000, 30000);
        Check(!Due(30199, true) && Due(30200, true), "A fast plan must still keep the full 200 ms fast interval.");
        Mark(30200, 29600);
        Check(Due(30200, true), "A 600 ms kernel plan must not pay the 200 ms fast interval on top of the search.");
        Check(!Due(30599) && Due(30600), "Normal mode must still keep one second between actions when the plan was shorter than it.");
        Mark(30600, 0);
        Check(!Due(30799, true) && Due(30800, true), "A missing plan stamp must not zero the fast wait.");
        Console.WriteLine("PASS: autonomous 1-second team pace, immediate 200ms cleanup, pause, normal-speed restoration, no catch-up bursts, round/reload resets, plan time overlaps the wait.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
