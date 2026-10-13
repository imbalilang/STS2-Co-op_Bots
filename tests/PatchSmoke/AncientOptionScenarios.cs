using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 2026-10-01: each fault was injected into production, observed red, then restored.
// Commands and full traces: work/ancient-options-20261001/r2-*.json / r2-*.log.
// catalog -> System.Exception: Ancient options: native option coverage drift for NEOW
// missing-effect -> System.Exception: Ancient options: missing effect for NEOW.pages.INITIAL.options.CURSED_PEARL
// snecko -> System.Exception: Ancient options: random-cost draw must distinguish expensive and zero-cost decks
// snecko-free -> System.Exception: Ancient options: Confused must charge the cost of replacing zero-cost cards
// team-strength -> System.Exception: Ancient options: enemy Strength must charge the whole team
// energy-demand -> System.Exception: Ancient options: energy must reflect payable deck demand
// lethal-damage -> System.Exception: Ancient options: forced lethal damage must never become a profitable removal
// max-hp -> System.Exception: Ancient options: Talon must subtract max HP once without inventing current damage below the cap
// curse -> System.Exception: Ancient options: gold-plus-curse must include the curse and actual gold amount
// bell -> System.Exception: Ancient options: Bell cannot be three free relics
// cape -> System.Exception: Ancient options: Cape cannot omit its two curse payments
// pandora -> System.Exception: Ancient options: Pandora with no basic Strike or Defend must have zero replacement value
// tooth-delay -> System.Exception: Ancient options: Tooth must include the period before selected cards return
// empty-upgrade -> System.Exception: Ancient options: upgrades cannot improve an already upgraded deck
// instinct -> System.Exception: Ancient options: chosen Instinct enchants must improve eligible attacks
// radiant -> System.Exception: Ancient options: two energy once must be below four opening energy for this payable deck
// empty-mask -> System.Exception: Ancient options: Mask has no target without a power
// gauntlets -> System.Exception: Ancient options: Gauntlets must charge the power surcharge
// fiddle -> System.Exception: Ancient options: Fiddle must charge lost non-hand draw
// claws -> System.Exception: Ancient options: Claws native optional transform must select beneficial replacements
// enchantment-target -> System.Exception: Ancient options: native INSTINCT choice must use the priced enchantment target
// event-routing -> System.Exception: Ancient options: live event entry must use complete ancient option values
// option-order -> System.Exception: Ancient options: option order must not decide between different effects
// explanation -> System.Exception: Ancient options: live explanation must expose option cost components
// async-consistency -> System.Exception: Ancient options: async event planning must match read-only synchronous choice
// tooth-delay-value -> System.Exception: Ancient options: Tooth must include the period before selected cards return
// imbued-target -> System.Exception: Ancient options: native IMBUED choice must use the priced enchantment target
// clone-target -> System.Exception: Ancient options: native CLONE choice must use the priced enchantment target
// read-only-fixed -> System.Exception: Ancient options: previews must preserve deck, relics, HP, gold and run/player RNG: NEOW.pages.INITIAL.options.CURSED_PEARL
// native-transform-pool -> System.Exception: Ancient options: randomly transforming a curse must not turn it into an ordinary class card
internal static class AncientOptionScenarios
{
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static bool Exists(ref bool __result) { __result = false; return false; }
    // Only presentation is stubbed; the native option factory and configured payload run.
    private static bool Tips(ref IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip> __result)
    { __result = []; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Ancient options: " + message); }
    private static double Part(AncientOptionValue.Estimate value, string label) => double.Parse(
        value.Detail.Split(',').Single(s=>s.StartsWith(label+":"))[ (label.Length+1)..],System.Globalization.CultureInfo.InvariantCulture);
    private static readonly AncientEventModel[] Ancients = [ModelDb.Event<Neow>(), ModelDb.Event<Darv>(), ModelDb.Event<Nonupeipe>(),
        ModelDb.Event<Orobas>(), ModelDb.Event<Pael>(), ModelDb.Event<Tanx>(), ModelDb.Event<Tezcatara>(), ModelDb.Event<Vakuu>()];
    private static Player Fixture(int seats = 1, params string[] cards)
    {
        var players = Enumerable.Range(0,seats).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(i+1))).ToArray();
        var run = RunState.CreateForTest(players, ascensionLevel:10, seed:"ANCIENT-OPTIONS");
        foreach (var p in players)
        {
            typeof(Player).GetField("<Character>k__BackingField", BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(p, ModelDb.Character<Ironclad>());
            p.MaxEnergy=3; p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(64);
            foreach(var card in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(card);
            foreach(var id in cards.Length>0 ? cards : new[]{"STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH","POMMEL_STRIKE","INFLAME","SHRUG_IT_OFF"})
                p.Deck.AddInternal(ConstructionParallelScenarios.Card(id,p));
        }
        return players[0];
    }
    private static AncientOptionValue.Estimate Value(Player p, string relic)
    {
        var model=Ancients.First(m=>AncientOptionValue.Catalog[m.GetType().Name].Contains(relic));
        var option=model.AllPossibleOptions.First(o=>o.Relic?.GetType().Name==relic);
        return AncientOptionValue.Evaluate(p,model,option);
    }
    private static readonly JsonSerializerOptions StateJson = new() { IncludeFields=true };
    private static string State(Player p) => ConstructionDecision.Fingerprint(p) + JsonSerializer.Serialize(p.RunState.Rng.ToSerializable(),StateJson)
        + JsonSerializer.Serialize(p.PlayerRng.ToSerializable(),StateJson) + JsonSerializer.Serialize(p.Deck.Cards.Select(c=>c.ToSerializable()),StateJson);

    internal static void Run(string? output = null, bool skipInventory = false)
    {
        TestEnvironment.Ensure();
        using var evaluation = DeckStrength.BeginEvaluation();
        new Harmony("coopbots.test.ancient-options.format").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(AncientOptionScenarios),nameof(Format)));
        new Harmony("coopbots.test.ancient-options.exists").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"Exists",[typeof(string),typeof(string)]),
            prefix:new HarmonyMethod(typeof(AncientOptionScenarios),nameof(Exists)));
        new Harmony("coopbots.test.ancient-options.tips").Patch(AccessTools.PropertyGetter(typeof(RelicModel),"HoverTipsExcludingRelic"),
            prefix:new HarmonyMethod(typeof(AncientOptionScenarios),nameof(Tips)));
        var p=Fixture(); var before=State(p); var rows=new List<object>();var total=0;var variants=0;
        foreach(var ancient in skipInventory ? [] : Ancients)
        {
            var native=ancient.AllPossibleOptions.Where(o=>o.Relic is not null).ToArray();
            var nativeNames=native.Select(o=>o.Relic!.GetType().Name).Distinct().Order().ToArray();
            Check(nativeNames.SequenceEqual(AncientOptionValue.Catalog[ancient.GetType().Name].Order()),"native option coverage drift for "+ancient.Id.Entry);
            foreach(var option in native)
            {
                var score=AncientOptionValue.Evaluate(p,ancient,option);
                Check(State(p)==before,"previews must preserve deck, relics, HP, gold and run/player RNG: "+option.TextKey);
                Check(score.Modeled&&double.IsFinite(score.Value)&&!score.Detail.Contains("missing-native-effect"),"missing effect for "+option.TextKey);
                rows.Add(new{Ancient=ancient.Id.Entry,Option=option.TextKey,Payload=option.Relic!.GetType().Name,score.Value,score.Detail});variants++;
                Console.WriteLine($"ANCIENT {ancient.Id.Entry}/{option.Relic!.GetType().Name} {score.Value:F2} {score.Detail}");
            }
            total+=nativeNames.Length;
        }
        if (!skipInventory) Check(total==102,"expected all 102 native option mechanisms");

        var free=Fixture(1,"SLICE","SLICE","DEFLECT","DEFLECT","PREPARED","PREPARED","SLICE","DEFLECT");
        var heavy=Fixture(1,"BLUDGEON","BLUDGEON","IMPERvious".ToUpperInvariant(),"DEMON_FORM","BARRICADE","BLUDGEON","DEMON_FORM","BARRICADE");
        Check(Value(heavy,"SneckoEye").Value>Value(free,"SneckoEye").Value+10,"random-cost draw must distinguish expensive and zero-cost decks");
        Check(Value(free,"SneckoEye").Value<0,"Confused must charge the cost of replacing zero-cost cards");
        var team=Fixture(4);
        Check(Value(team,"PhilosophersStone").Value<Value(p,"PhilosophersStone").Value,"enemy Strength must charge the whole team");
        Check(Value(free,"Ectoplasm").Value<Value(heavy,"Ectoplasm").Value,"energy must reflect payable deck demand");
        p.Creature.SetCurrentHpInternal(10);
        Check(Value(p,"PrecariousShears").Value< -9000,"forced lethal damage must never become a profitable removal");
        p.Creature.SetCurrentHpInternal(64);
        Check(Value(p,"SereTalon").Detail.Contains("nine-max-hp-and-clipping:-9.00"),"Talon must subtract max HP once without inventing current damage below the cap");
        Check(Value(p,"CursedPearl").Value<Value(p,"GoldenPearl").Value*333/150,"gold-plus-curse must include the curse and actual gold amount");
        Check(Value(p,"CallingBell").Detail.Contains("unremovable-bell-curse:-"),"Bell cannot be three free relics");
        Check(Value(p,"DistinguishedCape").Detail.Contains("two-distinct-curses:-"),"Cape cannot omit its two curse payments");
        var clean=Fixture(1,"POMMEL_STRIKE","SHRUG_IT_OFF","INFLAME");
        Check(Math.Abs(Value(clean,"PandorasBox").Value)<1e-8,"Pandora with no basic Strike or Defend must have zero replacement value");
        var cursed=Fixture(1,"WRITHE");
        Check(Value(cursed,"NewLeaf").Value<50,"randomly transforming a curse must not turn it into an ordinary class card");
        var tooth=Value(p,"PaelsTooth");
        Check(Math.Abs(tooth.Value-Part(tooth,"delayed-return-upgrades"))>.01,"Tooth must include the period before selected cards return");
        var p2=Fixture();
        foreach(var card in p2.Deck.Cards.Where(c=>c.IsUpgradable)){card.UpgradeInternal();card.FinalizeUpgradeInternal();}
        Check(Value(p2,"YummyCookie").Value==0&&Value(p2,"SandCastle").Value==0,"upgrades cannot improve an already upgraded deck");
        Check(Value(p,"TriBoomerang").Value>0,"chosen Instinct enchants must improve eligible attacks");
        Check(Value(p,"RadiantPearl").Value>0 && Value(p,"RadiantPearl").Value<Value(p,"VeryHotCocoa").Value,
            "two energy once must be below four opening energy for this payable deck");
        var noPower=Fixture(1,"STRIKE_IRONCLAD","DEFEND_IRONCLAD");
        Check(Value(noPower,"JeweledMask").Value==0,"Mask has no target without a power");
        Check(Value(p,"SpikedGauntlets").Detail.Contains("power-cost-increase:-"),"Gauntlets must charge the power surcharge");
        Check(Value(p,"Fiddle").Detail.Contains("blocked-card-draw-engines:-"),"Fiddle must charge lost non-hand draw");

        // Exercise native callback metadata and the dispatcher, including min=0.
        AutoPilot.Set(p.NetId,true);
        try
        {
            var claws=ModelDb.Relic<Claws>().ToMutable();
            var callback=typeof(Claws).GetMethods(BindingFlags.NonPublic|BindingFlags.Instance)
                .First(m=>m.ReturnType==typeof(CardTransformation)&&m.GetParameters() is [{ParameterType:var t}]&&t==typeof(CardModel));
            var preview=Delegate.CreateDelegate(typeof(Func<CardModel,CardTransformation>),claws,callback);
            var method=typeof(CardSelectCmd).GetMethods().First(m=>m.Name==nameof(CardSelectCmd.FromDeckForTransformation)&&m.GetParameters().Any(a=>a.ParameterType==preview.GetType()));
            Check(BotCardChoiceDispatcher.TrySelect(method,[p,new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt,0,6),preview],out var chosenCards)
                && chosenCards is IEnumerable<CardModel> transformed && transformed.Any() && transformed.Count()<=6,
                "Claws native optional transform must select beneficial replacements");
            var plain=BotBrain.SelectCards(p,p.Deck.Cards,0,6,"FromDeckForTransformation");
            Check(plain.Count==0,"ordinary optional transforms retain their original policy");
            var enchMethod=typeof(CardSelectCmd).GetMethods().First(m=>m.Name==nameof(CardSelectCmd.FromDeckForEnchantment)&&m.GetParameters().Length==4&&m.GetParameters()[0].ParameterType==typeof(Player));
            foreach(var enchantment in new EnchantmentModel[]{ModelDb.Enchantment<Instinct>(),ModelDb.Enchantment<Imbued>(),ModelDb.Enchantment<Clone>()})
            {
                // This fixture has ordinary single-hit attacks, one power, no
                // X cards/exhaust. The owner's later CSV makes Instinct prefer
                // total damage before the old price; Imbued/Clone tie on tier.
                // R2: remove Instinct's damage ranking -> exit 1:
                // System.Exception: Ancient options: native INSTINCT choice must use static priorities before within-tier price
                // Evidence: work/decision-consistency-20261004/enchant-r2.json; restored exit 0.
                var best=p.Deck.Cards.Where(enchantment.CanEnchant)
                    .OrderByDescending(card=>enchantment is Instinct ? CardProfile.Of(card).Damage : 0)
                    .ThenByDescending(card=>AncientOptionValue.EnchantmentDelta(p,card,enchantment,1))
                    .ThenBy(card=>card.Id.Entry,StringComparer.Ordinal).First();
                Check(BotCardChoiceDispatcher.TrySelect(enchMethod,[p,enchantment,1,new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt,1)],out var picked)
                    && ((IEnumerable<CardModel>)picked!).Single()==best,"native "+enchantment.Id.Entry+" choice must use static priorities before within-tier price");
            }
        }
        finally { AutoPilot.Set(p.NetId,false); }

        var darv=(AncientEventModel)ModelDb.Event<Darv>().ToMutable();
        var options=darv.AllPossibleOptions.Where(o=>o.Relic is SneckoEye or EmptyCage or PhilosophersStone).ToList();
        options=options.OrderBy(o=>AncientOptionValue.Evaluate(team,darv,o).Value).ToList();
        typeof(EventModel).GetField("_currentOptions",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(darv,options);
        var expected=AncientOptionValue.Rank(team,darv).First();
        var chosen=(int)AccessTools.Method(typeof(BotEventDriver),"ChooseEventOption").Invoke(null,[team,darv])!;
        Check(chosen==expected.Index,"live event entry must use complete ancient option values");
        options.Reverse();
        var reversed=AncientOptionValue.Rank(team,darv).First();
        Check(reversed.Option==expected.Option,"option order must not decide between different effects");
        Check(EventHandlers.Explain(team,darv).Contains("enemy-strength-team-cost"),"live explanation must expose option cost components");
        using(var pump=new ConstructionParallelScenarios.Pump())
        {
            var stamp=State(team);
            var asyncChoice=pump.Run(()=>ConstructionDecision.RunAsync(team,()=>AncientOptionValue.Rank(team,darv).First().Option,()=>"ancient-test"));
            Check(asyncChoice==expected.Option&&State(team)==stamp,"async event planning must match read-only synchronous choice");
        }
        if(output is not null)File.WriteAllText(output,JsonSerializer.Serialize(new{mechanisms=total,variants,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(skipInventory ? "PASS: ancient option costs, target dispatch and read-only async planning."
            : $"PASS: all {total} ancient option mechanisms ({variants} native variants), costs, deck context, entry routing and read-only async planning.");
        foreach(var suffix in new[]{"format","exists","tips"})
        { var harmony=new Harmony("coopbots.test.ancient-options."+suffix); harmony.UnpatchAll(harmony.Id); }
    }
}
