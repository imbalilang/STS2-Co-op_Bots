namespace CoopBots.Kernel.Vendor;

internal static class SearchTransitionGuard
{
    public static TResult Execute<TResult>(
        PlanAction action,
        StateFingerprint parentState,
        int parentActionCount,
        Func<TResult> transition)
    {
        try
        {
            return transition();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidPlannedChoiceBranchException)
        {
            throw;
        }
        catch (SearchTransitionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SearchTransitionException(
                action,
                parentState,
                parentActionCount,
                ex);
        }
    }
}
