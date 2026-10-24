using MegaCrit.Sts2.Core.GameActions;

namespace CoopBots;

// An action's owner identifies the seat, not whether the player clicked.
// Native ReadyToBeginEnemyTurnAction is an automatic protocol handshake after
// EndPlayerTurnAction (see its native class contract); it must not retire the
// future menu that the ending action just installed. Keep all other actions,
// including unknown mod actions and human hook choices, conservative.
internal static class ExternalCombatInput
{
    internal static bool IsHumanAction(GameAction action)
        => !AutoPilot.Drives(action.OwnerId)
            && action.GetType() != typeof(ReadyToBeginEnemyTurnAction);
}
