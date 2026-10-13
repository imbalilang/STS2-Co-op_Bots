using System.Text.Json;

namespace CoopBots.Building;

// Upstream's battle solver does not calibrate our cheap construction estimates.
// Explicit offline callers supply independent native witnesses; rewards never
// invoke this runner. Evidence carries units and provenance, never win probability.
// Owner's staged architecture plan, 2026-10-09; default policy weights are unchanged.
internal static class ConstructionNativeCalibration
{
    internal sealed record Budget(int MaxCandidates, int SamplesPerCandidate,
        int MaxNativeActions, double SoftMilliseconds);
    internal sealed record Scenario(string Key, string GameMvid, string Seed, int PlayerCount,
        double Energy, double Incoming, double EnemyHp, string Boundary);
    internal sealed record Proxy(double Damage, double Block, double Draw, double Exhaust,
        double TemporaryStrength, double TemporaryDexterity, double TemporaryFocus,
        double Score, string ScoreUnits);
    internal sealed record Request(string CandidateKey, bool Important, int Sample,
        Scenario Scenario, Proxy Proxy, ConstructionMechanismFacts.Facts Facts);
    internal sealed record Native(Scenario Scenario, double Damage, double Block, double HpLoss,
        double CardsDrawn, double CardsExhausted, double StrengthDuring, double DexterityDuring,
        double FocusDuring, double StrengthAfter, double DexterityAfter, double FocusAfter,
        bool Completed, string Boundary, string RngBefore, string RngAfter);
    internal sealed record Differences(double Damage, double Block, double Draw, double Exhaust,
        double TemporaryStrength, double TemporaryDexterity, double TemporaryFocus);
    internal sealed record Evidence(Request Request, Native? Native, Differences? NativeMinusProxy,
        int NativeActions, string Status)
    {
        internal bool Complete => Status == "complete";
        internal string JsonLine() => JsonSerializer.Serialize(this);
    }

    // The contract bounds commanded native actions and checks time BETWEEN them.
    // One atomic native command may overrun the soft deadline or execute hooks;
    // this is not a claim that every internal hook is separately metered.
    internal sealed class NativeScope
    {
        private readonly Session session;
        internal NativeScope(Session session) => this.session = session;
        internal int Actions { get; private set; }
        internal bool Exhausted { get; private set; }
        internal void Execute(Action nativeCommand)
        {
            if (!session.CanExecute)
            { Exhausted = true; throw new BudgetExhaustedException(); }
            session.Actions++;
            Actions++;
            nativeCommand();
        }
    }
    internal sealed class Session
    {
        private readonly Budget budget;
        private readonly TimeProvider clock;
        private readonly long start;
        private readonly Dictionary<string, int> samples = new(StringComparer.Ordinal);
        private readonly HashSet<string> candidates = new(StringComparer.Ordinal);
        private readonly HashSet<(string Candidate, string Scenario, int Sample)> identities = [];
        internal int Actions { get; set; }
        internal Session(Budget budget, TimeProvider? clock = null)
        {
            if (budget.MaxCandidates < 0 || budget.SamplesPerCandidate < 0 || budget.MaxNativeActions < 0
                || !double.IsFinite(budget.SoftMilliseconds) || budget.SoftMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(budget));
            this.budget = budget; this.clock = clock ?? TimeProvider.System;
            start = this.clock.GetTimestamp();
        }
        internal bool CanExecute => Actions < budget.MaxNativeActions
            && clock.GetElapsedTime(start, clock.GetTimestamp()).TotalMilliseconds < budget.SoftMilliseconds;
        internal Evidence Observe(Request request, Func<NativeScope, Native> independentNative)
        {
            if (request.Scenario.GameMvid != request.Facts.Source.GameMvid)
                return new(request, null, null, 0, "source-mismatch");
            var identity = (request.CandidateKey, request.Scenario.Key, request.Sample);
            if (identities.Contains(identity)) return new(request, null, null, 0, "duplicate");
            if (!CanExecute || samples.GetValueOrDefault(request.CandidateKey) >= budget.SamplesPerCandidate
                || request.Sample < 0 || request.Sample >= budget.SamplesPerCandidate
                || !candidates.Contains(request.CandidateKey) && candidates.Count >= budget.MaxCandidates)
                return new(request, null, null, 0, "not-funded");
            identities.Add(identity);
            candidates.Add(request.CandidateKey);
            samples[request.CandidateKey] = samples.GetValueOrDefault(request.CandidateKey) + 1;
            var scope = new NativeScope(this);
            Native native;
            try { native = independentNative(scope); }
            catch (BudgetExhaustedException) { return new(request, null, null, scope.Actions, "budget-exhausted"); }
            if (native.Scenario != request.Scenario)
                return new(request, native, null, scope.Actions, "scenario-mismatch");
            if (!native.Completed || scope.Exhausted || scope.Actions == 0
                || !string.Equals(native.Boundary, request.Scenario.Boundary, StringComparison.Ordinal))
                return new(request, native, null, scope.Actions, "incomplete");
            var proxy = request.Proxy;
            return new(request, native,
                new(native.Damage - proxy.Damage, native.Block - proxy.Block,
                    native.CardsDrawn - proxy.Draw, native.CardsExhausted - proxy.Exhaust,
                    native.StrengthDuring - native.StrengthAfter - proxy.TemporaryStrength,
                    native.DexterityDuring - native.DexterityAfter - proxy.TemporaryDexterity,
                    native.FocusDuring - native.FocusAfter - proxy.TemporaryFocus), scope.Actions, "complete");
        }
    }
    internal static IReadOnlyList<Evidence> Collect(IEnumerable<Request> requests, Budget budget,
        Func<Request, NativeScope, Native> independentNative, TimeProvider? clock = null)
    {
        var session = new Session(budget, clock);
        return requests.OrderByDescending(request => request.Important)
            .Select(request => session.Observe(request, scope => independentNative(request, scope))).ToArray();
    }
    private sealed class BudgetExhaustedException : Exception { }
}
