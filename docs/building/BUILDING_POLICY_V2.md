# Building Policy v2 — Core Phase

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


Status: core implementation complete, owner review items 1–4 fixed, focused
regression green on the revision, waiting for owner acceptance. This phase
changes only the permanent-deck building policy; shop, rest and team
coordination are untouched.

## Scope and files

| Area | File | Change |
| --- | --- | --- |
| Pick / skip | `src/CoopBots/Building/BuildValue.cs` | Continuous bounded size relief; affinity decoupled from size; no generic Power quota |
| Mechanisms | `src/CoopBots/Building/DeckMechanisms.cs` (new) | Explicit supported / unsupported / unknown dependencies from `BakedResources` |
| Structure | `src/CoopBots/Building/DeckStructure.cs` (new) | Per-decision curve / balance / redundancy facts and bounded adjustments |
| Upgrade | `src/CoopBots/Building/CardProfile.cs`, `BuildValue.cs` | `HpLossDelta`, contextual cost value, signed keyword transitions, contextual Exhaust |
| Entry gates | `tests/PatchSmoke/Program.cs` | `--building-policy-only`; new scenarios also run in the normal suite |
| Tests | `tests/PatchSmoke/BuildingPolicyScenarios.cs` (new), `BuildValueScenarios.cs` | Focused policy probes; two intended expectation changes (below) |
| Verification | `scripts/verify-building-policy.ps1` (new) | Isolated Release build + both focused runs + hashes |

## 1. Pick / skip

- `Add` no longer contains a binary `onPlan` switch. The size penalty is scaled
  by a continuous relief in `[0,1]` (`relief:` token in the reason):
  - **functional deficit** — the deck is short of block / damage / AOE / draw /
    energy, measured as a share of the comfortable count (same thresholds as
    `needs-*`);
  - **mechanism-supported fit** — `DeckMechanisms.Assess` returns `Supported`.
- Community affinity is still a `<= 6` additive prior but no longer contributes
  to the size penalty. A generic mined route likewise gives no relief.
- The automatic `needs-power` quota ("fewer than three Powers") is removed: it is
  not a deficit in every build and was promoting support-free payoffs. Powers
  are still valued through their roles and mechanisms.
- `Skip` keeps its real `0` candidate semantics and forced selection is
  unchanged.

## 2. Mechanisms (`DeckMechanisms`)

Co-draft lift is no longer read as a missing trigger. Dependencies come from
`BakedResources` and `CardProfile`:

- Shiv amplifier (`Accuracy`, `PhantomBlades`) → own Shiv producers; a card that
  is both producer and amplifier (`FanOfKnives`) is self-contained.
- `Rupture` → HP-loss sources. Rupture is a Strength producer too, but the
  explicit HP-loss dependency wins (it is recorded as a converter of `hp-loss`).
- Ordinary direct Strength → own attacks.
- Focus → own orb infrastructure; Dexterity (`Footwork`) → own block (`>= 3`, the
  previous calibration).
- Poison / Doom amplifier / Orb replayer (e.g. `Accelerant`, `NoEscape`,
  `Voltaic`) → own producers of that resource.
- Anything unclassified is `Unknown` and **not** penalised. A `scaling-supported:`
  / `scaling-unsupported:` token names the resource.
- A payoff that is not itself a producer never discounts a real producer. Only
  the candidate instance can be its own trigger, and only when the candidate can
  actually produce the resource, so two `Accuracy` in a deck with one `Blade
  Dance` counts exactly one trigger instead of negative one.
- Producer counts come from the useful supply only: curses, statuses and cards
  whose profile cannot be read are excluded, so they are never mistaken for a
  trigger.
- Deck context always comes from the passed deck / `deckOverride`; no live
  community query and no direct `player.Deck` inside the mechanism check.

**Affinity direction fix.** The feed's document for a held card B recommends the
offered card A. The old `Affinity` looked up the candidate A's table and searched
the deck for partners — the reverse arrow. It now iterates the held deck cards and
checks whether the candidate is among their recommendations, keeping the two
strongest positive lifts, capped at `6`.

## 3. Structure (`DeckStructure`)

One O(n) summary per decision (no cross-decision cache, no id-only cache):

- Cost curve + stable energy supply → `bottleneck:energy-*` for expensive cards
  in a heavy, unaccelerated deck. Pressure is `(average useful cost - supply per
  drawn card) / 1.5`, bounded to `[0,1]`. Supply is the character's persistent
  `Player.MaxEnergy` (any positive value, else the baseline `3`) plus a capped
  `0.6` credit per energy-role card. More supply can only lower pressure; a
  single generator cannot erase a heavy curve and a cheap deck with a generator
  is never read as more pressed than one without.
- `draw-unaffordable` when the deck already draws but cannot pay for its plays.
- `too-much-setup` / `needs-output` when setup outnumbers immediate output.
- Duplicate mechanism payoff: the penalty factor depends on triggers vs copies
  (`dup-payoff:*`), so an extra copy past its triggers is weaker than one with
  triggers to spare.
- Curses / statuses / unplayable and cards whose profile cannot be read are
  excluded from the useful supply, from `IdCounts` and from the average-cost
  denominator; unknown is neutral, never a defect. Draw is read from
  `Facts.Draw` (whitelisted by the analyzer), never from raw `CardsVar`.
- Total adjustment is clamped to `±6`. Comments describe this as a bounded
  heuristic that can move a ranking or cross the skip threshold, with no claim
  to a precise strength or win rate — not as something that can only break ties.

## 4. Upgrade

- Cost reduction value is bounded contextual (`8..16` per point) from energy
  pressure: worth more in a slow, unaccelerated curve, worth less when energy is
  already covered. It never goes negative for a reduction.
- `UpgradeDiff` now carries `HpLossDelta`; a lower HP cost is a gain, a higher
  one a cost (`hp-cost*`).
- Keyword transitions are signed: `Innate`/`Retain` beneficial, `Ethereal`/
  `Unplayable` harmful, anything else neutral. `Exhaust` is contextual: losing it
  pays only when the card actually has an effect worth replaying (so Hologram is
  positive and junk is neutral). The `±4` term skips `Exhaust` to avoid double
  counting.
- Upgrades are still measured by cloning the real card through the game's own
  upgrade path; the live card and deck are never mutated. `HumanCoopAdvisor`
  continues to delegate to `BuildValue.UpgradeDelta`.

## Owner review revision (core)

The owner's first review found four confirmed defects; all were fixed in the same
core files, and the per-decision summary reuse in `Add`/`Marginal` was kept:

1. **Mechanism producer subtraction.** `Assess` no longer subtracts the
   candidate's id count from the producer total. It subtracts at most one
   instance, and only when the candidate is genuinely a producer of that resource
   (self-contained payoffs such as `FanOfKnives`). Two `Accuracy` with one
   `BladeDance` now counts one trigger; removing the last producer makes the
   payoff unsupported.
2. **Energy pressure.** The flat `0.3` for any deck with an energy card is gone.
   Pressure is a bounded continuous function of average useful cost versus stable
   supply; adding a generator or raising `MaxEnergy` can only lower it.
   Any positive `Player.MaxEnergy` is respected, including high-energy custom
   characters. Ordinary-energy test fixtures explicitly set `3`; production
   logic does not clamp the debug character's `100` to satisfy a test.
3. **Summary supply.** `IdCounts` and the average-cost denominator are built only
   from playable, successfully profiled cards. Curses/statuses and unreadable
   cards are counted separately and never treated as negative evidence or as
   producers.
4. **Comments.** The "can only break ties, never decide" claims were replaced
   with an explicit bounded-heuristic, no-precise-strength/win-rate statement.

`Add`, `Marginal`, `Deck`, `Remove` and `UpgradeDelta` all pass the stable
`MaxEnergy` into the shared summary, so the same context is reused rather than
rebuilt per candidate.

## Intended test expectation changes

Two existing assertions encoded the old (rejected) behaviour and were updated:

1. `BuildValueScenarios` affinity block now asserts a held card's recommendation
   earns the prior but does **not** waive the size penalty (was `size:12.0`, now
   both decks `size:30.0` with no `relief:`).
2. The affinity direction probe now adds the *context* card and checks the
   *offered* card fires (`DEADLY_POISON` activates `OUTBREAK`), instead of the
   reverse. All other existing assertions are preserved and pass.

## Verification

`scripts/verify-building-policy.ps1` (PowerShell 5.1, SHA fallback) builds
`tests/PatchSmoke` Release into a fresh `work/building-policy-v2/<UTC>/build` and
runs `--building-policy-only` then `--building-edits-only`. It records exit codes,
logs, elapsed times and SHA256 of source, game and output assemblies, and only
reads the owner's `work/building-policy-v2/before` snapshot. It does not deploy,
package or touch shop / rest.

## Known limitations

- Heuristics only: no solver, simulation or learned model. The terms can tip a
  close call, not establish precise combat strength.
- The mechanism table is intentionally narrow; anything it does not classify is
  left neutral rather than guessed.
- Full-suite and live multiplayer acceptance remain the owner's step.
