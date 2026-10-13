using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CoopBots;

// Upstream records all seats but only writes a replay on request. OUR live-test
// harness archives that native format plus the original save automatically, to
// support the user's 2026-09-27 defeat diagnosis. Ordinary play remains off.
internal static class LiveCombatCapture
{
    private static readonly FieldInfo ReplayField = AccessTools.Field(typeof(CombatReplayWriter), "_replay");
    private static string? directory;
    private static string? current;
    private static int sequence;
    private static DateTime lastWrite;
    internal static bool Enabled => LiveTestMenuTicker.Armed && LiveTestAutoStart.Enabled;

    internal static void TraceTeamAction(MegaCrit.Sts2.Core.Combat.CombatState combat,
        MegaCrit.Sts2.Core.Models.CardModel card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        // Card submissions are already bot-owned. Keep their input visible in
        // manual sessions too; otherwise a reported hand cannot be reconstructed.
        var vulnerable = target?.GetPower<MegaCrit.Sts2.Core.Models.Powers.VulnerablePower>()?.Amount ?? 0;
        var hands = string.Join(";", combat.Players.Where(p => p.Creature.IsAlive && p.PlayerCombatState is not null)
            .Select(p => $"{p.NetId}:hp{p.Creature.CurrentHp}:b{p.Creature.Block}:e{p.PlayerCombatState!.Energy}:"
                + (p == card.Owner || Enabled ? string.Join(",", p.PlayerCombatState.Hand.Cards.Select(c =>
                    $"{c.Id.Entry}{(c.IsUpgraded ? "+" : "")}/{c.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.All)}")) : "")
                + ":powers=" + string.Join(",",p.Creature.Powers.Select(q=>$"{q.Id.Entry}:{q.Amount}"))));
        var enemies=string.Join(";",combat.Enemies.Where(e=>e.IsAlive).Select(e=>
            $"{e.CombatId}:hp{e.CurrentHp}:b{e.Block}:move{e.Monster?.NextMove.Id}:"
            + string.Join(",",e.Powers.Select(q=>$"{q.Id.Entry}:{q.Amount}"))));
        // Before enqueue: after PlayCardAction starts, the setup may already
        // have applied its power and no longer describes the decision's input.
        Log.Info($"CoopBots order: round={combat.RoundNumber} owner={card.Owner.NetId} card={card.Id.Entry} "
            + $"target={target?.CombatId} vulnerable={vulnerable} hands=[{hands}] enemies=[{enemies}]");
    }
    internal static string SerializeInitial(SerializableRun run)
        => JsonSerializer.Serialize(run, JsonSerializationUtility.GetTypeInfo<SerializableRun>());

    internal static byte[] SerializeReplay(CombatReplay replay)
    {
        var writer = new PacketWriter();
        writer.Write(replay.Anonymized());
        return writer.Buffer.AsSpan(0, writer.BytePosition).ToArray();
    }

    internal static void Begin(CombatReplayWriter writer, SerializableRun run)
    {
        if (!Enabled || !writer.IsRecordingReplay) return;
        try
        {
            directory ??= Path.Combine(Path.GetDirectoryName(typeof(ModEntry).Assembly.Location)!,
                "live-recordings", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
            Directory.CreateDirectory(directory);
            current = Path.Combine(directory, $"combat-{++sequence:D3}");
            File.WriteAllText(current + ".run.json", SerializeInitial(run));
            Log.Info($"CoopBots capture: initial={current}.run.json players={run.Players.Count} ascension={run.Ascension}");
            Flush(writer);
        }
        catch (Exception ex) { Log.Warn($"CoopBots capture: initial failed {ex.GetType().Name}: {ex.Message}"); }
    }

    internal static void Flush(CombatReplayWriter writer, bool periodic = false)
    {
        if (!Enabled || current is null || periodic && (DateTime.UtcNow - lastWrite).TotalSeconds < 15) return;
        try
        {
            if (ReplayField.GetValue(writer) is not CombatReplay replay) return;
            // Atomic replacement keeps the previous complete packet if a write
            // fails. Do not call StopRecording or change the game's recorder.
            var path = current + ".replay";
            File.WriteAllBytes(path + ".tmp", SerializeReplay(replay));
            File.Move(path + ".tmp", path, overwrite: true);
            lastWrite = DateTime.UtcNow;
            if (!periodic) Log.Info($"CoopBots capture: replay={path} players={replay.serializableRun.Players.Count} events={replay.events.Count} checksums={replay.checksumData.Count}");
        }
        catch (Exception ex) { Log.Warn($"CoopBots capture: replay failed {ex.GetType().Name}: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.RecordInitialState))]
internal static class LiveCaptureInitialPatch
{
    static void Prefix(CombatReplayWriter __instance) => LiveCombatCapture.Flush(__instance);
    static void Postfix(CombatReplayWriter __instance, SerializableRun serializableRun) => LiveCombatCapture.Begin(__instance, serializableRun);
}

[HarmonyPatch(typeof(CombatReplayWriter), "RecordChecksum")]
internal static class LiveCaptureProgressPatch
{
    static void Postfix(CombatReplayWriter __instance) => LiveCombatCapture.Flush(__instance, periodic: true);
}

[HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.StopRecording))]
internal static class LiveCaptureStopPatch
{
    static void Prefix(CombatReplayWriter __instance) => LiveCombatCapture.Flush(__instance);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class LiveCaptureEndPatch
{
    static void Prefix(RunManager __instance)
    {
        if (__instance.CombatReplayWriter is { } writer) LiveCombatCapture.Flush(writer);
    }
}
