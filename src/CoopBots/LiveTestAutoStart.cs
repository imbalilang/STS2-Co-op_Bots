using System;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
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
            // 走 --bootstrap 那条路：**不要建主菜单**。
            // 依据是游戏自己的 NSceneBootstrapper._Ready：`_game.StartOnMainMenu = false;`
            // 然后等 GameStartupComplete，再 SetUpNewSingleplayer + EnterRoomDebug。
            // 实测（2026-09-20）：从主菜单进 `EnterRoomDebug` 会在 `RunManager.ClearScreens()`
            // 空引用 —— 即使主菜单已经完全就绪（日志里 `Time to main menu: 26,243ms` 就在报错之前）。
            // 那条流不是给"从菜单跳到房间"用的。
            // mod 初始化（早于主菜单加载 ~26s）时就关掉它，所以来得及。
            game.StartOnMainMenu = false;
            if (!game.GameStartupComplete.IsCompleted) return;
            // **必须等主菜单真的加载完再建局。**
            // 实测（2026-09-20，第 12 轮）：只等 GameStartupComplete 就建局，日志里
            // `where scene=NMainMenu room=none` 一直重复 —— 局建起来了但没留下来，
            // 因为主菜单那条流程**在我们建局之后**才加载完（`Time to main menu: 26,243ms`），
            // 它继续跑就把局状态覆盖掉了。`StartOnMainMenu = false` 拦不住，那时它已越过那个判断。
            if (game.RootSceneContainer?.CurrentScene is not NMainMenu) return;
            if (RunManager.Instance is not { } runs) return;
            // 已经有局在跑：不抢。（也自然覆盖了"上一次还没退干净"的情况。）
            if (runs.DebugOnlyGetState() is not null) return;
        }
        catch { return; }

        fired = true;
        try
        {
            var (seed, multiplayer, room, encounter) = FlagSettings();
            // PRINT WHAT WAS PARSED. A targeted run silently did nothing and there was no way to
            // tell whether the sentinel was unread, mis-parsed, or ignored downstream —
            // the parser is the one place that distinguishes those, and it cost a whole batch to
            // discover it was not saying.
            // `search=tournament` makes the roll-out tournament the decision-maker for the whole
            // fight. Read HERE, at arming time, because it is a property of how this run was
            // launched and not of any particular combat.
            var search = new string(File.ReadAllLines(FlagPath)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("search=", StringComparison.OrdinalIgnoreCase))?
                .Skip("search=".Length).ToArray() ?? []);
            KernelCombatPlanner.TournamentDrives =
                search.Equals("tournament", StringComparison.OrdinalIgnoreCase);
            Log.Info($"CoopBots live test: flag seed='{seed}' multiplayer={multiplayer} "
                + $"room='{room}' encounter='{encounter}' search='{search}' "
                + $"tournamentDrives={KernelCombatPlanner.TournamentDrives}.");
            if (multiplayer)
            {
                if (!LiveTestMultiplayerRun.Start(seed, room, encounter)) failed = true;
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
                + $"mapTravel={MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.IsTravelEnabled.ToString() ?? "-"}");
        }
        catch (Exception error) { Log.Warn($"CoopBots live test: where failed: {error.GetType().Name}"); }
    }

    private static long lastWhereMs;

    /// <summary>
    /// `BotRuntime.Tick()` 被调用的次数 —— 由 BotRuntime 递增（`LiveTestMenuTicker.Armed` 为真时）。
    /// 它回答一个二分问题：进局之后 `NRun._Process` 这条挂点**到底通不通**。
    /// 涨 → tick 跑着（问题在 TryVoteOnMap 之类的逻辑）；不涨 → 挂点不通，得另找。
    /// </summary>
    internal static long BotTicks;

    /// <summary>
    /// 哨兵文件里的设置。第一行是种子；可选一行 <c>mode=single</c> 切回旧的单席直进战斗模式。
    /// </summary>
    /// <remarks>
    /// 默认是**多人**：用户 2026-09-20 的规格要求跑 4 BOT 的 A10 爬塔，
    /// 而单席直进战斗那条路测不到任何 "Host 才亮" 的门。保留 <c>mode=single</c>
    /// 是为了那条路仍然可复现 —— 它当初是用来把"进不了局"和"进得了局"分开的。
    /// </remarks>
    private static (string Seed, bool Multiplayer, string Room, string Encounter) FlagSettings()
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
            // TARGETING, added 2026-09-21 after nine live batches could not verify a two-phase
            // boss fix: the act-1 boss is seed-chosen AND the transition only fires if the party
            // actually reaches 0 HP on it, so "run until it happens" was a lottery that hit
            // four times in nine. `room=Boss` + `encounter=ENCOUNTER.WATERFALL_GIANT_BOSS`
            // enters that fight every time.
            return (seed, multiplayer, Value("room"), Value("encounter"));
        }
        catch { return ("COOPBOTS-LIVE", true, "", ""); }
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
                try { LiveTestAutoStart.Tick(); LiveTestAutoStart.ReportWhere(); } catch { }
                Schedule();
            };
        }
        catch { }
    }
}

/// <summary>
/// 在 `NGame` 建立**之前**就把 `StartOnMainMenu` 关掉 —— 这是 `NSceneBootstrapper` 的做法。
///
/// <para>
/// 实测（2026-09-20，第 14 轮）：用无歧义诊断拿到
/// `where scene=NMainMenu state=set room=null floor=0` ——
/// **局建好了、而且留住了**（`state=set`），但 **scene 仍是主菜单**，没人把它推进到地图。
/// 原因是游戏自己的状态机在 `StartOnMainMenu` 为真时会一直停在主菜单；
/// 定时器里再设已经太晚（`NGame` 在 `GameStartupWrapper`／`_EnterTree` 时就读过这个值了）。
/// </para>
///
/// <para>
/// 挂 `_EnterTree` 而不是 `_Process`：**`NGame` 确实声明了 `_EnterTree`**（`NGame.cs:527`），
/// 而它没有声明 `_Process` —— 上一版挂 `_Process` 就是因此被套件门禁正确拒掉的。
/// </para>
/// </summary>
[HarmonyPatch(typeof(NGame), nameof(NGame._EnterTree))]
internal static class LiveTestSuppressMenuPatch
{
    private static void Prefix(NGame __instance)
    {
        try
        {
            if (LiveTestAutoStart.Enabled) __instance.StartOnMainMenu = false;
        }
        catch { }
    }
}
