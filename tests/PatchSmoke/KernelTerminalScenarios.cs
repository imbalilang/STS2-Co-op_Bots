using CoopBots.Kernel;

/// <summary>
/// P1-01 acceptance: the unified terminal comparator orders endings the way the plan says it
/// must (STS2_Bot_Solver_Implementation_Plan_v1.0 §02, §P1-01).
///
/// These are pure ordering assertions over hand-built records, deliberately: the point is the
/// COMPARATOR's contract, and building them out of real fights would test the fixture at least
/// as much as the ordering. The cases are the ones the plan names — 回血上限、回血时点、
/// 自伤换收益、临时倒地、不重复扣血.
/// </summary>
internal static class KernelTerminalScenarios
{
    internal static void Run()
    {
        VictoryBeatsEverything();
        ACutoffIsNotADefeat();
        FewerLostSeatsWins();
        TheftOutranksPostCombatHp();
        TheReserveCurveIsOffByDefault();
        TheReserveCurveBreaksTiesTheWayItIsMeantTo();
        HealthIsChargedExactlyOnce();
    }

    private static TerminalRecord Win(params int[] hp) =>
        new(true, EvidenceKind.VerifiedTerminal, "", hp, 0, 0);

    private static TerminalRecord Wipe(params int[] hp) =>
        new(false, EvidenceKind.VerifiedTerminal, "", hp, hp.Length, 0);

    private static TerminalRecord Cutoff(string reason, params int[] hp) =>
        new(false, EvidenceKind.EstimatedCutoff, reason, hp, 0, 0);

    private static void Better(TerminalRecord a, TerminalRecord b, string why,
        ObjectiveConfig? config = null) =>
        Check(TerminalComparer.Compare(a, b, config) > 0, why);

    // (1) A victory the simulation produced outranks every non-victory, regardless of HP.
    // Between the two non-victory categories, a cutoff (UNKNOWN) now outranks a verified
    // wipe (a known fact): the 2026-09-22 final boss had 32 WIPE and 23 pending-choice
    // decisions, and the old order made the tournament choose a known losing line over the
    // unmodeled ones. "截断不算团灭" means it must not be scored as a loss — and that includes
    // not losing to one.
    //
    // CHECKED (R2) 2026-09-22: restoring `Tier { Cutoff = 0, VerifiedDefeat = 1 }` turns
    // the third assertion red:
    //   System.Exception: an unfinished line must not be ranked below a verified defeat;
    //   the simulator cannot see the rest of it, so preferring the wipe turns a coverage
    //   hole into a deliberately losing move.
    private static void VictoryBeatsEverything()
    {
        Better(Win(1, 1, 1, 1), Wipe(80, 80, 80, 80),
            "a bare victory must outrank a full-HP wipe.");
        Better(Win(1, 1, 1, 1), Cutoff("step-cap", 99, 99, 99, 99),
            "a victory must outrank a cutoff with perfect HP.");
        Better(Cutoff("step-cap", 80, 80, 80, 80), Wipe(1, 1, 1, 1),
            "an unfinished line must not be ranked below a verified defeat; the simulator "
            + "cannot see the rest of it, so preferring the wipe turns a coverage hole into "
            + "a deliberately losing move.");
        Console.WriteLine("PASS: terminal ranking is victory > cutoff > verified defeat, and no "
            + "amount of remaining HP lets a lesser ending outrank a greater one.");
    }

    // (2) The rule the plan states twice: 截断不算团灭. A cut-off line must not be scored as a
    // loss — it is unknown, and the comparator has to say so rather than quietly treating a
    // budget limit as a death.
    private static void ACutoffIsNotADefeat()
    {
        var cutoff = Cutoff("step-cap", 0, 0, 0, 0);
        Check(!cutoff.Victory, "a cutoff must never report a victory.");
        Check(cutoff.Kind == EvidenceKind.EstimatedCutoff, "a cutoff must be labelled as one.");
        Check(cutoff.CutoffReason == "step-cap", "a cutoff must carry its reason.");
        Check(cutoff.IrreversibleLoss == 0,
            "an unfinished fight has UNKNOWN losses; an all-zero HP array must not be reported "
            + "as four irreversibly lost seats. 截断不算团灭.");
        Console.WriteLine("PASS: a cutoff is labelled with its reason, reports no victory and is "
            + "never counted as a party wipe.");
    }

    // (3) The middle key: between two outcomes of the same rank, losing fewer seats wins even
    // when the survivor is at lower HP. This is what "不可逆损失" is for — the plan is explicit
    // that HP totals must not be allowed to hide a body.
    private static void FewerLostSeatsWins()
    {
        var oneLost = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [1, 1, 1, 0], 1, 0);
        var noneLost = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [1, 1, 1, 1], 0, 0);
        Better(noneLost, oneLost, "losing no seat must outrank losing one, HP being equal.");
        Better(oneLost, new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [60, 60, 60, 0], 2, 0),
            "losing one seat must outrank losing two, even at much lower surviving HP.");
        Console.WriteLine("PASS: irreversible loss is compared before resources, so a line that "
            + "costs a seat loses to one that does not even when it leaves more HP behind.");
    }

    // (3b) Unrecovered stolen cards/gold are a permanent loss too. The live report was a bot
    // that out-blocked a Thieving Hopper, ended at higher HP, and lost a key card; HP must not
    // hide that any more than it hides a body.
    // CHECKED (R2) 2026-09-22: deleting the OutstandingStolenResource comparison turns the
    // first assertion red, verbatim:
    //   System.Exception: a line that recovers the stolen cards must beat a higher-HP line
    //   that lets the thief escape.
    private static void TheftOutranksPostCombatHp()
    {
        var clean = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [1, 1, 1, 1], 0, 0, 0);
        var stolen = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [80, 80, 80, 80], 0, 0, 2);
        Better(clean, stolen,
            "a line that recovers the stolen cards must beat a higher-HP line that lets the thief escape.");
        var mostlyClean = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [1, 1, 1, 1], 0, 0, 1);
        Better(clean, mostlyClean, "fewer outstanding stolen resources must win at equal HP.");
        Console.WriteLine("PASS: unrecovered theft is compared before HP, so a high-HP escape "
            + "does not hide a key card lost from the deck.");
    }

    // (4) Switching comparator must not silently change what the bot optimises for. With the
    // default configuration the resource term is exactly the team's net post-fight HP, which
    // is the objective the project already has. If this ever stops holding, the swap becomes
    // a stealth objective change.
    private static void TheReserveCurveIsOffByDefault()
    {
        var record = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [10, 20, 30, 40], 0, 0);
        Check(Math.Abs(TerminalComparer.ResourceUtility(record) - 100) < 1e-9,
            "the default objective must be exactly the sum of post-fight HP; got "
            + $"{TerminalComparer.ResourceUtility(record)} for [10,20,30,40].");
        var withConsumables = record with { ResourcesConsumed = 3 };
        Check(Math.Abs(TerminalComparer.ResourceUtility(withConsumables) - 100) < 1e-9,
            "consumables are free by default: the plan records them separately until their "
            + "weight is decided, so they must not move the default score.");
        Console.WriteLine("PASS: the default objective is exactly team net post-fight HP, and "
            + "consumables are recorded but unpriced until their weight is chosen.");
    }

    // (5) The optional reserve curve. This is the case the plan warns about: a total-loss
    // comparison prefers the first line (more total HP) while leaving one seat nearly dead,
    // which is exactly the pattern "总损失下降掩盖某个角色反复承担风险".
    private static void TheReserveCurveBreaksTiesTheWayItIsMeantTo()
    {
        // The arithmetic, so the next reader can check it rather than trust it. Floor 30,
        // penalty b, u = sum(hp) - b*sum(shortfall^2)/30 when a seat is under the floor:
        //   spread [5,40,40,40]: total 125, one shortfall of 25 -> 125 - b*625/30
        //   even   [25,25,25,25]: total 100, four shortfalls of 5 -> 100 - b*100/30
        // spread wins while 25 > b*(625-100)/30, i.e. while b < 1.43.
        // The squared term is why: ONE deep shortfall is punished far harder than several
        // shallow ones, which is the shape the plan asks for ("低于该血线的惩罚强度").
        // b = 1 is NOT enough to flip this case — measured, by this assertion going red.
        var floor = new[] { 30, 30, 30, 30 };
        var spread = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [5, 40, 40, 40], 0, 0);
        var even = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [25, 25, 25, 25], 0, 0);
        var config = new ObjectiveConfig(floor, ReservePenalty: 2);
        // Without the curve the "spread" line wins on raw total (125 vs 100).
        Better(spread, even, "sanity: without the reserve curve the higher total wins.");
        // With b = 2: spread 125 - 41.7 = 83.3, even 100 - 6.7 = 93.3.
        Better(even, spread,
            "with a reserve floor the even line must win: a seat left at 5 HP against a floor "
            + "of 30 is the risk-hiding pattern the plan calls out.", config);
        Console.WriteLine("PASS: with a reserve floor enabled, a line that keeps every seat above "
            + "the line beats one with a higher total that leaves a seat nearly dead.");
    }

    // (6) No double counting (§07): the plan forbids charging prefix HP loss and then charging
    // the same HP again in the final total. Structurally that holds here because loss is
    // counted in SEATS and resources in HP — a dead seat contributes 0 to the HP sum and 1 to
    // the loss count, and neither is charged twice.
    private static void HealthIsChargedExactlyOnce()
    {
        var dead = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", [0, 50], 1, 0);
        Check(Math.Abs(TerminalComparer.ResourceUtility(dead) - 50) < 1e-9,
            "a dead seat must contribute exactly its own zero HP and no extra deduction; got "
            + $"{TerminalComparer.ResourceUtility(dead)} for [0,50].");
        Check(dead.IrreversibleLoss == 1, "the dead seat must be counted once in the loss key.");
        Console.WriteLine("PASS: HP is charged exactly once — a dead seat contributes zero HP to "
            + "the resource key and one seat to the loss key, and is never charged twice.");
    }

    // NOT ASSERTED HERE, DELIBERATELY.
    //
    // The comparator is wired into KernelTeamSearch's terminal selection (KernelTeamSearch.cs,
    // "TERMINALS ARE RANKED BY THE OBJECTIVE"), and that swap is NOT pinned by any assertion.
    // A probe proved it: restoring the old `node.Score > bestTerminal.Score` ranking left every
    // assertion in the whole suite green, so nothing can tell the two implementations apart.
    //
    // A distinguishing case needs TWO verified terminals whose tactical Score and objective
    // disagree. Within victories the Score is `VictoryBonus - tactical`, and tactical already
    // prefers surviving HP, so the two agree in every configuration tried. That case is
    // therefore UNVERIFIED rather than verified-by-omission — written down instead of dressing
    // a green-but-identical check up as coverage (R2, R5b).
    //
    // The property the swap preserves — "a reachable kill is committed, not a round-crossing
    // line" — IS guarded, and more strongly, by KernelRoundScenarios.cs:163, which pins the
    // chosen ACTION rather than merely the outcome. No duplicate lives here.

    private static void Check(bool condition, string failure)
    { if (!condition) throw new Exception(failure); }
}
