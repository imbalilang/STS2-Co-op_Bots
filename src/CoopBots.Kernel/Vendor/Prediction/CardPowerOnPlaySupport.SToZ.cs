using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel.Vendor;

internal static partial class CardPowerOnPlaySupport
{
    private static void ApplyLate(
        SimulatedCombatState combat,
        CardModel card,
        Creature owner)
    {
        switch (card)
        {
            case SeekingEdge:
                combat.Apply<SeekingEdgePower>(owner, 1, owner);
                break;
            case SentryMode:
                combat.Apply<SentryModePower>(owner, card.DynamicVars["SentryModePower"].IntValue, owner);
                break;
            case SerpentForm:
                combat.Apply<SerpentFormPower>(owner, card.DynamicVars["SerpentFormPower"].IntValue, owner);
                break;
            case Shadowmeld:
                combat.Apply<ShadowmeldPower>(owner, card.DynamicVars["Power"].IntValue, owner);
                break;
            case Shroud:
                combat.Apply<ShroudPower>(owner, card.DynamicVars.Block.IntValue, owner);
                break;
            case SignalBoost:
                combat.Apply<SignalBoostPower>(owner, card.DynamicVars["SignalBoostPower"].IntValue, owner);
                break;
            case SleightOfFlesh:
                combat.Apply<SleightOfFleshPower>(owner, card.DynamicVars["SleightOfFleshPower"].IntValue, owner);
                break;
            case Smokestack:
                combat.Apply<SmokestackPower>(owner, card.DynamicVars["SmokestackPower"].IntValue, owner);
                break;
            case SpectrumShift:
                combat.Apply<SpectrumShiftPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case Speedster:
                combat.Apply<SpeedsterPower>(owner, card.DynamicVars["SpeedsterPower"].IntValue, owner);
                break;
            case Spinner:
                combat.Apply<SpinnerPower>(owner, card.DynamicVars["SpinnerPower"].IntValue, owner);
                break;
            case SpiritOfAsh:
                combat.Apply<SpiritOfAshPower>(owner, card.DynamicVars["BlockOnExhaust"].IntValue, owner);
                break;
            case Stampede:
                combat.Apply<StampedePower>(owner, card.DynamicVars["Power"].IntValue, owner);
                break;
            case StoneArmor:
                combat.Apply<PlatingPower>(owner, card.DynamicVars["PlatingPower"].IntValue, owner);
                break;
            case Storm:
                combat.Apply<StormPower>(owner, card.DynamicVars["StormPower"].IntValue, owner);
                break;
            case Stratagem:
                combat.Apply<StratagemPower>(owner, 1, owner);
                break;
            case Subroutine:
                combat.Apply<SubroutinePower>(owner, 1, owner);
                break;
            case SwordSage:
                combat.Apply<SwordSagePower>(owner, card.DynamicVars["SwordSagePower"].IntValue, owner);
                break;
            case Terraforming:
                combat.Apply<VigorPower>(owner, card.DynamicVars["VigorPower"].IntValue, owner);
                break;
            case TheSealedThrone:
                combat.Apply<TheSealedThronePower>(owner, 1, owner);
                break;
            case Thunder:
                combat.Apply<ThunderPower>(owner, card.DynamicVars["ThunderPower"].IntValue, owner);
                break;
            case ToolsOfTheTrade:
                combat.Apply<ToolsOfTheTradePower>(owner, 1, owner);
                break;
            case TrashToTreasure:
                combat.Apply<TrashToTreasurePower>(owner, 1, owner);
                break;
            case Tyranny:
                combat.Apply<TyrannyPower>(owner, 1, owner);
                break;
            case Unmovable:
                combat.Apply<UnmovablePower>(owner, 1, owner);
                break;
            case Vicious:
                combat.Apply<ViciousPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case WellLaidPlans:
                combat.Apply<WellLaidPlansPower>(owner, 1, owner);
                break;
        }
    }
}
