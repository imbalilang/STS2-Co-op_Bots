# CoopBots third-party attribution

## Spire Codex — mined archetype clusters, card affinity and event option ids

As of preview.62, CoopBots ships three tables generated from Spire Codex:
`src/CoopBots/Building/BakedArchetypes.cs` (archetype clusters mined from real
runs), `src/CoopBots/Building/BakedCardAffinity.cs` (card-to-card draft affinity
mined from real picks) and `src/CoopBots/Building/BakedEvents.cs` (the option
ids each event can show). The project owner confirmed on 2026-09-13 that
permission had been obtained for this use. This records that confirmation and
the upstream terms; it does not purport to grant rights to downstream projects.

- Source data: `https://spire-codex.com/api/runs/archetypes`
  (snapshot built 2026-08-22, game version v0.111.0)
- Source data: `https://spire-codex.com/api/draft-recs/cards/{id}`, fetched by
  `scripts/fetch-spire-codex-draft-recs.py` under its published rate limits
- Source data: `https://spire-codex.com/api/events`
- Generators: `scripts/import-spire-codex-archetypes.py`,
  `scripts/import-spire-codex-affinity.py`, `scripts/import-spire-codex-events.py`
- Project: Spire Codex, https://spire-codex.com
- Copyright: © 2025-present Peter Lord and Spire Codex contributors

Required Notice: Copyright © 2025-present Peter Lord and Spire Codex contributors.

Licensing and terms that apply to that data:

- Spire Codex source and data are licensed under the **PolyForm Noncommercial
  License 1.0.0**, https://polyformproject.org/licenses/noncommercial/1.0.0.
  Copies of that license text and the Required Notice above must accompany any
  distribution containing this data. CoopBots is a free, non-commercial mod.
- The Spire Codex API terms (https://github.com/ptrlrd/spire-codex/blob/main/API_TERMS.md)
  make the hosted API free for community use within rate limits, with
  attribution encouraged. CoopBots links to https://spire-codex.com for that
  purpose.
- Slay the Spire 2 card, relic, monster, potion, event and other game data
  belongs to **Mega Crit Games**. Spire Codex serves it as a community reference
  under fair use / educational terms. CoopBots consumes only derived cluster
  signatures (card and relic identifiers and names), does not redistribute game
  assets, and does not recompile, repackage or redistribute the game.

Only the cluster signature — defining card and relic identifiers, the cluster
name, and its sample size — is baked. The feed's reported win rate is
deliberately not carried over because its scale is not consistent between
clusters. For affinity, only card identifiers and their lift are baked, and only
for pairs that clear the sample floors recorded in the generated file
(`MinOffers` / `MinPicks`); the reported pick win rate is not carried over
because it is confounded by who picks what. For the event table only option
*ids* are baked: titles and descriptions are localized text, and no decision may
depend on parsing them. No Spire Codex source code is copied; the generators
read the published JSON only.

CoopBots preview.21 adapts selected card and relic simulation algorithms from CombatSolver 0.33.9 and, as of preview.21, ships the imported Engine, Prediction and Search source trees with selected runtime support as the `CoopBots.Kernel.dll` component. The project owner confirmed on 2026-09-12 that permission had been obtained for this work. This records that confirmation; it does not purport to grant rights to downstream projects.

CombatSolver original work: copyright Torch and respective contributors.
Random Foreseer-derived portions: copyright hotwords123.
Random Foreseer: https://github.com/hotwords123/StS2.RandomForeseer

The released preview.21 embeds the upstream simulation source under the CoopBots.Kernel.Vendor namespace, without the original mod startup, UI or automation. It is a selective source port into CoopBots' multiplayer projection. Neither port loads CombatSolver / Random Foreseer assemblies. Ported behavior, source locations, adaptations and limitations are documented in outputs/port-preview20.md and src/CoopBots.Kernel/INTEGRATION.md in the source workspace.

0.36.0 additionally ports Random Foreseer's **out-of-combat** prediction layer (source tree `RandomForeseerCode/OutOfCombat`, `.../Common`, version 0.13.14) into `src/CoopBots.Kernel/Vendor/OutOfCombat`, under the same namespace scheme and for the same reason: an AI teammate can only act on a random outcome it can name. The port keeps the prediction logic and the data-only hover-tip model wrappers, and discards the upstream UI layer entirely (Harmony patches, Godot nodes and scenes, settings pages, localization tables and telemetry), replacing them with small stubs in `Data/`, `Telemetry/`, `Utils/` and `Localization/`. Two local adaptations exist: the hover-tip factory records the predicted relic/potion model in a side table (upstream nulls `CanonicalModel` to avoid marking models as discovered, which also discards the identity), and the event entry point takes the `EventModel` as a parameter instead of resolving it through a UI patch. The upstream fairness gate is deliberately not reproduced — a bot has no save to reload — which is recorded in the stub and in outputs/randomforeseer-out-of-combat-plan.md.

Version note: the 0.13.14 checkout used for this port carries an MIT License (Copyright (c) 2026 hotwords123). That is later than the 2026-08-28 permission recorded below, which states that no public license existed at the time. Nothing here is legal advice; both records are kept so the history is not silently rewritten.

The original CombatSolver notice follows unchanged. Its permission grant refers to Combat Solver; the CoopBots port proceeds on the separately confirmed permission above.

---
# Third-Party Notices

Last updated: 2026-08-28

## Random Foreseer

Combat Solver 的内置战斗模拟核心使用并改造了 Random Foreseer 的部分实现，现已获得原作者的许可。

Combat Solver's built-in combat simulation core uses and modifies portions of the Random Foreseer implementation with permission from its original author.

- Project: Random Foreseer
- Author and copyright holder: hotwords123
- GitHub: https://github.com/hotwords123/StS2.RandomForeseer
- Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3747531952

### Permission Grant

On 2026-08-28, hotwords123 granted Combat Solver a non-exclusive, royalty-free, perpetual permission covering the Random Foreseer code for which hotwords123 owns the copyright. The permission allows Combat Solver to use, copy, modify, and redistribute that code, subject to all of the following conditions:

1. Retain attribution to hotwords123.
2. Retain a link to Random Foreseer.
3. Accurately describe the source relationship between Combat Solver and Random Foreseer.
4. Include a LICENSE or NOTICE file containing the above information with every binary distribution.

This file records that permission and is the NOTICE required by its conditions. It must be retained in source distributions and included with every Combat Solver binary distribution.

## Scope and Ownership

Random Foreseer code and the portions of Combat Solver derived from it remain subject to hotwords123's copyright and the permission above. Combat Solver's modifications, search system, deployment system, user interface, diagnostics, tests, documentation, and other original work are copyright Torch and their respective contributors.

Combat Solver does not load or distribute the Random Foreseer assembly as a runtime dependency. This runtime separation does not alter the source relationship described above.

At the time this permission was granted, the Random Foreseer repository did not contain a public software license. The permission recorded here does not place Random Foreseer-derived code under any separate license that may apply to Torch-authored portions of Combat Solver. If Random Foreseer later publishes a license, this notice will be updated to record the resulting licensing arrangement without removing the attribution and source history above.

