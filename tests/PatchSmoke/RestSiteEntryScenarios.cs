using CoopBots;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;

internal static class RestSiteEntryScenarios
{
    internal static void Run()
    {
        // R2 2026-09-27, both production faults exit 1 then are restored:
        // Camp decisions must wait for entry relic healing to finish.
        // Camp decisions must hook completed room entry, not option creation.
        // Commands and logs: work/strength-20260927-v8b/r2-results.json.
        // Test the registered production hook, not just a detached helper.
        var entry = AccessTools.Method(typeof(RestSiteRoom), nameof(RestSiteRoom.EnterInternal));
        var hook = AccessTools.Method(typeof(BotRestSitePatch), "Postfix");
        if (Harmony.GetPatchInfo(entry)?.Postfixes.All(p => p.PatchMethod != hook) != false)
            throw new Exception("Camp decisions must hook completed room entry, not option creation.");
        var pendingEntry = new TaskCompletionSource();
        var hp = 50; var observed = -1;
        var completion = BotRestSitePatch.CompleteAfterEntry(pendingEntry.Task, () =>
        { observed = hp; return Task.CompletedTask; });
        if (observed != -1) throw new Exception("Camp decisions must wait for entry relic healing to finish.");
        hp = 65; // free entry healing lands after BeginRestSite
        pendingEntry.SetResult(); completion.GetAwaiter().GetResult();
        if (observed != 65) throw new Exception("Camp decisions must use HP after entry relic healing.");
        Console.WriteLine("PASS: the actual camp entry hook awaits room setup and free relic healing before choosing.");
        LocalChoiceLifecycle();
    }

    private static void LocalChoiceLifecycle()
    {
        // R2: six production mutations each fail with exit 1, then restore.
        // Local camp choice must use the broadcast path and wait for the selection.
        // Successful local camp choice must finish and await the native room UI.
        // Refused or throwing camp choice must restore options without finishing the room.
        // Successful camp choice must finish exactly once without restoring old choices.
        // Remote camp choices must not operate the local player's room UI or broadcast as local.
        // Commands/full failures: work/strength-20260927-v8c/r2-results.json.
        var calls = new List<string>();
        var selected = new TaskCompletionSource<bool>();
        var animation = new TaskCompletionSource();
        var completion = BotRestSitePatch.CompleteChoice(true,
            () => { calls.Add("local-message"); return selected.Task; },
            () => { calls.Add("remote"); return Task.FromResult(true); },
            () => calls.Add("disable"),
            () => { calls.Add("finish-ui"); return animation.Task; },
            () => calls.Add("restore"));
        if (!calls.SequenceEqual(new[] { "disable", "local-message" }))
            throw new Exception("Local camp choice must use the broadcast path and wait for the selection.");
        selected.SetResult(true);
        if (!calls.SequenceEqual(new[] { "disable", "local-message", "finish-ui" }) || completion.IsCompleted)
            throw new Exception("Successful local camp choice must finish and await the native room UI.");
        animation.SetResult();
        if (!completion.GetAwaiter().GetResult() || calls.Count != 3)
            throw new Exception("Successful camp choice must finish exactly once without restoring old choices.");
        foreach (var throws in new[] { false, true })
        {
            calls.Clear();
            try
            {
                var result = BotRestSitePatch.CompleteChoice(true,
                    () => throws ? Task.FromException<bool>(new InvalidOperationException("fixture")) : Task.FromResult(false),
                    () => Task.FromResult(true), () => calls.Add("disable"),
                    () => { calls.Add("finish-ui"); return Task.CompletedTask; },
                    () => calls.Add("restore")).GetAwaiter().GetResult();
                if (result) throw new Exception("Refused camp choice must not report success.");
            }
            catch (InvalidOperationException) when (throws) { }
            if (!calls.SequenceEqual(new[] { "disable", "restore" }))
                throw new Exception("Refused or throwing camp choice must restore options without finishing the room.");
        }
        calls.Clear();
        BotRestSitePatch.CompleteChoice(false,
            () => { calls.Add("local-message"); return Task.FromResult(true); },
            () => { calls.Add("remote"); return Task.FromResult(true); },
            () => calls.Add("disable"),
            () => { calls.Add("finish-ui"); return Task.CompletedTask; },
            () => calls.Add("restore")).GetAwaiter().GetResult();
        if (!calls.SequenceEqual(new[] { "remote" }))
            throw new Exception("Remote camp choices must not operate the local player's room UI or broadcast as local.");
        Console.WriteLine("PASS: camp choices complete the native local lifecycle, await UI, restore failures, and isolate remote seats.");
    }
}
