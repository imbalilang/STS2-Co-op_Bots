using System;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CoopBots;

/// <summary>
/// 无人值守实机测试的开局：直接进一局并跳到一场战斗，不经过主菜单。
///
/// <para>
/// 依据是游戏自己的 <c>NSceneBootstrapper.StartNewRun</c>（`debug/scene_bootstrapper`，
/// 由 <c>--bootstrap</c> 加载）。那段的原话：
/// <code>
/// RunState runState = RunState.CreateForNewRun(
///     [Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL)],
///     acts, modifiers, GameMode.Standard, ascension, seed);
/// RunManager.Instance.SetUpNewSingleplayer(runState, saveRunHistory);
/// await PreloadManager.LoadRunAssets([character]);
/// await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter);
/// </code>
/// </para>
///
/// <para>
/// 为什么抄它而不是用 <c>--bootstrap</c>：那条路要求游戏工程里存在一个
/// <c>IBootstrapSettings</c> 实现，而 <c>BootstrapSettingsUtil.Get()</c> 读的是**编译期生成的**
/// 子类型表（接口上带 <c>[GenerateSubtypes]</c>），只扫游戏自己工程里的类型 —— mod 注入不进去。
/// 但它给出的这段全是公开 API，我们直接调即可。
/// </para>
///
/// <para>
/// 为什么是单机 1 席：<c>SetUpNewSingleplayer</c> 只建一个玩家。验收要的是「按剧本打完一场战斗」,
/// 单席足以覆盖；4 席是更强、但非必需的条件。配套改动见 <c>BotRuntime</c> 里把 Host-only 的门
/// 放开到 <c>Host or Singleplayer</c>。
/// </para>
///
/// <para>
/// 闸门：环境变量 <c>COOPBOTS_LIVE_TEST=1</c>。没设就永远不触发，正式包里是一段死代码。
/// </para>
/// </summary>
internal static class LiveTestAutoStart
{
    internal const string FlagFile = "live-test.flag";

    private static bool fired;
    private static bool failed;

    /// <summary>
    /// 闸门是**哨兵文件**，不是环境变量。
    /// 原因：经 Steam 启动时（`steam://rungameid/...`，这是让 mod 正常加载的唯一途径 ——
    /// 直启会进 editor 模式，在 mod 枚举之前还没载入 profile 设置，所有 mod 都被跳过），
    /// 游戏**不是启动器的子进程**，环境变量传不进去。文件在磁盘上，与启动方式无关。
    /// 路径取本程序集所在目录，也就是 `mods/CoopBots/`。
    /// </summary>
    /// <remarks>
    /// 路径取**本程序集自己的目录**，不是 <c>AppContext.BaseDirectory</c> —— mod 是被
    /// <c>Assembly.LoadFrom</c> 装进隔离加载上下文的，后者指向游戏 exe 目录，
    /// 于是哨兵文件永远找不到（实测：连 `armed` 那行都没打出来）。
    /// </remarks>
    internal static string FlagPath =>
        Path.Combine(Path.GetDirectoryName(typeof(LiveTestAutoStart).Assembly.Location) ?? ".", FlagFile);

    internal static bool Enabled => File.Exists(FlagPath);

    /// <summary>
    /// 每帧调用。只做廉价检查；真正开局是异步的，且只发一次。
    /// </summary>
    internal static void Tick()
    {
        if (fired || failed || !Enabled) return;
        try
        {
            if (NGame.Instance is not { } game) return;
            // This harness restores through NGame.LoadRun AFTER menu startup;
            // it is not NSceneBootstrapper. Native NGame.GameStartup only calls
            // LaunchMainMenu when StartOnMainMenu is true. Suppressing it while
            // waiting below for NMainMenu deadlocks on a sufficiently early tick.
            if (!game.GameStartupComplete.IsCompleted) return;
            // Wait until the native menu transition has completed, so it cannot
            // subsequently replace the diagnostic run scene.
            if (game.RootSceneContainer?.CurrentScene is not NMainMenu) return;
            if (RunManager.Instance is not { } runs) return;
            // 已经有局在跑：不抢。（也自然覆盖了"上一次还没退干净"的情况。）
            if (runs.DebugOnlyGetState() is not null) return;
        }
        catch { return; }

        fired = true;
        try
        {
            var (seed, multiplayer, room, encounter, ascension) = FlagSettings();
            // PRINT WHAT WAS PARSED. A targeted run silently did nothing and there was no way to
            // tell whether the sentinel was unread, mis-parsed, or ignored downstream —
            // the parser is the one place that distinguishes those, and it cost a whole batch to
            // discover it was not saying.
            // `search=tournament` forces the roll-out tournament as the decision-maker, and
            // `search=search` forces the segmented search. LEAVE IT OUT for the shipped default,
            // which since 2026-09-23 is the tournament on every table with at least one driven
            // seat (all-bot and mixed). Read HERE, at arming
            // time, because it is a property of how this run was launched and not of any
            // particular combat.
            var search = new string(File.ReadAllLines(FlagPath)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("search=", StringComparison.OrdinalIgnoreCase))?
                .Skip("search=".Length).ToArray() ?? []);
            KernelCombatPlanner.TournamentOverride = search.ToLowerInvariant() switch
            {
                "tournament" => true,
                "search" => false,
                _ => null,
            };
            // `perf=low|mid|high|ultra` picks the tournament's spend tier. It lives here rather
            // than in a settings file because what we are comparing is one machine's stutter
            // against its decision quality, and a batch has to be able to say which one it ran.
            // Absent or unrecognised leaves the shipped default.
            var perf = new string(File.ReadAllLines(FlagPath)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("perf=", StringComparison.OrdinalIgnoreCase))?
                .Skip("perf=".Length).ToArray() ?? []);
            KernelCombatPlanner.ApplyPerf(perf);
            // `party=IRONCLAD,SILENT,SILENT,IRONCLAD` sets the four seats' characters, in seat
            // order. Absent keeps the harness's historical all-Ironclad party. It is read and
            // LOGGED here for the same reason as `perf`: a party that is not the one the batch
            // asked for makes every number from that round a measurement of something else.
            var partyNames = new string(File.ReadAllLines(FlagPath)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("party=", StringComparison.OrdinalIgnoreCase))?
                .Skip("party=".Length).ToArray() ?? []);
            var party = ResolveParty(partyNames);
            var snapshot = File.ReadAllLines(FlagPath).Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("snapshot=", StringComparison.OrdinalIgnoreCase));
            if (snapshot is not null)
            {
                LiveTestSnapshotResume.Start(snapshot["snapshot=".Length..]);
                ArmRunSceneReassert();
                return;
            }
            Log.Info($"CoopBots live test: flag seed='{seed}' multiplayer={multiplayer} "
                + $"ascension={ascension} party='{partyNames}'(resolved={party.Count}) "
                + $"room='{room}' encounter='{encounter}' search='{search}' "
                + $"tournamentOverride={KernelCombatPlanner.TournamentOverride?.ToString() ?? "default"} "
                + $"perf='{perf}'(active={KernelCombatPlanner.Perf}).");
            if (multiplayer)
            {
                if (LiveTestMultiplayerRun.Start(seed, room, encounter, ascension, party))
                    ArmRunSceneReassert();
                else failed = true;
            }
            else StartOnMainThread();
        }
        catch (Exception error) { failed = true; Log.Error($"CoopBots live test: could not start the run: {error}"); }
    }

    /// <summary>
    /// 建局之后每隔几秒报一次"现场" —— 场景名 + 当前房间类型。
    ///
    /// <para>
    /// 为什么需要它：实测建局之后日志**一行都没有**（`started a run` 之后再无输出），
    /// 无从判断游戏是停在地图、空房间，还是在等一个永远不会来的输入。
    /// **先让系统说话，再动手** —— 这一行就能把范围从"某个地方"缩到"某一个界面"。
    /// </para>
    /// </summary>
    internal static void ReportWhere()
    {
        if (!fired || failed) return;
        var now = System.Environment.TickCount64;
        if (now - lastWhereMs < 5000) return;
        lastWhereMs = now;
        try
        {
            // 三个字段分开报，**不许合并**。上一版把 state 和 room 串成一行（`room=none`），
            // 而两个 `?.` 都会打印 "none"，于是同一行同时能表示两种相反的故障
            // （局没建 / 局建了但还没房间）——白跑了一轮。诊断行本身必须无歧义。
            var scene = NGame.Instance?.RootSceneContainer?.CurrentScene?.GetType().Name ?? "null";
            var state = RunManager.Instance?.DebugOnlyGetState();
            Log.Info($"CoopBots live test: where scene={scene} "
                + $"state={(state is null ? "null" : "set")} "
                + $"room={(state?.CurrentRoom is null ? "null" : state.CurrentRoom.RoomType.ToString())} "
                // roomStack 与 room 分开报：room 非 null 但栈为空 / 栈非空但 room 为 null，
                // 是两种不同的故障（前者房间没挂上，后者房间刚被 pop 掉）。合并就又会变成歧义行。
                + $"roomStack={(state is null ? "-" : state.CurrentRoomCount.ToString())} "
                + $"floor={(state is null ? "-" : state.TotalFloor.ToString())} "
                + $"botTicks={BotTicks} "
                // 地图屏状态：TryVoteOnMap 的第一道守卫就是
                // `NMapScreen.Instance is null || !IsOpen || !IsTravelEnabled || IsTraveling`
                // ——"站在地图上却不推进"最可能就是这道门。
                + $"mapScreen={(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance is null ? "null" : "ok")} "
                + $"mapOpen={MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.IsOpen.ToString() ?? "-"} "
                + $"mapTravel={MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.IsTravelEnabled.ToString() ?? "-"} "
                // hp/maxHp PER SEAT, in seat order. Without it the only HP readings in the log are
                // the tournament's PREDICTED end-of-fight arrays, which mix "what the search
                // expects" with "what happened" and cannot answer "how much did this fight cost
                // us". This line already runs every 5 s, so it is a free HP trajectory across the
                // whole run — the cheapest instrument for "which room is bleeding the party".
                // deck= is the other free reading: it shows how many cards a run actually got.
                + " hp=["
                + (state is null ? "-" : string.Join(",", state.Players.Select(p =>
                    $"{p.Creature.CurrentHp}/{p.Creature.MaxHp}")))
                + "] deck=["
                + (state is null ? "-" : string.Join(",", state.Players.Select(p => p.Deck.Cards.Count)))
                + "]");
        }
        catch (Exception error) { Log.Warn($"CoopBots live test: where failed: {error.GetType().Name}"); }
    }

    /// <summary>
    /// Put the run scene back if the main-menu flow took it after we built the run.
    ///
    /// <para>
    /// The live test arms from the main menu, and the menu is still loading when the run is
    /// created from it, so the menu's own scene change can land after ours. With the all-Ironclad
    /// default the preload is short enough that ours wins; a mixed party makes it long enough
    /// that the menu wins (measured 2026-09-27: `party=SILENT,SILENT,IRONCLAD,IRONCLAD` parked at
    /// `scene=NMainMenu state=set` forever, while the same package with the default party played
    /// a whole round). Re-asserting is exactly what <c>NSceneBootstrapper</c> and
    /// <c>NGame.LoadRun</c> do; it stops after a bounded number of ticks so a genuine failure to
    /// enter the run still surfaces instead of turning into an infinite scene rebuild.
    /// </para>
    /// </summary>
    internal static void EnsureRunScene()
    {
        if (LiveTestMultiplayerRun.PendingRunScene is not { } runState) return;
        try
        {
            if (NGame.Instance?.RootSceneContainer is not { } container) return;
            if (container.CurrentScene is NRun)
            {
                LiveTestMultiplayerRun.ClearPendingRunScene();
                return;
            }
            if (System.Environment.TickCount64 > runSceneDeadlineMs)
            {
                LiveTestMultiplayerRun.ClearPendingRunScene();
                Log.Warn("CoopBots live test: gave up re-asserting the run scene; the run never "
                    + "took the screen. Check the main-menu flow for a hard failure.");
                return;
            }
            Log.Warn($"CoopBots live test: the run was built but the scene is "
                + $"{container.CurrentScene?.GetType().Name ?? "null"}; re-asserting NRun.");
            container.SetCurrentScene(NRun.Create(runState));
        }
        catch (Exception error)
        {
            Log.Warn($"CoopBots live test: re-asserting the run scene failed: {error.GetType().Name}");
        }
    }

    /// <summary>Deadline for <see cref="EnsureRunScene"/>, set when the run is built.</summary>
    private static long runSceneDeadlineMs;

    internal static void ArmRunSceneReassert() => runSceneDeadlineMs = System.Environment.TickCount64 + 20_000;

    private static long lastWhereMs;

    /// <summary>Set by the game-over patch, consumed by the next live-test tick.</summary>
    private static bool runEnded;

    /// <summary>
    /// Told by the game-over screen. Deliberately only a FLAG: the quit itself has to happen on
    /// the live-test ticker, never inside the screen's own <c>_Ready</c>, because
    /// <see cref="NGame.Quit"/> tears the scene tree down and doing that while Godot is still
    /// building the screen it was called from is the kind of re-entrancy that shows up as a
    /// crash in one round out of twenty. An attended game never sets this — a human has to be
    /// able to read the screen.
    /// </summary>
    internal static void GameOverSeen()
    {
        if (LiveTestMenuTicker.Armed) runEnded = true;
    }

    /// <summary>
    /// WHY THIS EXISTS: nothing in the pipeline closed the client when a run ended. The mod's
    /// driver only STARTS a run, and the watcher in scripts/run-live-loop.ps1 only kills on a
    /// silence timeout. Measured live 2026-09-26 that left 20 heartbeat lines (~100 s) of pure
    /// idle on the game-over screen every round, against a ~4 minute run — a third of each batch
    /// spent on a screen with nobody left to press "continue". Quitting here makes the loop's
    /// existing "the client exited by itself" path fire within a second or two instead.
    /// </summary>
    internal static void QuitIfRunEnded()
    {
        if (!runEnded || !LiveTestMenuTicker.Armed) return;
        runEnded = false;
        Log.Info("CoopBots live test: the run is over — quitting so the unattended loop can start the next round.");
        try { NGame.Instance?.Quit(); }
        catch (Exception error) { Log.Warn($"CoopBots live test: quit after the run failed: {error.GetType().Name}"); }
    }

    /// <summary>
    /// `BotRuntime.Tick()` 被调用的次数 —— 由 BotRuntime 递增（`LiveTestMenuTicker.Armed` 为真时）。
    /// 它回答一个二分问题：进局之后 `NRun._Process` 这条挂点**到底通不通**。
    /// 涨 → tick 跑着（问题在 TryVoteOnMap 之类的逻辑）；不涨 → 挂点不通，得另找。
    /// </summary>
    internal static long BotTicks;

    /// <summary>
    /// 哨兵文件里的设置。第一行是种子；可选一行 <c>mode=single</c> 切回旧的单席直进战斗模式，
    /// 一行 <c>ascension=N</c> 指定多人局的飞升等级（缺省 10）。
    /// </summary>
    /// <remarks>
    /// 默认是**多人**：用户 2026-09-20 的规格要求跑 4 BOT 的 A10 爬塔，
    /// 而单席直进战斗那条路测不到任何 "Host 才亮" 的门。保留 <c>mode=single</c>
    /// 是为了那条路仍然可复现 —— 它当初是用来把"进不了局"和"进得了局"分开的。
    /// </remarks>
    /// <summary>
    /// Resolve the sentinel's <c>party=</c> list BY ID against this build's characters, never by
    /// a guessed type name. An unknown id is an ERROR, not a silent fallback to Ironclad: a party
    /// that is not the one the batch asked for makes every number from that round a measurement
    /// of something else, which is exactly the failure this project keeps paying for (R5).
    /// </summary>
    private static IReadOnlyList<CharacterModel> ResolveParty(string csv)
    {
        var names = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return [];
        var resolved = new List<CharacterModel>();
        foreach (var name in names)
        {
            var wanted = name.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
                ? name["CHARACTER.".Length..]
                : name;
            var character = ModelDb.AllCharacters.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.Entry, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id.Entry, "CHARACTER." + wanted, StringComparison.OrdinalIgnoreCase));
            if (character is null)
                throw new InvalidOperationException(
                    $"live-test.flag names party member '{name}', which this build does not know.");
            resolved.Add(character);
        }
        return resolved;
    }

    private static (string Seed, bool Multiplayer, string Room, string Encounter, int Ascension) FlagSettings()
    {
        try
        {
            var lines = File.ReadAllLines(FlagPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            string Value(string key) => lines
                .FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?
                .Substring(key.Length + 1).Trim() ?? "";
            var seed = lines.FirstOrDefault(line => !line.Contains('=')) ?? "COOPBOTS-LIVE";
            var multiplayer = Value("mode").Length == 0
                || !Value("mode").Contains("single", StringComparison.OrdinalIgnoreCase);
            // ASCENSION, added 2026-09-25: the multiplayer lobby was hardwired to A10, so a batch
            // aimed at the A5 clear rate would have measured the wrong difficulty entirely.
            // Clamped, not refused — but it is printed, so a mis-typed value cannot be mistaken
            // for "the run really was A5".
            var ascension = int.TryParse(Value("ascension"), out var asked)
                ? Math.Clamp(asked, 0, 20)
                : LiveTestMultiplayerRun.DefaultAscension;
            // TARGETING, added 2026-09-21 after nine live batches could not verify a two-phase
            // boss fix: the act-1 boss is seed-chosen AND the transition only fires if the party
            // actually reaches 0 HP on it, so "run until it happens" was a lottery that hit
            // four times in nine. `room=Boss` + `encounter=ENCOUNTER.WATERFALL_GIANT_BOSS`
            // enters that fight every time.
            return (seed, multiplayer, Value("room"), Value("encounter"), ascension);
        }
        catch { return ("COOPBOTS-LIVE", true, "", "", LiveTestMultiplayerRun.DefaultAscension); }
    }

    /// <summary>
    /// 建局并接管本机席位 —— 逐行照抄 <c>NSceneBootstrapper.StartNewRun</c>（`debug/scene_bootstrapper`）。
    ///
    /// <para>
    /// 为什么是 async + <c>TaskHelper.RunSafely</c>：游戏自己就是这么写的
    /// （`NSceneBootstrapper._Ready`：`TaskHelper.RunSafely(StartNewRun());`）。它从主线程起步，
    /// GodotSharp 里装着 <c>GodotSynchronizationContext</c>／<c>GodotTaskScheduler</c>，
    /// 所以每个 `await` 的续体都会回到主线程 —— 这正是"照抄上游"的意义，别再自己发明同步方式。
    /// </para>
    ///
    /// <para>
    /// **上一版的真正缺陷**：调完 <c>SetActInternal(0)</c> 就收工了。对照上游可见还差最后一步 ——
    /// 进房间。<c>NSceneBootstrapper</c> 在 `RoomType.Unassigned` 时 `await EnterAct(0)`，
    /// 其余房间类型走 `await EnterRoomDebug(settings.RoomType, …, encounter.ToMutable())`。
    /// `NGame.StartRun` 也是同一形状（`await RunManager.Instance.EnterAct(0, doTransition: false)`）。
    /// 缺这一步的现场是 `scene=NRun state=set room=null floor=0 mapOpen=False` ——
    /// 局和界面都在，但**没有任何房间**，于是地图屏永远不会被 `NMapRoom._Ready` 打开，
    /// `TryVoteOnMap` 的第一道 guard 永远为假，日志里连一场 `combat summary` 都不会有。
    /// </para>
    ///
    /// <para>
    /// 本实机测试直接进 <see cref="CultistsNormal"/>（2 个邪教徒，最短的怪物房），
    /// 不经过地图投票 —— 验收要的是「按剧本打完一场战斗」，地图投票是另一条独立的失败面。
    /// </para>
    /// </summary>
    private static void StartOnMainThread() => TaskHelper.RunSafely(StartAsync());

    private static async Task StartAsync()
    {
        var character = ModelDb.Character<Ironclad>();
        string seed = FlagSettings().Seed;
        var players = new List<MegaCrit.Sts2.Core.Entities.Players.Player>
        {
            MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun(
                character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL),
        };
        var runState = RunState.CreateForNewRun(
            players,
            ModelDb.Acts.Select(act => act.ToMutable()).ToList(),
            [], GameMode.Standard, 0, seed);
        // shouldSave: false —— 游戏自己的文档原话是 "false during tests and bootstrap"。
        RunManager.Instance.SetUpNewSingleplayer(runState, false);
        // 只驱动 AutoPilot.Drives(netId) 的席位；不标记就没人打牌、没人推地图。
        if (RunManager.Instance.NetService is { } net)
        {
            AutoPilot.Set(net.NetId, true);
            Log.Info($"CoopBots live test: auto-pilot enabled for seat {net.NetId}.");
        }
        // 以下是 NSceneBootstrapper.StartNewRun:103-131 的逐行对照（去掉 settings 那两行）。
        await PreloadManager.LoadRunAssets(new List<CharacterModel> { character });
        // NSceneBootstrapper 漏了这一步，但 NGame.StartRun 与 NCharacterSelectScreen 都有；
        // 文档原话是 "This is called when creating a new run"（RunManager.cs:721）。
        await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();
        NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
        await RunManager.Instance.SetActInternal(0);
        RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(runState.RunLocation);
        RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(runState.MapLocation);
        var settings = FlagSettings();
        // Resolved BY ID against the build's own model database, never by a guessed type name:
        // `--perf-probe --list-encounters` prints the whole list with its ids.
        var encounter = settings.Encounter.Length == 0
            ? ModelDb.Encounter<CultistsNormal>().ToMutable()
            : ModelDb.AllEncounters.FirstOrDefault(e =>
                string.Equals(e.Id.Entry, settings.Encounter, StringComparison.OrdinalIgnoreCase))?.ToMutable()
                ?? throw new InvalidOperationException(
                    $"live-test.flag names encounter='{settings.Encounter}', which this build does not know.");
        var room = Enum.TryParse<RoomType>(settings.Room.Length == 0 ? "Monster" : settings.Room,
            ignoreCase: true, out var parsed) ? parsed : RoomType.Monster;
        await RunManager.Instance.EnterRoomDebug(room, MapPointType.Unassigned, encounter);
        Log.Info($"CoopBots live test: started a run (seed={seed}) into {encounter.Id} as room={room}.");
    }
}

/// <summary>
/// 主菜单期的驱动入口 —— 用 **Godot 定时器**，不是 Harmony 补丁。
///
/// <para>
/// 为什么不用补丁：`BotRuntime.Tick()` 挂在 `NRun._Process` 上，只在局内跑，而开局驱动
/// 必须在**还没有局**的时候触发。先试了 `[HarmonyPatch(typeof(NGame), "_Process")]`，
/// 但 `NGame` **根本没有声明 `_Process`**（它只声明 `_EnterTree/_Ready/_ExitTree/_Notification/_Input`），
/// 于是套件门禁直接拒了包 —— 那次失败是对的，挂不上的补丁不该混进包里。
/// </para>
///
/// <para>
/// 定时器绕开了补丁解析的全部问题，而且天然在主线程、天然周期执行。
/// 首次命中后 `Tick()` 里的 <c>fired</c> 会挡住后续所有调用，所以不必精确地停表。
/// </para>
/// </summary>
/// <summary>
/// The game-over screen is the one node BOTH endings share — a wipe and a victory both land here
/// — and it has no singleton to poll, so this patch is what tells the unattended live test that
/// the run is over. Without it the client sat on that screen until the watcher's silence timeout.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
internal static class LiveTestGameOverQuit
{
    [HarmonyPostfix]
    internal static void Postfix()
    {
        LiveTestAutoStart.GameOverSeen();
        // MACHINE-READABLE OUTCOME. A batch that measures a clear rate has to be able to say,
        // per round, whether the party won or wiped, and the log is the only place it can read
        // it: `shouldSave: false` keeps these runs out of the profile's run history, so there is
        // no run-*.json to read the `win` field from. IsVictoryRoom is the game's own answer —
        // true only for the Architect room — and it is the same field NGameOverScreen._Ready
        // reads to pick its banner (`bool win = _runState.CurrentRoom?.IsVictoryRoom ?? false`).
        // Printed on its own line, with the floor and act, so a batch can also tell "died on
        // floor 16" from "died on floor 48" without parsing the run history.
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var won = state?.CurrentRoom?.IsVictoryRoom ?? false;
            Log.Info($"CoopBots live test: run outcome — victory={won.ToString().ToLowerInvariant()} "
                + $"act={state?.CurrentActIndex.ToString() ?? "-"} floor={state?.TotalFloor.ToString() ?? "-"}");
        }
        catch (Exception error)
        {
            Log.Warn($"CoopBots live test: outcome line failed: {error.GetType().Name}");
        }
    }
}

internal static class LiveTestMenuTicker
{
    private static bool attached;

    /// <summary>定时器已挂上（哨兵文件存在）。供 BotRuntime 零成本判断是否需要计数。</summary>
    internal static bool Armed => attached;

    /// <summary>在 mod 初始化时调用；只有闸门打开时才真的挂表。</summary>
    internal static void Attach()
    {
        if (attached || !LiveTestAutoStart.Enabled) return;
        attached = true;
        Schedule();
        Log.Info("CoopBots live test: armed (waiting for the main menu to be ready).");
    }

    private static void Schedule()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            tree.CreateTimer(1.0).Timeout += () =>
            {
                try
                {
                    LiveTestAutoStart.Tick();
                    LiveTestAutoStart.ReportWhere();
                    LiveTestAutoStart.EnsureRunScene();
                    LiveTestAutoStart.QuitIfRunEnded();
                }
                catch { }
                Schedule();
            };
        }
        catch { }
    }
}

/// <summary>
/// Ensure the sentinel harness reaches the native main menu it waits for.
/// V21 startup evidence: old suppression loaded the mod but never reached
/// "snapshot resumed"; native GameStartup skips LaunchMainMenu when false.
/// </summary>
[HarmonyPatch(typeof(NGame), nameof(NGame._EnterTree))]
internal static class LiveTestSuppressMenuPatch
{
    private static void Prefix(NGame __instance)
    {
        try
        {
            if (LiveTestAutoStart.Enabled) __instance.StartOnMainMenu = true;
        }
        catch { }
    }
}
