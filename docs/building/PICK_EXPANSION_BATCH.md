# 扩展战斗对照：88场，收益混合

> **⚠ 已废弃（2026-09-19 判死）** —— 本目录属于离线卡组评价 / DraftSim 调参线，
> 其 `work/` 数据已于 2026-09-20 删除，文中路径多数已失效。
> 入口与适用范围见 [`README.md`](README.md)。当前规范是 [`../../AGENT.md`](../../AGENT.md)。


**结论：本批不支持“稳定提升”，暂不进入批量标签或模型训练。** 3套牌组的12对有效留出比较中，求解器选牌8胜、冻结BuildValue基线9胜（少1胜）；6对更好、2对持平、4对更差。败转胜1次，胜转败2次。

实际执行88场（64开发＋24留出），87场完整有效、1场300秒超时。N16×dop2，战斗批次490.4秒（约8分10秒）；87场已返回CPU数据合计6309.9核秒，折合批次平均约12.87核，未计入超时进程未返回的CPU时间。

|牌组来源|冻结基线|求解器选择|留出胜场：求解器 / 基线|
|---|---|---|---|
|071d89030c0fad1f|BLADE_DANCE|DAGGER_SPRAY|2 / 3|
|10c4d71b61aeec36|DAGGER_SPRAY|skip|4 / 3|
|14a8539d859aa755|BACKFLIP|skip|2 / 3|

另2套因当前运行器不支持其中卡牌而未开跑；1套开发阶段有超时，按事前规则未进入留出。下方unavailable=12是24个原计划比较中的缺席项，不是12场失败。

Generated: 2026-09-17T17:42:11.111975Z
Protocol SHA256: `165914141e01e042e66762765464c9359503bf9efbffa6239898d2c1b08ff745`
Tool: `work/pick-expansion-batch/run_batch.py`
Actual battles: 88 (peak parallel 16), wall 490.4s, child CPU 6309.874399999997s
Caps: N16 dop2 nodes6000 soft120000ms searchTimeout180000ms maxturn40; deadline 1800s; no retries
Runtime stable: True

Six community terminal decks were frozen; four were loadable and received actual capped battles. The remaining two were excluded without changing their cards. This comparison uses (PROXY reconstruction: relics, potions, serialized props/enchantments ablated; one Ascender's Bane injected). Development seeds choose one action per deck; the frozen choice is then compared with the pinned compiled BuildValue pick on held-out seeds.

## BuildValue pin (compiled, historical)

- probe assembly: `BuildingDecisionProbe` version 1.0.0.0 sha256 `a6e4d31ac33a97cf044cba012dfd83db4aebea348271c982263ba7d65b48ec7a`
- CoopBots assembly: `CoopBots` version 1.0.0.0 sha256 `e7f40ba85105f64d65160f3b3f4d6f2359c5e72b096458af44935e9018547f17`
- This is a pinned compiled artifact, not re-derived from current production source.

## Per-deck results

| deck | hist won | deck | char | colorless | unsupported | BuildValue | chosen | dev score | avail |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 071d89030c0fad1f | True | 37 | 33 | 3 | - | BLADE_DANCE | DAGGER_SPRAY | [3, 90] | True |
| 1ea2817c7dacccfc | True | 29 | 27 | 1 | - | None | None | None | False |
| 2926074ecfa9f90c | True | 32 | 29 | 2 | CLUMSY,SQUASH,DOUBT | None | None | None | False |
| 0ee0ce474be7f8cc | False | 31 | 30 | 0 | MAD_SCIENCE | None | None | None | False |
| 10c4d71b61aeec36 | False | 23 | 21 | 1 | - | DAGGER_SPRAY | skip | [4, 151] | True |
| 14a8539d859aa755 | False | 29 | 26 | 2 | - | BACKFLIP | skip | [3, 86] | True |

Held-out pair counts: better=6 tie=2 worse=4 unavailable=12
Loss->win: 1; win->loss: 2; co-win HP deltas: [4, 13, 37, 29, -4, 40, -27]

## Held-out cells

| deck | encounter | seed | chosen | baseline | outcome | detail |
| --- | --- | --- | --- | --- | --- | --- |
| 071d89030c0fad1f | SOUL_NEXUS_ELITE | EXPANDEVAL01 | DAGGER_SPRAY | BLADE_DANCE | worse | win->loss |
| 071d89030c0fad1f | SOUL_NEXUS_ELITE | EXPANDEVAL02 | DAGGER_SPRAY | BLADE_DANCE | tie | loss->loss |
| 071d89030c0fad1f | FROG_KNIGHT_NORMAL | EXPANDEVAL01 | DAGGER_SPRAY | BLADE_DANCE | better | chosenHp=35 baseHp=31 |
| 071d89030c0fad1f | FROG_KNIGHT_NORMAL | EXPANDEVAL02 | DAGGER_SPRAY | BLADE_DANCE | better | chosenHp=37 baseHp=24 |
| 1ea2817c7dacccfc | SOUL_NEXUS_ELITE | EXPANDEVAL01 | - | - | unavailable | not all actions/cells complete and input-verified |
| 1ea2817c7dacccfc | SOUL_NEXUS_ELITE | EXPANDEVAL02 | - | - | unavailable | not all actions/cells complete and input-verified |
| 1ea2817c7dacccfc | FROG_KNIGHT_NORMAL | EXPANDEVAL01 | - | - | unavailable | not all actions/cells complete and input-verified |
| 1ea2817c7dacccfc | FROG_KNIGHT_NORMAL | EXPANDEVAL02 | - | - | unavailable | not all actions/cells complete and input-verified |
| 2926074ecfa9f90c | SOUL_NEXUS_ELITE | EXPANDEVAL01 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 2926074ecfa9f90c | SOUL_NEXUS_ELITE | EXPANDEVAL02 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 2926074ecfa9f90c | FROG_KNIGHT_NORMAL | EXPANDEVAL01 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 2926074ecfa9f90c | FROG_KNIGHT_NORMAL | EXPANDEVAL02 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 0ee0ce474be7f8cc | SOUL_NEXUS_ELITE | EXPANDEVAL01 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 0ee0ce474be7f8cc | SOUL_NEXUS_ELITE | EXPANDEVAL02 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 0ee0ce474be7f8cc | FROG_KNIGHT_NORMAL | EXPANDEVAL01 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 0ee0ce474be7f8cc | FROG_KNIGHT_NORMAL | EXPANDEVAL02 | - | - | unavailable | deck fails to load: cards outside harness pools |
| 10c4d71b61aeec36 | SOUL_NEXUS_ELITE | EXPANDEVAL01 | skip | DAGGER_SPRAY | better | chosenHp=47 baseHp=10 |
| 10c4d71b61aeec36 | SOUL_NEXUS_ELITE | EXPANDEVAL02 | skip | DAGGER_SPRAY | better | loss->win |
| 10c4d71b61aeec36 | FROG_KNIGHT_NORMAL | EXPANDEVAL01 | skip | DAGGER_SPRAY | better | chosenHp=68 baseHp=39 |
| 10c4d71b61aeec36 | FROG_KNIGHT_NORMAL | EXPANDEVAL02 | skip | DAGGER_SPRAY | worse | chosenHp=56 baseHp=60 |
| 14a8539d859aa755 | SOUL_NEXUS_ELITE | EXPANDEVAL01 | skip | BACKFLIP | worse | win->loss |
| 14a8539d859aa755 | SOUL_NEXUS_ELITE | EXPANDEVAL02 | skip | BACKFLIP | tie | loss->loss |
| 14a8539d859aa755 | FROG_KNIGHT_NORMAL | EXPANDEVAL01 | skip | BACKFLIP | better | chosenHp=54 baseHp=14 |
| 14a8539d859aa755 | FROG_KNIGHT_NORMAL | EXPANDEVAL02 | skip | BACKFLIP | worse | chosenHp=23 baseHp=50 |

## Limitations

- Both encounters (SOUL_NEXUS_ELITE, FROG_KNIGHT_NORMAL) are act2 proxies; not observed reward screens.
- PROXY ablations remove relics, potions, serialized card props and enchantments; real-run meaning changes.
- Two development and two held-out seeds over correlated decks are not independence or significance proof.
- Search timing has diagnostic limits; no node-only proof gate applies.
- Excluded decks fail to load because their cards are outside the harness-resolved Silent/colorless pools; no replacement fishing was performed.

## 后续决定

本轮收益混合，不扩产标签、不训练强度模型，也不优先开发更多采集或注入工具。若继续，直接冻结当前3套牌组的既有选择，再用6个新种子跑72场确认（3牌组×2遭遇×6种子×2动作），保持N16×2。优先看是否减少死亡，再看共同获胜时HP；不重新挑选动作，不把已使用种子继续称为未见留出。该批尚未执行。

## 验收边界

Astra独立重读88条记录，核对原始输入/请求/结果哈希、87场实际载入状态与搜索策略、冻结选择先于留出输入生成，并从原始结果重算12对比较。未追加战斗或运行无关测试。

冻结选择的原始.sha256采用UTF-8 LF文本哈希，Windows磁盘为CRLF；owner-acceptance.json同时记录原始字节与LF文本哈希，冻结文件未改动。部分journal.dir为短目录名，实际证据路径以命令--out为准。Worker运行时快照未包含求解器DLL的批前哈希，验收时其哈希与此前已验收版本一致，但不追称本轮做过新的批前测量。

基线为已冻结编译版BuildValue；本批是无遗物/药水、固定70HP的终局牌组代理实验，不能直接外推生产Bot整局收益。共同获胜7对的HP差合计+92、均值+13.14，仅适用于这7对，不抵消净少1胜。
