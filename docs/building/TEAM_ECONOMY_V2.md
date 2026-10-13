# Team economy v2

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


Second phase of the authorized non-training optimization areas: team draft
coordination, shop purchases, and rest-site removal/camp decisions. Bounded
heuristics only — no solver, no simulation, no learned model, no network schema
change.

## 1. Combat-start team snapshot (`Building/TeamBuildingContext.cs`)

Rewards resolve concurrently on every peer (`RewardPatches.BotRemoteChoicePatch`
computes locally with no host authority), so scoring one player's reward must
never read a teammate's mutable deck. A prefix on
`CombatManager.SetUpCombat(CombatState)` captures every player's **permanent**
`Player.Deck` before the engine calls `PopulateCombatState` and before any
temporary combat pile exists.

- Storage is a `ConditionalWeakTable` keyed by the `IRunState` reference and
  versioned by `CurrentActIndex`, `TotalFloor` and `CurrentRoomCount`.
- A read that does not match the current location (new room, resumed or
  reconnected run, shop, unknown map) is unknown. The caller falls back to an
  own-deck/roster-only bonus and **never** rereads a teammate deck.
- A rejoin or a save load poisons the current act/floor on every peer. The
  verified native paths are `RunManager.GetRejoinMessage` (host, postfix),
  `RunManager.InitializeSavedRun` (loading/rejoining client, postfix) and
  `RunLobby.HandlePlayerRejoinedMessage` (existing clients, **prefix**: the
  native handler invokes the `PlayerRejoined` listeners, which can resume
  actions, so the poison must land before that body runs). The poison drops
  the capture and refuses to recapture at that act/floor, so a saved combat's
  own `SetUpCombat` cannot rebuild a snapshot the other peers never shared. It
  clears only when the run reaches a later act/floor. No new message or save
  field is introduced.
- The stored value is plain counts and card ids (`DeckStructure.Summary` plus
  support/debuff counts), never live card references.
- Capture is synchronous, bounded, and free of IO, RNG and network; any failure
  clears the entry and fails harmlessly. There is no save/network schema change.

## 2. Bounded team bonus (`TeamCoordinator.TeamBonus`)

`TeamBonus` now uses actual established structure instead of a fixed lowest-id
aux role.

- Ally support is priced only when the card has a modelled effect (GainsBlock,
  draw, energy, scaling, poison/doom, or a debuff). An unmodelled `AnyAlly`
  power is neutral (`0`, the CONCOCT regression).
- Demand and coverage come from the snapshot: ally attack/block/draw/energy
  shortages for support, and per-channel vulnerable/weak/strengthdown coverage
  for debuffs. Weak coverage is not Vulnerable coverage, and each channel's own
  saturation drives its falloff. Only enemy-targeted debuffs earn credit; a
  self/ally Weak or Vulnerable drawback is not a shared benefit.
- Redundancy is divided down (existing party support and duplicate copies), and a
  weak own deck does not stack pure support it cannot cash while keeping a
  minimum own offence/defence.
- The bonus is bounded absolutely (`MaxTeamBonus`) **and** relatively: in the
  `BuildValue` path it may add at most a modest share (`TeamBonusBuildShare`) of
  the card's pre-team useful contribution, so a flat team term cannot carry a
  card the deck cannot use. The 1.5x share used by `HumanCoopAdvisor.CardValue`
  is deliberately not copied into the raw `BuildValue` score, which uses a
  different scale. `BuildValue` passes its own hypothetical deck and summary so a
  candidate is judged against the hypothetical own context, never a teammate's
  in-flight edit.

## 3. Bounded two-purchase shop comparison (`Building/PurchaseComparison.cs`)

The existing `GoldPerDeckValue = 2.2` and threshold/reserve/affordability scales
are unchanged. `BotShopPlanner.Choose` additionally compares the best single
with a feasible pair of two affordable, individually worthwhile offers:

- The pair is only a comparison; only the **first** action is returned and the
  existing driver replans after the real purchase. A follow-up `Choose` after a
  projected first transaction must take the second item. No commitments are
  queued and no network message changes.
- The second item is priced against a hypothetical deck/gold/potion state the
  first would leave (second card via `BuildValue.Add(..., deckOverride)`, one
  removal via the shared plan, a potion at the projected potion count). Gold,
  deck, inventory and potion slots are never mutated. The lost MawBank income is
  charged at most once per plan, matching the native relic: `AfterItemPurchased`
  ignores a zero-gold spend, so a **free** first item leaves the bank active and
  the pair's first *paid* item is the one charged the 24. Both paid charges once,
  both free charges none.
- The second action is re-checked against the existing single-item gates
  (affordability, item threshold, reserve, potion capacity), so a pair the
  driver itself would refuse cannot win on combined arithmetic.
- A pair is conservatively rejected when it contains **any** relic (its side
  effects on deck, prices or capacity cannot be modelled; the variable-price
  ones would also assume a discount), two removals, more potions than remaining
  slots, or a total above the purse. Relics — including a single
  `MembershipCard` — keep their existing single valuations.
- `BestRemoval` skips non-removable cards, so a zero invalid index can never
  beat a negative valid one.
- The stock considered is capped at a fixed small prefix (`MaxConsidered = 16`),
  deterministic by existing entry index.

## 4. Shared removal plan and reachable risk (`RestSitePatches.cs`, `Building/RemovalPlan.cs`)

`RemovalPlan.Choose` is the one pure, deterministic ordering used by both
`BotBrain.RemoveFromDeck` and the rest-site `CookValue`, so the removal Cook is
valued for is the removal it would make. Forced count, fallback, protection,
live-deck immutability, tie-breaks and logging are preserved.

- The removal term is signed: removing strong cards is a real cost and can pull
  the choice below the +5 max HP baseline (worth 10), which floors the whole
  choice at zero. Only the upper bound is capped.
- A required step onto a protected card is the planner refusing, not a
  zero-cost removal, so Cook declines outright instead of claiming a free +10.
  An evaluable plan keeps the ordinary baseline.
- `UpcomingRisk` now uses the documented conservative maximum over all
  reachable children, because child order is presentation order, not travel
  order. The last-camp-before-boss guard and the neutral unknown map are kept.
- Each card's upgrade is scored once per camp instead of twice.

## Verification

`scripts/verify-team-economy.ps1` builds `tests/PatchSmoke` and runs
`--team-economy-only` (the new scenarios plus the existing BuildValue /
BuildingPolicy / Shop / Coordinator regressions). It records exit codes, logs,
timings, source and assembly SHA256 under `work/building-team-economy/<UTC>/`.
The focused scenarios cover: snapshot immutability, location/room versioning,
separate-peer rejoin/load poison and later-floor resume, stable reward order,
per-channel debuff separation, bounded unknown support, hypothetical own decks,
the two-item shop pair and its sequential follow-up, the MawBank free-first
charge (active bank vs no bank via real `ChooseDetailed`), second-action gates,
pair feasibility, shared removal plan, signed/protected Cook value and
reachable-risk order invariance. It also emits (documentation-only) elapsed
milliseconds for one reward decision for each of **three real bots** with their
own 25-card decks, and for a 16-offer pair shop on a 25-card damage-deficit deck
(20 Defend + 5 Injury, gold 2000) that is asserted to select a pair. The normal
`Program.cs` tail runs `BuildValueScenarios`, `BuildingPolicyScenarios` and
`TeamEconomyScenarios`; the focused scripts remain the only automated gate here.
No win-rate, battle or live-network claim is made; full-suite and live
multiplayer acceptance remain the owner's step.

## Owner acceptance — 2026-09-18

Astra reviewed and accepted both building-policy-v2 and building-team-economy after the final corrections. DeepSeek implemented the changes and ran focused regressions; the owner ran the full project gate on the final source.

- Full gate: `work/verification/20260918T135710885Z/summary.json`, status pass. Build, type-load check, ordinary PatchSmoke and kernel PatchSmoke all exited 0. Their 149/229 PASS lines overlap and are not unique test counts.
- Focused gate: `work/building-team-economy/20260918T135542.518843Z/verify-summary.json`. Source hashes matched at acceptance. Default tests now include both new scenario suites.
- Independent acceptance record: `work/building-team-economy/owner-acceptance.json`; all 14 C# files from the previous accepted kernel sync also remain unchanged.
- Warmed headless samples: three separate bots with 25-card decks took 27–32.4 ms total for three rewards; a 16-offer, active pair-search shop fixture took 295–395.2 ms. These are sampled local costs, not a game latency guarantee.
- One unknown-profile error probe was skipped because no real card reproduced a profiling exception. That exceptional branch remains dynamically unverified; playable/unplayable supply tests did run.
- No live multiplayer GUI session, win-rate validation, training, deployment or commit was performed. The source implementation and offline regression gate are accepted; live in-game acceptance remains separate.
