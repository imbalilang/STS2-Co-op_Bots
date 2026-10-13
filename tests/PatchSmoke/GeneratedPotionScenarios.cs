using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class GeneratedPotionScenarios
{
    // R2 2026-09-28: removed free costs / full-belt eligibility, used root RNG,
    // removed capacity guard and reversed live choice ranking. Each exited 1;
    // restored byte-for-byte. work/power-investigation-20260928/r2-potion-*.log:
    // System.Exception: Full-belt generated potion must use a profitable free card without a death forecast.
    // System.Exception: Potion preview must not advance root RNG between identical probes.
    // System.Exception: A full hand must not waste a generated-card bottle, even at predicted death.
    // System.Exception: The live potion chooser must prefer five useful block over Bash when facing ten damage with no follow-up energy.
    private static void Check(bool value,string why) { if(!value) throw new Exception(why); }
    private static bool Format(ref string __result) { __result=""; return false; }
    private static bool SkipStartingRelics() => false;
    internal static void Run()
    {
        new Harmony("coopbots.test.generated-potions").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(GeneratedPotionScenarios),nameof(Format)));
        // The console host has no SaveManager. Skip only starting inventory's
        // seen-relic persistence; leave the real Silent card pool and RNG intact.
        var inventory = new Harmony("coopbots.test.generated-potions.inventory");
        var relicInit=AccessTools.Method(typeof(Player),"PopulateStartingRelics");
        inventory.Patch(relicInit,prefix:new HarmonyMethod(typeof(GeneratedPotionScenarios),nameof(SkipStartingRelics)));
        Player p;
        try { p=Player.CreateForNewRun<Silent>(UnlockState.all,BotRegistry.CreateId(BotDifficulty.Pro,35,0)); }
        finally { inventory.Unpatch(relicInit,HarmonyPatchType.Prefix,inventory.Id); }
        var run=RunState.CreateForTest([p],seed:"GENERATED-POTIONS");
        var combat=new CombatState(runState:run);
        p.ResetCombatState();combat.AddPlayer(p);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
        p.PlayerCombatState!.Phase=PlayerTurnPhase.Play; // zero energy: these cards are free this turn
        for(var i=0;i<10;i++)p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeSilent>(p));
        var e=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"enemy");
        combat.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(500);e.SetCurrentHpInternal(500);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,9,true);
        var attack=ModelDb.Potion<AttackPotion>().ToMutable();p.AddPotionInternal(attack,silent:true);
        var colorless=ModelDb.Potion<ColorlessPotion>().ToMutable();p.AddPotionInternal(colorless,silent:true);
        while(p.Potions.Count()<p.MaxPotionCount)p.AddPotionInternal(ModelDb.Potion<StrengthPotion>().ToMutable(),silent:true);
        var root=KernelSession.Capture(combat);var original=root.StateText(p);
        foreach(var bottle in new PotionModel[]{attack,colorless})
        {
            var offers=root.PreviewPotionOffers(bottle,p.Creature,[p]);
            Console.WriteLine($"PROBE {bottle.Id.Entry}="+string.Join(',',offers.Select(o=>$"{o.CardId}:{o.Benefit:F2}")));
            Check(offers.Count==3,"Generated potion must preview the three native RNG offers.");
            Check(offers.SequenceEqual(root.PreviewPotionOffers(bottle,p.Creature,[p])),
                "Potion preview must not advance root RNG between identical probes.");
            var dose=KernelPotionOpportunity.Measure(root,bottle,p.Creature,[p]);
            Check(dose.Benefit>0 && dose.FullValue,"Full-belt generated potion must use a profitable free card without a death forecast.");
            Check(!KernelSession.CanModelPotion(bottle) && !root.Fork().UsePotion(bottle,p.Creature,out _),
                "Previewing a choice must not falsely make a choice-less combat script replayable.");
        }
        var choice=KernelPotionOpportunity.Choose(root,[p],[p]);
        Check(choice?.Action.Potion is AttackPotion or ColorlessPotion && choice.Reason.StartsWith("full-belt"),
            "Full belt containing generated-card bottles must produce an active potion opportunity.");
        var bash=combat.CreateCard<Bash>(p);var defend=combat.CreateCard<DefendSilent>(p);
        Check(root.GeneratedCardBenefit(bash,[p])>0 && bash.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.All)==2,
            "Potion candidate must be evaluated at zero cost without mutating the original card cost.");
        Check(BotBrain.SelectGeneratedPotionCard(p,[bash,defend]) == defend,
            "The live potion chooser must prefer five useful block over Bash when facing ten damage with no follow-up energy.");
        Check(root.StateText(p)==original && root.HeldPotionCount(p)==p.MaxPotionCount && p.PlayerCombatState.Hand.Cards.Count==0,
            "Potion previews must preserve root and live hand, energy, HP and inventory.");
        // Isolate the two chooser bottles for the lethal-hand case. Permanent
        // Strength was only the filler needed for the full-belt fixture above.
        foreach(var filler in p.Potions.OfType<StrengthPotion>().ToArray()) p.DiscardPotionInternal(filler,silent:true);
        p.Creature.SetCurrentHpInternal(1);
        var free = combat.CreateCard<Shiv>(p);p.PlayerCombatState.Hand.AddInternal(free);
        for(var i=0;i<9;i++)p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(p));
        var crowded=KernelSession.Capture(combat);
        var wipe=new TerminalRecord(false,EvidenceKind.VerifiedTerminal,"",[0],1,0);
        foreach(var bottle in new PotionModel[]{attack,colorless})
        foreach(var emergency in new[]{false,true})
            Check(KernelPotionOpportunity.Measure(crowded,bottle,p.Creature,[p],emergency).Benefit==0,
                "A full hand must not waste a generated-card bottle, even at predicted death.");
        Check(KernelPotionOpportunity.Choose(crowded,[p],[p],wipe)?.Action.Potion is not (AttackPotion or ColorlessPotion),
            "The emergency chooser must not bypass the full-hand capacity check.");
        Check(crowded.Play(free,e,out _) && crowded.Hand(p).Count==9,
            "A real free attack must open one hand slot before drinking.");
        var freed=KernelPotionOpportunity.Choose(crowded,[p],[p],wipe);
        Console.WriteLine($"PROBE freed potion={freed?.Action.Potion?.Id.Entry} reason={freed?.Reason} attack={KernelPotionOpportunity.Measure(crowded,attack,p.Creature,[p],true)}");
        Check(freed?.Action.Potion is AttackPotion or ColorlessPotion,
            "After playing a card, the emergency chooser must immediately reconsider useful generated-card potions.");
        Console.WriteLine("PASS: generated-card potions use cloned RNG offers, native free costs and proactive full-belt opportunities.");
    }
}
