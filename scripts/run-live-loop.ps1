# 目标 Windows PowerShell 5.1（与 scripts/build.ps1 一致；系统上没有 pwsh）。
<#
.SYNOPSIS
  一次无人值守实机测试的完整骨架：杀客户端 → 部署 → 启动 → 看护日志。

.DESCRIPTION
  AGENT.md §5 S5 的落地。它**不**分析日志、**不**改代码 —— 那两件需要判断，由代理做。
  它只负责"让一局跑起来"和"卡住了把它杀掉"，把代理从重复的手工动作里摘出来。

  ⚠️ 两个前提，缺一个这套循环就跑不起来：

  1. **全自动开局驱动**（mod 内调用 RunManager.SetUpNewMultiplayer）必须先进包里。
     没有它，客户端只会停在主菜单 —— 这个脚本会如实地报告"日志 5 分钟没动"然后杀掉，
     看起来像卡死，其实是没人开局。
  2. **搜索心跳日志**（KernelCombatPlanner 每 45s 一行 search heartbeat）。
     没有它，合法的最长 10 分钟开局搜索会被"$SilenceMinutes 分钟没有进度"规则误杀。

.PARAMETER DryRun
  只做检查与打印：不杀进程、不启动、不部署覆盖。用来验证参数与路径接线。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live-loop.ps1 -DryRun
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live-loop.ps1 -Rounds 3
#>
param(
    [string]$GameRoot = 'B:\SteamLibrary\steamapps\common\Slay the Spire 2',
    [string]$PackageDir = '',
    [int]$SilenceMinutes = 5,
    [int]$Rounds = 1,
    [string]$Seed = 'COOPBOTS-LIVE',
    # Tournament spend tier, written into the sentinel as `perf=<名字>`: low | mid | high | ultra.
    # Empty leaves the mod's shipped default. It travels in the FLAG rather than an environment
    # variable for the same reason the seed does — launched through Steam the game is not this
    # process's child, so it would never see the environment.
    [string]$Perf = '',
    # Multiplayer ascension, written into the sentinel as `ascension=<n>`. Empty leaves the mod's
    # default (10). The lobby still clamps it to the profile's unlocked maximum, so asking for a
    # difficulty this profile has not unlocked fails loudly in the log rather than silently.
    [int]$Ascension = 0,
    # Four seat characters in seat order, comma separated, written into the sentinel as
    # `party=`. Empty keeps the harness's historical all-Ironclad party. The host seat is set
    # the same way the bots are, so this really is the whole party.
    [string]$Party = '',
    [switch]$NoAutoslay,
    [int]$PollSeconds = 15,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
# 控制台是 GBK 代码页，中文输出会变乱码 —— 代理要读这些行，先改成 UTF-8。
# 必须在 param() 之后：param 必须是脚本的第一条可执行语句。
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $GameRoot 'SlayTheSpire2.exe'
$mods = Join-Path $GameRoot 'mods\CoopBots'
$log = Join-Path $env:APPDATA 'SlayTheSpire2\logs\godot.log'

if (!(Test-Path -LiteralPath $exe)) { throw "找不到客户端：$exe" }
if (!(Test-Path -LiteralPath $log)) { throw "找不到日志：$log（客户端必须至少跑过一次）" }

# 最新的带标签测试包。故意不取 outputs\CoopBots-v<版本>.zip —— 那是已发布工件，见 AGENT.md §3。
if (!$PackageDir) {
    $candidates = Get-ChildItem -LiteralPath (Join-Path $repo 'outputs') -Directory -Filter 'CoopBots-v*-*' |
        Where-Object { $_.Name -notmatch '\.zip$' } |
        Sort-Object LastWriteTime -Descending
    if ($candidates.Count -eq 0) { throw 'outputs/ 下没有带标签的测试包目录。先跑 build.ps1 -Label <label>。' }
    $PackageDir = $candidates[0].FullName
}
$payload = Join-Path $PackageDir 'CoopBots'
if (!(Test-Path -LiteralPath $payload)) { throw "$PackageDir 里没有 CoopBots/ 子目录" }
$files = 'CoopBots.dll', 'CoopBots.Kernel.dll', 'mod_manifest.json', 'THIRD_PARTY_NOTICES.md'
$payload = (Resolve-Path -LiteralPath $payload).Path
if (Test-Path -LiteralPath (Join-Path $payload 'LICENSE')) { $files += 'LICENSE' }
$licensePayload = Join-Path $payload 'licenses'
if (Test-Path -LiteralPath $licensePayload) {
    foreach ($item in (Get-ChildItem -LiteralPath $licensePayload -Recurse -File | Sort-Object FullName)) {
        $files += $item.FullName.Substring($payload.TrimEnd('\', '/').Length + 1)
    }
}
foreach ($f in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $f))) { throw "包里缺少 $f" }
}
$kernelHash = (Get-FileHash -LiteralPath (Join-Path $payload 'CoopBots.Kernel.dll') -Algorithm SHA256).Hash

function Stop-Client {
    $procs = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue
    if (!$procs) { return $false }
    if ($DryRun) { Write-Host "[dry] 已有 $($procs.Count) 个客户端；直接入口将拒绝运行。"; return $true }
    # This legacy skeleton has no recorded process ownership. Never close or
    # force an arbitrary user's game. The benchmark replaces this whole function
    # with its recorded-PID normal-close implementation before actual execution.
    throw '已有客户端进程，旧直接入口不拥有其身份，拒绝关闭。请使用 scripts/a10-benchmark.py 的实机入口。'
}

Write-Host "包      : $PackageDir"
Write-Host "内核 sha: $kernelHash"
Write-Host "部署到  : $mods"
Write-Host "日志    : $log"
Write-Host "静默阈值: $SilenceMinutes 分钟  轮数: $Rounds  飞升: $(if ($Ascension -gt 0) { $Ascension } else { '默认(10)' })  perf: $(if ($Perf) { $Perf } else { '默认' })  party: $(if ($Party) { $Party } else { '默认(全 Ironclad)' })"
if ($DryRun) { Write-Host '[dry] 只检查接线，不杀进程、不部署、不启动。' }

for ($round = 1; $round -le $Rounds; $round++) {
    Write-Host "`n===== 第 $round/$Rounds 轮 ====="
    [void](Stop-Client)

    if (!$DryRun) {
        New-Item -ItemType Directory -Force -Path $mods | Out-Null
        foreach ($f in $files) {
            $target = Join-Path $mods $f
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath (Join-Path $payload $f) -Destination $target -Force
        }
        Write-Host "已部署 $($files.Count) 个文件"
    }

    # 记录起点，之后所有判断都相对它
    $logStart = (Get-Item -LiteralPath $log).Length
    $mtime = (Get-Item -LiteralPath $log).LastWriteTimeUtc
    Write-Host "启动前日志长度=$logStart  最后写入=$mtime"

    if (!$DryRun) {
        # --fastmp 是游戏自己的命令行开关（NMultiplayerHostSubmenu:191）：
        # PlatformType = (SteamInitializer.Initialized && !HasArg("fastmp")) ? Steam : None
        # 带上它，游戏的建房流程就走 StartENetHost（纯 UDP），
        # 不需要 Steam 大厅、不需要第二个账号、也不会留 Steam 会话残留。
        # --autoslay 是游戏自带的无人值守模式（NGame.cs:694）：自动跑完整局（含战斗、地图、房间、奖励），
        # 并且自带 --seed 与 --log-file。--fastmp 让它走 ENet 而非 Steam。
        # 我们的 mod 负责接管战斗；这一版先用它把「一局跑起来」这件事解决。
        # --autoslay 实测是死路：NGame.cs:694 的条件是 `!IsReleaseGame() && HasArg("autoslay")`，
        # 而本安装版 IsReleaseGame() 硬编码 return true。所以开局改由 mod 自己驱动：
        # LiveTestAutoStart（抄自游戏的 NSceneBootstrapper）在闸门打开时直接建局并进入战斗房。
        # 哨兵文件（不是环境变量）：经 Steam 启动时游戏不是本进程的子进程，环境变量传不进去。
        $flagLines = @($Seed)
        if ($Perf) { $flagLines += "perf=$Perf" }
        if ($Ascension -gt 0) { $flagLines += "ascension=$Ascension" }
        if ($Party) { $flagLines += "party=$Party" }
        $flagBody = $flagLines -join "`n"
        Set-Content -LiteralPath (Join-Path $mods 'live-test.flag') -Value $flagBody -NoNewline -Encoding ascii
        # --force-steam=off 是必须的：直启 exe 时 Steam 未初始化，游戏会卡在
        # "Failed to initialize Steam! Attempting to show error popup"，连主菜单都到不了。
        # （现有 oracle harness 的启动参数里一直有它，我上一版替换参数时丢掉了 —— 这就是
        #   那次"日志一个字节都不涨"的原因。）
        # 故意不再传 --log-file：它会把日志重定向到别处，而看护器盯的是 godot.log，
        # 两者不一致就变成"看起来卡死"。一份日志、一个监视目标。
        # 经 STEAM 启动，而不是直启 exe。
        # 原因：直启会进入 "editor mode"，它在**mod 枚举之前**还没载入 profile 设置，
        # 于是 ModManager._settings 为 null → PlayerAgreedToModLoading 取不到 →
        # 每个 mod 都被 "user has not yet seen the mods warning" 跳过（实测：把该标志
        # 写进 default/1/settings.save 也没用，因为读它的时机更晚）。
        # 经 Steam 走正常模式与正常档案（steam/<id>/modded/profile2），那里的
        # mods 警告早已接受，mod 会正常加载。
        # 2868840 是游戏 appid（见 …/workshop/content/2868840/…）。
        Start-Process "steam://rungameid/2868840" | Out-Null
        Write-Host '客户端已启动（经 Steam：steam://rungameid/2868840）。'
    } else {
        Write-Host '[dry] 会在这里启动客户端。'
        break
    }

    # 看护判据是【进度】，不是【日志 mtime】。
    # 为什么改：mod 自己每 5 秒打一行 `live test: where …` 心跳，它会把 mtime 一路刷新，
    # 于是"日志 5 分钟没动"永远不成立 —— 实测 2026-09-20 第 1 轮就卡在 floor 4 的商店里
    # 一直空转，看护器本该杀掉却永远不杀。心跳是为了让人能诊断，不是为了给看护器喂信号。
    # 所以这里改成：从日志里抽【局面指纹】，指纹不变才算卡住。
    # 一个合法的长搜索（开局搜索最长 10 分钟）会持续打 `search heartbeat`，指纹在变，不会被误杀。
    # 指纹只由【最后一条有意义的行】构成，不用滑动窗口里的行数。
    # 为什么：第 2 轮实测指纹里的计数会随旧行滚出 -Tail 窗口而【变小】
    # （`sum=3|search=25` → `sum=4|search=23`），一次缩小本身就是"变化"，
    # 于是卡死会被自己的窗口效应刷掉 —— 一个不单调的进度量不是进度量。
    # 用最后一条进度行的原文：没有新的进度行就完全不变，重复的同一行也确实是卡住。
    $ProgressPattern = 'combat summary|combat start:|search heartbeat|search-complete|' +
        'opening search #|plan step|plan dropped|plan sentinel|plan boundary|' +
        'CoopBots shop:|CoopBots room:|CoopBots rewards:|CoopBots team:|' +
        'CoopBots build:|CoopBots: queued|CoopBots kernel:'
    function Get-Progress {
        $tail = Get-Content -LiteralPath $log -Tail 200 -ErrorAction SilentlyContinue
        if (!$tail) { return '<empty>' }
        $where = $tail | Select-String -SimpleMatch 'live test: where' | Select-Object -Last 1
        $place = if ($where) {
            $m1 = [regex]::Match($where.Line, 'floor=(\S+)')
            $m2 = [regex]::Match($where.Line, 'room=(\S+)')
            "$($m1.Groups[1].Value)/$($m2.Groups[1].Value)"
        } else { 'none' }
        $last = $tail | Select-String -Pattern $ProgressPattern | Select-Object -Last 1
        "$place|$($last.Line)"
    }

    $deadline = (Get-Date).AddMinutes($SilenceMinutes)
    $print = Get-Progress
    Write-Host "进度指纹: $print"
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $PollSeconds
        if (!(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue)) {
            Write-Host '客户端自己退出了（崩溃或正常关闭）。本轮结束。'
            break
        }
        $now = Get-Progress
        if ($now -ne $print) {
            $print = $now
            $deadline = (Get-Date).AddMinutes($SilenceMinutes)
            Write-Host "进度指纹: $print"
        }
    }

    $size = (Get-Item -LiteralPath $log).Length
    if ((Get-Date) -ge $deadline -and (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue)) {
        Write-Host "$SilenceMinutes 分钟没有任何局面变化（指纹停在 $print）→ 判定卡死，杀掉客户端。"
        Write-Host "（若停在主菜单，多半是【全自动开局驱动】还没进包 —— 见本脚本页首警告）"
        [void](Stop-Client)
    }
    Write-Host "本轮结束：日志 $logStart → $size 字节。把这一段的 combat summary 行交给代理分析。"
}

Write-Host '循环结束。'
