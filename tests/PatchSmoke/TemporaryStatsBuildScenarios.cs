using CoopBots.Building;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TemporaryStatsBuildScenarios
{
    // R2 2026-10-07: 32 isolated production-source faults compile exit0,
    // targeted assertion RED1; exact byte restore then build/assert GREEN0.
    // Production=mirror SHA: source-r2-final-manifest.json; complete raw
    // commands/logs: work/incombat-repair-20261007/temporary-stats/build/r2-results.json.
    // Original errors below are taken from those runs, not predicted text.
    // feeding-frenzy: System.InvalidOperationException: Temporary stat build: every positive native temporary card including FeedingFrenzy must use expiring profile facts at native base and upgrade amounts
    // temporary-scaling: System.InvalidOperationException: Temporary stat build: every positive native temporary card including FeedingFrenzy must use expiring profile facts at native base and upgrade amounts
    // setup-own-hit: System.InvalidOperationException: Temporary stat build: a one-energy SetupStrike hand must not spend its post-hit temporary Strength on itself (9.10000000 vs 7.00000000)
    // replay-earlier-grant: System.InvalidOperationException: Temporary stat build: each native replay must receive only earlier executions' temporary Strength (14.00000000 vs 16.10000000)
    // replay-grant-stock: System.InvalidOperationException: Temporary stat build: both replayed temporary grants must remain available to the subsequent attack without retroactive first-hit credit (24.20000000 vs 26.30000000)
    // temporary-dexterity: System.InvalidOperationException: Temporary stat build: temporary Dexterity must retain the bounded same-hand block payoff (5.00000000 vs 6.40000000)
    // payable-temporary-result: System.InvalidOperationException: Temporary stat build: the sustained payable path must reuse each realized FiendFire temporary burn exactly once (21.00000000 vs 31.50000000)
    // analytic-self-hit: System.InvalidOperationException: Temporary stat build: six-turn construction must not invent a same-card SetupStrike payoff in an otherwise empty deck (9.10000000 vs 7.00000000)
    // sustained-double-bonus: System.InvalidOperationException: Temporary stat build: the sustained payable path must reuse each realized FiendFire temporary burn exactly once (42.00000000 vs 31.50000000)
    // ally-solo-legality: System.InvalidOperationException: Temporary stat build: native and construction AnyAlly cards must reject one living player rather than invent a solo self buff
    // replay-required-discard: System.InvalidOperationException: Temporary stat build: native replayed Acrobatics must pay one required discard per execution, beside its single original hand slot (4.00000000 vs 3.00000000)
    // upgrade-preference-off: System.InvalidOperationException: Temporary stat build: owner twelve-card baseline must prefer PommelStrike's true additional draw to Bash and temporary SetupStrike upgrades
    // upgrade-rank-raw: System.InvalidOperationException: Temporary stat build: owner twelve-card baseline must prefer PommelStrike's true additional draw to Bash and temporary SetupStrike upgrades
    // upgrade-priority-fabricated-price: System.InvalidOperationException: Temporary stat build: upgrade preference must not fabricate a gain in the actual solitary deck potential (13.09290464 vs 3.09290464)
    // upgrade-no-basic-fallback: System.InvalidOperationException: Temporary stat build: all-basic decks must retain their original real upgrade fallback rather than an empty permanent choice
    // upgrade-forbidden-exception: System.InvalidOperationException: Temporary stat build: forbidden ordinary basic Defend upgrades cannot cancel a policy while real nonbasic choices exist
    // upgrade-cost-protection: System.InvalidOperationException: Temporary stat build: the hand-advantage priority must preserve useful native cost-reduction upgrades
    // upgrade-wounded-protection: System.InvalidOperationException: Temporary stat build: the hand-advantage priority must preserve useful wounded defensive upgrades
    // upgrade-unpayable-draw: System.InvalidOperationException: Temporary stat build: extra draw with no payable remaining attack or block must retain the ordinary upgrade ranking
    // upgrade-cooperative-policy: System.InvalidOperationException: Temporary stat build: real cooperative contributions must not erase the owner's requested starter net-draw upgrade priority
    // plot-caster-excluded: System.InvalidOperationException: Temporary stat build: Plot must carry one native caster draw and additional teammate support without duplicating either
    // plot-allied-estimate: System.InvalidOperationException: Temporary stat build: native living teammates with affordable revealed cards must supply bounded delayed support value beside the caster's one local draw
    // plot-payable-support: System.InvalidOperationException: Temporary stat build: the actual payable hand scoring path must consume delayed allied draw support exactly once (0.00000000 vs 1.05000000)
    // plot-sustained-support: System.InvalidOperationException: Temporary stat build: the actual sustained construction scoring path must preserve usable delayed allied draw benefit
    // camp-single-wrong-price: System.InvalidOperationException: Temporary stat build: single and team camp SMITH must price their actual policy-selected card rather than the larger unselected Bash gain
    // camp-team-wrong-price: System.InvalidOperationException: Temporary stat build: single and team camp SMITH must price their actual policy-selected card rather than the larger unselected Bash gain
    // screen-raw-order: System.InvalidOperationException: Temporary stat build: the actual permanent upgrade selector must apply the same starter hand-advantage policy
    // ancient-raw-order: System.InvalidOperationException: Temporary stat build: Ancient chosen-upgrade target previews must match the actual starter hand-advantage upgrade selector
    // event-raw-order: System.InvalidOperationException: Temporary stat build: event chosen-upgrade price must follow the actual selected Pommel native delta without inflating its priority into a price (14.75598566 vs 3.09290464)
    // tooth-native-count: System.InvalidOperationException: Temporary stat build: the actual native Pael's Tooth option price must wire the complete basic-inclusive five-card upgrade/removal package (20.66097986 vs 41.04320047)
    // performance-temporal: System.Exception: Construction performance: sampled hand results or stable tie order changed
    // performance-nonstat: System.Exception: Construction performance: sampled hand results or stable tie order changed
    // Native card execution is the timing oracle; .7 remains the existing
    // bounded construction overlap policy, not a native power amount.
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var presentation = new Harmony("coopbots.temporary-stats-build.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(TemporaryStatsBuildScenarios), nameof(Format)));
        try
        {
            var cases = new (string Name, Action Run)[] {
                ("family-mapping", FamilyMapping), ("setup-own-hit", SetupOwnHit),
                ("replay", Replay), ("cycle", Cycle), ("dexterity", Dexterity),
                ("upgrade-anchor", UpgradeAnchor), ("draw-upgrade", DrawUpgrade),
                ("plot", PlotTeam), ("upgrade-boundaries", UpgradeBoundaries),
                ("fiend-burns", FiendBurns), ("upgrade-camp-price", UpgradeCampPrice),
                ("ally-legality", AllyLegality), ("upgrade-team", UpgradeTeam),
                ("upgrade-eligibility", UpgradeEligibility), ("upgrade-events", UpgradeEvents) };
            if (caseName is not null && !cases.Any(c => c.Name == caseName)) throw new InvalidOperationException("Unknown temporary stat build case: " + caseName);
            foreach (var test in cases.Where(c => caseName is null || c.Name == caseName))
            { test.Run(); Console.WriteLine("PASS: temporary stat build " + test.Name); }
        }
        finally { presentation.UnpatchAll(presentation.Id); }
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException("Temporary stat build: " + why); }
    private static void Near(double value, double expected, string why) => Check(Math.Abs(value - expected) < 1e-8, why + $" ({value:F8} vs {expected:F8})");
    private static (Player Player, CombatState Combat, Creature Enemy) Board(int energy = 3,bool withAlly=false)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 981);
        var party=withAlly?new[]{p,Player.CreateForNewRun<Deprived>(UnlockState.all,982)}:new[]{p};
        var c = new CombatState(runState: RunState.CreateForTest(party, seed: "TEMP-STATS-BUILD"));
        foreach(var owner in party)
        {
            owner.MaxEnergy = energy; owner.ResetCombatState(); c.AddPlayer(owner);
            owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(80);
            owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(energy);
        }
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "stat-oracle");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(1000); e.SetCurrentHpInternal(1000);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (p, c, e);
    }
    private static CardModel Card(string id, Player p)
    { var c = ModelDb.AllCards.Single(x => x.Id.Entry == id).ToMutable(); c.Owner = p; return c; }
    private static T Hand<T>(CombatState c, Player p) where T : CardModel
    { var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static ConstructionHand.Result HandResult(params CardModel[] cards)
        => ConstructionHand.Solve(cards.Select(c => new ConstructionHand.Slot(c, CardProfile.Of(c))).ToArray(), cards.Length, new(3, 20, 1000));
    private static void FamilyMapping()
    {
        var (p, _, _) = Board();
        foreach (var id in new[] { "SETUP_STRIKE", "COORDINATE", "FEEDING_FRENZY", "ANTICIPATE", "FADE" })
        foreach (var upgrade in new[] { false, true })
        {
            var card = Card(id, p); if (upgrade) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
            var f = CardProfile.Of(card);
            var strength = id is "SETUP_STRIKE" or "COORDINATE" or "FEEDING_FRENZY";
            var amount = (double)card.DynamicVars[strength ? "StrengthPower" : "DexterityPower"].BaseValue;
            Check(f.Scaling == 0 && !f.Roles.Contains("scaling")
                && (strength ? f.Effects.TemporaryStrength : f.Effects.TemporaryDexterity) == amount,
                "every positive native temporary card including FeedingFrenzy must use expiring profile facts at native base and upgrade amounts");
        }
        foreach (var id in new[] { "CRUSH_UNDER", "DARK_SHACKLES", "DYING_STAR", "ENFEEBLING_TOUCH", "MANGLE", "PIERCING_WAIL", "MONARCHS_GAZE" })
        {
            var f = CardProfile.Of(Card(id, p));
            Check(f.Scaling == 0 && f.StrengthDown > 0, "native temporary enemy StrengthLoss family must retain current-side prevention without positive persistent scaling");
        }
    }
    private static void SetupOwnHit()
    {
        var (p, c, e) = Board(1); var setup = Hand<SetupStrike>(c, p);
        for (var i = 0; i < 6; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root = KernelSession.Capture(c); var native = root.ForkForReplay();
        Check(native.Play(setup, e, out var why), "native SetupStrike must execute: " + why);
        Check(native.Hp(e) == 993, "native SetupStrike must deal its 7 damage before granting 3 Strength");
        Check(native.EndTurn(p, 2, out why), "native expiry boundary must execute: " + why);
        var strike = native.Hand(p).OfType<StrikeIronclad>().First();
        Check(native.Play(strike, e, out why) && native.Hp(e) == 987,
            "native SetupStrike Strength must expire before next turn's ordinary Strike without Artifact");
        var single = ConstructionHand.Solve([new(setup, CardProfile.Of(setup, 1))], 1, new(1, 0, 1000));
        Near(single.Damage, 7, "a one-energy SetupStrike hand must not spend its post-hit temporary Strength on itself");
        var other = Card("STRIKE_IRONCLAD", p);
        var pair = ConstructionHand.Solve([new(setup, CardProfile.Of(setup)), new(other, CardProfile.Of(other))], 2, new(2, 0, 1000));
        Near(pair.Damage, 13 + 3 * PayableHands.TemporaryStatShare,
            "temporary Strength must value only later affordable attacks with the existing overlap share");
        Check(root.Hp(e) == 1000 && p.Creature.Powers.Count == 0, "native stat witnesses must preserve the root and live state");
    }
    private static void Replay()
    {
        var (p, c, e) = Board(1); var setup = Hand<SetupStrike>(c, p); setup.BaseReplayCount = 1;
        var native = KernelSession.Capture(c).ForkForReplay();
        Check(native.Play(setup, e, out var why) && native.Hp(e) == 983,
            "native SetupStrike replay must deal 7 then 10 before its second temporary grant: " + why);
        var result = ConstructionHand.Solve([new(setup, CardProfile.Of(setup, 1))], 1, new(1, 0, 1000));
        Near(result.Damage, 14 + 3 * PayableHands.TemporaryStatShare,
            "each native replay must receive only earlier executions' temporary Strength");
        var strike = Card("STRIKE_IRONCLAD", p);
        var pair = ConstructionHand.Solve([new(setup, CardProfile.Of(setup)), new(strike, CardProfile.Of(strike))], 2, new(2, 0, 1000));
        Near(pair.Damage, 20 + 9 * PayableHands.TemporaryStatShare,
            "both replayed temporary grants must remain available to the subsequent attack without retroactive first-hit credit");
    }
    private static void Cycle()
    {
        var (p, _, _) = Board(1); var setup = Card("SETUP_STRIKE", p);
        foreach (var t in SustainedDeckValue.Estimate([setup], 1))
            Near(t.Damage, 7 * t.Plays[setup], "six-turn construction must not invent a same-card SetupStrike payoff in an otherwise empty deck");
        foreach (var t in SustainedDeckValue.OutcomeEstimate([setup], 1))
            Near(t.Damage, 7 * t.Plays[setup], "payable sustained outcomes must reuse ordered temporary payoff instead of scoring it again against all attacks");
        var blank = (CardModel)setup.MutableClone(); blank.DynamicVars["StrengthPower"].BaseValue = 0;
        var full = DeckStrength.Evaluate([setup], p); var stripped = DeckStrength.Evaluate([blank], p);
        Near(full.Combat, stripped.Combat, "actual DeckStrength combat pipeline must not price unpayable self-buff attacks");
        Near(full.Cycle, stripped.Cycle, "actual DeckStrength cycle pipeline must not duplicate temporary self-hit payoff");
        Near(full.Hands, stripped.Hands, "actual DeckStrength opening pipeline must respect temporary grant timing");
        var feed = Card("FEEDING_FRENZY", p); feed.AddKeyword(CardKeyword.Exhaust);
        var deck = new[] { feed }.Concat(Enumerable.Range(0, 3).Select(_ => Card("STRIKE_IRONCLAD", p))).ToArray();
        var trace = SustainedDeckValue.OutcomeEstimate(deck, 1);
        Check(trace.Any(t => t.Plays[feed] > 0), "temporary event buff fixture must actually play FeedingFrenzy");
        Check(trace.Where(t => t.Plays[feed] == 0).All(t => t.Strength == 0),
            "spent temporary event buffs must not survive in the next sustained turn's Strength stock");
    }
    private static void Dexterity()
    {
        var (p, c, _) = Board(1); var anticipate = Hand<Anticipate>(c, p); var defend = Hand<DefendSilent>(c, p);
        var native = KernelSession.Capture(c).ForkForReplay();
        Check(native.Play(anticipate, null, out var why) && native.Play(defend, null, out why)
            && native.Block(p.Creature) == 7, "native temporary Dexterity must affect later paid block: " + why);
        var first = HandResult(anticipate, defend);
        Near(first.Block, 5 + 2 * PayableHands.TemporaryStatShare, "temporary Dexterity must retain the bounded same-hand block payoff");
        var second = HandResult(defend);
        Near(second.Block, 5, "the next construction hand must reset temporary Dexterity");
        var noBlock = HandResult(Card("FADE", p));
        Near(noBlock.Block, 0, "temporary Dexterity alone must not manufacture block events");
    }
    private static void UpgradeAnchor()
    {
        var (p, _, _) = Board();
        foreach (var card in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(card);
        foreach (var id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "SETUP_STRIKE", "POMMEL_STRIKE" })
            p.Deck.AddInternal(Card(id, p));
        var values=p.Deck.Cards.Where(c=>c is Bash or SetupStrike or PommelStrike)
            .ToDictionary(c=>c.Id.Entry,c=>BuildValue.UpgradeForDecision(c,p));
        foreach(var v in values)Console.WriteLine($"UPGRADE {v.Key}={v.Value.Total:F8} {v.Value.Reason}");
        var ranked=BuildValue.RankUpgrades(p.Deck.Cards.Where(c=>c is Bash or SetupStrike or PommelStrike)
            .Select(c=>(Card:c,Value:values[c.Id.Entry]))).ToArray();
        Check(ranked[0].Card is PommelStrike && ranked[0].Value.UpgradePriority>ranked[1].Value.UpgradePriority,
            "owner twelve-card baseline must prefer PommelStrike's true additional draw to Bash and temporary SetupStrike upgrades");
        Check(values["BASH"].UpgradePriority==values["SETUP_STRIKE"].UpgradePriority,
            "Bash and SetupStrike must retain the same ordinary upgrade policy tier without forcing their numerical gains to match");
        foreach(var card in ranked)
            Near(card.Value.Total,BuildValue.UpgradeDelta(card.Card,p).Total,
                "upgrade preference must not fabricate a gain in the actual solitary deck potential");
        var choices=p.Deck.Cards.Where(c=>c is Bash or SetupStrike or PommelStrike).ToArray();
        Check(BotBrain.SelectCards(p,choices,1,1,"FromDeckForUpgrade").Single() is PommelStrike,
            "the actual permanent upgrade selector must apply the same starter hand-advantage policy");
        Check(p.Deck.Cards.All(c=>!c.IsUpgraded),"upgrade ranking must leave every input card's native upgrade state unchanged");
    }
    private static void DrawUpgrade()
    {
        var (p,c,e)=Board();var pommel=Hand<PommelStrike>(c,p);pommel.UpgradeInternal();pommel.FinalizeUpgradeInternal();
        for(var i=0;i<5;i++)p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var native=KernelSession.Capture(c).ForkForReplay();
        Check(native.Play(pommel,e,out var why) && native.Hand(p).Count==2,
            "native upgraded PommelStrike must replace its own hand slot and draw two real cards: "+why);
        var plain=Card("POMMEL_STRIKE",p);var f=CardProfile.Of(plain);var upgraded=CardProfile.Upgraded(plain)!.Value.Facts;
        Near(CardProfile.NetDraw(plain,f),0,"ordinary one-card draw is throughput without hand advantage");
        Near(CardProfile.NetDraw(plain,upgraded),1,"two-card upgraded draw has exactly one true hand advantage");
        var prepared=Card("PREPARED",p);var preparedUp=CardProfile.Upgraded(prepared)!.Value;
        Near(CardProfile.NetDraw(prepared,CardProfile.Of(prepared)),0,"Prepared's mandatory discard must pay its original draw");
        Near(CardProfile.NetDraw(preparedUp.Card,preparedUp.Facts),0,"Prepared upgrade must not convert matching extra draw and discard into hand advantage");
        foreach(var id in new[]{"SHRUG_IT_OFF","DAGGER_THROW","BACKFLIP"})
        {
            var card=Card(id,p);var diff=CardProfile.DiffUpgrade(card)!;
            Check(diff.DrawDelta==0 && diff.After.NextTurnDraw-diff.Before.NextTurnDraw==0,
                "damage or block upgrades on unchanged cantrips must have no fabricated draw delta");
        }
        foreach(var (id,draw,gain) in new[]{("ACROBATICS",3d,1d),("BURNING_PACT",2d,1d),
            ("BATTLE_TRANCE",3d,1d),("OFFERING",3d,2d)})
        {
            var card=Card(id,p);var diff=CardProfile.DiffUpgrade(card)!;
            Near(diff.Before.Draw,draw,"gross draw must follow the local native base amount for "+id);
            Near(diff.DrawDelta,gain,"gross upgrade draw must follow the local native increment for "+id);
            Near(CardProfile.NetDraw(CardProfile.Upgraded(card)!.Value.Card,diff.After)
                -CardProfile.NetDraw(card,diff.Before),gain,
                "fixed native discard/exhaust payments must retain only the genuine upgrade hand advantage for "+id);
        }
        var replayed=Card("ACROBATICS",p);replayed.BaseReplayCount=1;
        Near(CardProfile.NetDraw(replayed,CardProfile.Of(replayed)),3,
            "native replayed Acrobatics must pay one required discard per execution, beside its single original hand slot");
        foreach(var id in new[]{"PREDATOR","PLOT","RELAX"})
        {
            var card=Card(id,p);var before=CardProfile.Of(card);var after=CardProfile.Upgraded(card)!.Value.Facts;
            Check(before.Draw==0 && after.Draw==0
                && (before.NextTurnDraw>0 && after.NextTurnDraw>0
                    || before.NextTurnDrawForTeam>0 && after.NextTurnDrawForTeam>0),
                "next-turn draw upgrades must stay separate from an immediate draw dividend");
        }
    }
    private static void PlotTeam()
    {
        var (p,c,_)=Board();var plot=Hand<Plot>(c,p);
        // Native GetTeammatesOf INCLUDES its source creature. The owner's
        // next draw is local; the separate support term excludes that owner.
        var native=KernelSession.Capture(c).ForkForReplay();
        Check(native.Play(plot,null,out var why),"native solo Plot oracle must execute its empty teammate loop: "+why);
        Check(native.Power<DrawCardsNextTurnPower>(p.Creature)==2,
            "native Plot GetTeammatesOf must include the caster, even in a solitary native oracle");
        var f=CardProfile.Of(plot);var upgrade=CardProfile.Upgraded(plot)!.Value;
        Check(f.NextTurnDraw==2 && f.NextTurnDrawForTeam==2 && f.AlliedDrawValue==0
            && upgrade.Facts.NextTurnDraw==3 && upgrade.Facts.NextTurnDrawForTeam==3,
            "Plot must carry one native caster draw and additional teammate support without duplicating either");
        Check(HandResult(plot).NextDraw==2,"the actual opening/payable hand path must schedule the native Plot caster draw exactly once");
        var (teamOwner,teamCombat,_)=Board(withAlly:true);var ally=teamOwner.RunState.Players.Single(x=>x!=teamOwner);
        var teamPlot=Hand<Plot>(teamCombat,teamOwner);
        var teamNative=KernelSession.Capture(teamCombat).ForkForReplay();
        Check(teamNative.Play(teamPlot,null,out why)
            && teamNative.Power<DrawCardsNextTurnPower>(teamOwner.Creature)==2
            && teamNative.Power<DrawCardsNextTurnPower>(ally.Creature)==2,
            "native Plot must grant one delayed draw counter to its caster and each living teammate: "+why);
        p=teamOwner;
        foreach(var owner in new[]{p,ally})foreach(var held in owner.Deck.Cards.ToArray())owner.Deck.RemoveInternal(held);
        foreach(var owner in new[]{p,ally})
        {
            for(var i=0;i<7;i++)owner.Deck.AddInternal(Card("DAZED",owner));
            owner.Deck.AddInternal(Card("STRIKE_IRONCLAD",owner));
        }
        var team=CardProfile.Of(Card("PLOT",p));
        Check(team.NextTurnDraw==2 && team.AlliedDrawValue>0,"native living teammates with affordable revealed cards must supply bounded delayed support value beside the caster's one local draw");
        var paid=ConstructionHand.Solve([new(teamPlot,team)],1,new(3,0,1000));
        var isolated=ConstructionHand.Solve([new(teamPlot,team with { AlliedDrawValue=0 })],1,new(3,0,1000));
        Near(paid.Utility-isolated.Utility,team.AlliedDrawValue,
            "the actual payable hand scoring path must consume delayed allied draw support exactly once");
        var supported=SustainedDeckValue.Strength([teamPlot],3,false);
        ally.Creature.SetCurrentHpInternal(0);
        Near(CardProfile.Of(Card("PLOT",p)).AlliedDrawValue,0,"a dead teammate cannot supply duplicate caster support value");
        Check(supported>SustainedDeckValue.Strength([teamPlot],3,false),
            "the actual sustained construction scoring path must preserve usable delayed allied draw benefit");
    }
    private static void UpgradeBoundaries()
    {
        Player Anchor()
        {
            var (p,_,_)=Board();
            foreach(var held in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(held);
            foreach(var id in new[]{"STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH","SETUP_STRIKE","POMMEL_STRIKE"})p.Deck.AddInternal(Card(id,p));
            return p;
        }
        void Preserved(Player p,string why)
        {
            var card=p.Deck.Cards.OfType<PommelStrike>().Single();
            var value=BuildValue.UpgradeForDecision(card,p);
            Check(!value.Reason.Contains("upgrade-policy:"),why);
        }
        var economy=Anchor();economy.Deck.AddInternal(Card("BODY_SLAM",economy));
        Preserved(economy,"the hand-advantage priority must preserve useful native cost-reduction upgrades");
        var wounded=Anchor();wounded.Deck.AddInternal(Card("SHRUG_IT_OFF",wounded));wounded.Creature.SetCurrentHpInternal(20);
        Preserved(wounded,"the hand-advantage priority must preserve useful wounded defensive upgrades");
        var woundsWithOnlyBasics=Anchor();woundsWithOnlyBasics.Creature.SetCurrentHpInternal(20);
        Check(BuildValue.UpgradeForDecision(woundsWithOnlyBasics.Deck.Cards.OfType<PommelStrike>().Single(),woundsWithOnlyBasics).UpgradePriority==1,
            "forbidden ordinary basic Defend upgrades cannot cancel a policy while real nonbasic choices exist");
        var constrained=Anchor();constrained.MaxEnergy=1;
        Preserved(constrained,"extra draw with no payable remaining attack or block must retain the ordinary upgrade ranking");
    }

    private static void FiendBurns()
    {
        // Three actual exhaust targets, with the buff played before the native
        // mass exhaust. The .7 macro amount is applied to each burn exactly once.
        foreach(var buffed in new[]{false,true})
        {
            var (p,c,e)=Board(2);var feed=Hand<FeedingFrenzy>(c,p);var fiend=Hand<FiendFire>(c,p);
            for(var i=0;i<3;i++)Hand<Dazed>(c,p);
            var native=KernelSession.Capture(c).ForkForReplay();
            if(buffed)Check(native.Play(feed,null,out var why),"native FeedingFrenzy must execute: "+why);
            else { p.PlayerCombatState!.Hand.RemoveInternal(feed); native=KernelSession.Capture(c).ForkForReplay(); }
            Check(native.Play(fiend,e,out var error) && native.Hp(e)==1000-(buffed?36:21),
                "native FiendFire must apply current temporary Strength once to each real exhaust hit: "+error);
            var hand=p.PlayerCombatState!.Hand.Cards.Select(x=>new ConstructionHand.Slot(x,CardProfile.Of(x,2))).ToArray();
            var macro=ConstructionHand.Solve(hand,hand.Length,new(2,0,1000));
            Near(macro.Damage,21+(buffed?15*PayableHands.TemporaryStatShare:0),
                "actual mass-exhaust hand must realize the bounded temporary bonus once per native target");
            Near(macro.TemporaryDamage,buffed?15*PayableHands.TemporaryStatShare:0,
                "mass-exhaust temporary diagnostic must isolate the realized bonus for sustained reuse");
            var trace=SustainedDeckValue.OutcomeEstimate(p.PlayerCombatState.Hand.Cards.ToArray(),2);
            if(buffed)Near(trace[0].Damage,31.5,
                "the sustained payable path must reuse each realized FiendFire temporary burn exactly once");
            foreach(var turn in trace)
                if(turn.Plays[fiend]>0)Console.WriteLine($"FIEND buffed={buffed} turn={turn.Turn} damage={turn.Damage:F8} feed={turn.Plays.GetValueOrDefault(feed):F8} fiend={turn.Plays[fiend]:F8}");
        }
    }

    private static void UpgradeCampPrice()
    {
        var (p,_,_)=Board();
        foreach(var held in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(held);
        foreach(var id in new[]{"STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH","SETUP_STRIKE","POMMEL_STRIKE"})p.Deck.AddInternal(Card(id,p));
        var priority=BuildValue.RankUpgrades(p.Deck.Cards.Select(card=>(Card:card,Value:BuildValue.UpgradeForDecision(card,p)))).First();
        var highest=p.Deck.Cards.Max(card=>BuildValue.UpgradeForDecision(card,p).Total);
        Check(priority.Card is PommelStrike && priority.Value.Total<highest,
            "the camp fixture must distinguish its actual preferred upgrade's price from the largest numerical alternative");
        var own=new RestSiteOption[]{new SmithRestSiteOption(p),new DigRestSiteOption(p)};
        var horizon=BotRestSitePatch.InvestmentHorizon(p);
        var preferred=priority.Value.Total*horizon;
        var dig=5+3*horizon;
        Check(preferred<dig && highest*horizon>dig,
            "the actual camp horizon must place its fixed alternative between the two distinguishable upgrade prices");
        var choose=AccessTools.Method(typeof(BotRestSitePatch),"Choose");
        var single=(BotRestSitePatch.CampPlan)choose.Invoke(null,[p,own])!;
        Check(single.Index==1 && BotRestSitePatch.ChooseTeam(p,own,_=>[],false).Index==1,
            "single and team camp SMITH must price their actual policy-selected card rather than the larger unselected Bash gain");
    }

    private static void AllyLegality()
    {
        foreach(var id in new[]{"COORDINATE","FADE"})
        {
            var (p,c,_)=Board();var card=Card(id,p);p.PlayerCombatState!.Hand.AddInternal(card);
            card.CanPlay(out var reason,out _);
            Check(reason.HasFlag(UnplayableReason.NoLivingAllies) && CardProfile.Of(card).Unplayable,
                "native and construction AnyAlly cards must reject one living player rather than invent a solo self buff");
            var payoff=Card(id=="COORDINATE"?"STRIKE_IRONCLAD":"DEFEND_IRONCLAD",p);
            var solo=HandResult(card,payoff);
            Near(id=="COORDINATE"?solo.Damage:solo.Block,id=="COORDINATE"?6:5,
                "unavailable solitary AnyAlly temporary stats cannot improve the owner's payable hand");
            var (multiOwner,multiCombat,_)=Board(withAlly:true);
            p=multiOwner;card=Card(id,p);p.PlayerCombatState!.Hand.AddInternal(card);
            payoff=Card(id=="COORDINATE"?"STRIKE_IRONCLAD":"DEFEND_IRONCLAD",p);
            // Forecast party membership uses the known run, just like native
            // combat membership; the same card may legally target itself now.
            card.CanPlay(out reason,out _);
            Check(!reason.HasFlag(UnplayableReason.NoLivingAllies) && card.IsValidTarget(p.Creature)
                && !CardProfile.Of(card).Unplayable,
                "native and construction multiplayer AnyAlly must retain legal same-side self targeting");
            var multi=HandResult(card,payoff);
            Check((id=="COORDINATE"?multi.Damage:multi.Block)>(id=="COORDINATE"?6:5),
                "legal multiplayer self temporary buffs must retain their later payable owner benefit");
        }
    }

    private static void UpgradeTeam()
    {
        var (p,_,_)=Board(withAlly:true);var ally=p.RunState.Players.Single(x=>x!=p);
        foreach(var held in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(held);
        foreach(var id in new[]{"STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH","SETUP_STRIKE","POMMEL_STRIKE"})p.Deck.AddInternal(Card(id,p));
        foreach(var held in ally.Deck.Cards.ToArray())ally.Deck.RemoveInternal(held);
        foreach(var id in new[]{"BULLY","BULLY","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD"})ally.Deck.AddInternal(Card(id,ally));
        var deck=p.Deck.Cards.ToArray();
        var valued=BuildValue.EligibleUpgrades(deck).Select(card=>(Card:card,Value:BuildValue.UpgradeForDecision(card,p))).ToArray();
        foreach(var pair in valued)
        {
            var clone=CardProfile.Upgraded(pair.Card)!.Value.Card;
            var team=TeamCoordinator.TeamBonus(clone,p,deck,null)-TeamCoordinator.TeamBonus(pair.Card,p,deck,null);
            Near(pair.Value.Total,BuildValue.UpgradeDelta(pair.Card,p).Total+(Math.Abs(team)<=.01?0:team),
                "upgrade priority must preserve the true combined native deck and cooperative contribution");
        }
        Check(BuildValue.RankUpgrades(valued).First().Card is PommelStrike
            && BotBrain.SelectCards(p,valued.Select(x=>x.Card).ToArray(),1,1,"FromDeckForUpgrade").Single() is PommelStrike,
            "real cooperative contributions must not erase the owner's requested starter net-draw upgrade priority");
        Check(p.Deck.Cards.All(x=>!x.IsUpgraded),"cooperative upgrade comparisons must use native isolated clones without mutating candidates");
    }

    private static void UpgradeEligibility()
    {
        var (p,_,_)=Board();foreach(var held in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(held);
        var basics=Enumerable.Range(0,5).Select(_=>Card("STRIKE_IRONCLAD",p)).ToArray();
        foreach(var card in basics)p.Deck.AddInternal(card);
        Check(BuildValue.EligibleUpgrades(basics).Count==5
            && basics.Contains(BotBrain.SelectCards(p,basics,1,1,"FromDeckForUpgrade").Single()),
            "all-basic decks must retain their original real upgrade fallback rather than an empty permanent choice");
        var pommel=Card("POMMEL_STRIKE",p);p.Deck.AddInternal(pommel);
        Check(BuildValue.EligibleUpgrades(p.Deck.Cards).SequenceEqual(new[]{pommel}),
            "ordinary camp and screen candidates must exclude forbidden plain basics whenever a legal nonbasic exists");
        var tooth=BotBrain.SelectCards(p,p.Deck.Cards.ToArray(),5,5,BotBrain.PaelsToothPurpose);
        Check(tooth.Count==5 && tooth.Count(BotBrain.IsForbiddenUpgrade)>=4,
            "native Pael's Tooth removal-plus-upgrade must still fill five and must not inherit ordinary upgrade filtering");
    }

    private static void UpgradeEvents()
    {
        var (p,_,_)=Board();foreach(var held in p.Deck.Cards.ToArray())p.Deck.RemoveInternal(held);
        foreach(var id in new[]{"STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH","SETUP_STRIKE","POMMEL_STRIKE"})p.Deck.AddInternal(Card(id,p));
        var type=typeof(AncientOptionValue).GetNestedType("Context",System.Reflection.BindingFlags.NonPublic)!;
        var context=Activator.CreateInstance(type,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,[p],null)!;
        var method=AccessTools.Method(type,"UpgradePlan");
        var plan=((CardModel[] Targets,double Value))method.Invoke(context,[1,false])!;
        Check(plan.Targets.Single() is PommelStrike,
            "Ancient chosen-upgrade target previews must match the actual starter hand-advantage upgrade selector");
        var expected=BuildValue.UpgradeDelta(p.Deck.Cards.OfType<PommelStrike>().Single(),p).Total;
        Near(plan.Value,expected,"Ancient chosen-upgrade price must use the real selected Pommel deck delta rather than the unselected Bash maximum");
        Near(EventTrade.BestUpgrades(p,1),expected,
            "event chosen-upgrade price must follow the actual selected Pommel native delta without inflating its priority into a price");
        var nativeCount=ModelDb.Relic<PaelsTooth>().DynamicVars["Cards"].IntValue;
        var tooth=((CardModel[] Targets,double Value))method.Invoke(context,[nativeCount,true])!;
        Check(tooth.Targets.Length==nativeCount && tooth.Targets.Any(BotBrain.IsForbiddenUpgrade),
            "Ancient Pael's Tooth native count must remain fillable with plain basics alongside its preferred nonbasic upgrades");
        var price=(AncientOptionValue.Estimate)AccessTools.Method(typeof(AncientOptionValue),"Price")
            .Invoke(null,[context,ModelDb.Relic<PaelsTooth>().ToMutable()])!;
        Check(price.Detail.Contains("delayed-return-upgrades:"),
            "native Pael's Tooth option must execute its full basic-inclusive package preview");
        var fights=(double)type.GetField("Fights",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(context)!;
        var removed=(double)AccessTools.Method(type,"Delta").Invoke(context,[p.Deck.Cards.Except(tooth.Targets)])!;
        Near(price.Value,tooth.Value*Math.Min(1,fights/Math.Max(1,nativeCount))*.75+removed*.5,
            "the actual native Pael's Tooth option price must wire the complete basic-inclusive five-card upgrade/removal package");
        Check(p.Deck.Cards.All(x=>!x.IsUpgraded),"all chosen event upgrade comparisons must leave the real native deck immutable");
    }

}
