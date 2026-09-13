using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal static class HumanFinisherHints
{
    private static CombatState? combat;
    private static long revision = -1, nextAttempt;
    private static string stamp = "";
    private static IEnumerator<HumanFinisher.Plan?>? steps;
    private static bool finished, visible;
    private static long started;

    internal static void Reset()
    {
        steps?.Dispose(); steps = null; combat = null; revision = -1; finished = false;
        if (visible) BotCalloutSync.Clear();
        visible = false;
    }

    internal static void Invalidate()
    {
        if (combat is not null && revision != KernelSession.LiveRevision) Reset();
    }

    internal static void Tick(CombatState state, bool botsFinished, long now)
    {
        if (!botsFinished || BotCooperation.Gate.Paused) { Reset(); return; }
        Invalidate();
        if (!ReferenceEquals(combat, state)) Reset();
        if (finished) return;
        try
        {
            if (steps is null)
            {
                if (now < nextAttempt || !state.HittableEnemies.Any(HumanFinisher.Threat)) return;
                nextAttempt = now + 500;
                combat = state;
                stamp = KernelSession.CaptureLiveStamp(state);
                var root = KernelSession.Capture(state);
                revision = KernelSession.LiveRevision;
                steps = HumanFinisher.Search(root, state).GetEnumerator();
                started = now;
                return; // Never combine uninterruptible capture/JIT with expansion.
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (!steps.MoveNext()) { finished = true; steps.Dispose(); steps = null; return; }
                if (steps.Current is { } plan)
                {
                    if (revision != KernelSession.LiveRevision || stamp != KernelSession.CaptureLiveStamp(state))
                    { Reset(); return; }
                    var names = string.Join(" → ", plan.Cards.Select(c => c.Title));
                    var target = plan.Enemy.Monster?.Id.Entry ?? "目标";
                    var seat = state.Players.ToList().IndexOf(plan.Human) + 1;
                    var text = $"玩家 {seat}（{plan.Human.Character.Title}）：当前用 {names}（共 {plan.Energy} 能量）预计可补杀 {target}。其他敌人的攻击仍需防御。";
                    BotCalloutSync.Publish(text, plan.Enemy.CombatId ?? 0);
                    visible = true; finished = true; steps.Dispose(); steps = null;
                    return;
                }
            } while (clock.Elapsed.TotalMilliseconds < 3 && now - started < 500);
            if (now - started >= 500) { finished = true; steps.Dispose(); steps = null; }
        }
        catch (Exception error)
        {
            Reset(); nextAttempt = now + 2000;
            Log.Warn("CoopBots finisher verification skipped: " + error.GetType().Name);
        }
    }
}
