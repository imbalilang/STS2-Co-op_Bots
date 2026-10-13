# Template consumed by a10-benchmark.py, not a standalone launch entry point.
        $a10Client = $a10NewClients[0]
        $a10SelectedPid = [int]$a10Client.Id
        $a10SelectedTicks = $a10Client.StartTime.ToUniversalTime().Ticks.ToString()
        $a10ExpectedExe = [IO.Path]::GetFullPath('__DIAGNOSTIC_GAME_EXE_LITERAL__')
        $a10PathWait = [Diagnostics.Stopwatch]::StartNew()
        $a10PathWaitLimitMs = 10000
        $a10PathReads = 0
        $a10ResolvedPath = $null
        $a10PathReadSource = $null
        do {
          if ($a10PathWait.ElapsedMilliseconds -ge $a10PathWaitLimitMs) { throw 'Diagnostic new PID executable unresolved within 10000ms; no ownership claimed' }
          $a10StillNew = @(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $_.StartTime.ToUniversalTime() -ge $a10LaunchStarted })
          if ($a10StillNew.Count -ne 1 -or [int]$a10StillNew[0].Id -ne $a10SelectedPid) { throw 'Diagnostic unique new PID changed during image wait; no ownership claimed' }
          $a10FreshClient = Get-Process -Id $a10SelectedPid -ErrorAction SilentlyContinue
          if (!$a10FreshClient -or $a10FreshClient.ProcessName -ne 'SlayTheSpire2' -or $a10FreshClient.StartTime.ToUniversalTime().Ticks.ToString() -ne $a10SelectedTicks) { throw 'Diagnostic new PID start identity changed; no ownership claimed' }
          $a10PathReads++
          $a10ProcessPath = $null
          try { $a10ProcessPath = $a10FreshClient.Path } catch { $a10ProcessPath = $null }
          if ($a10ProcessPath) {
            if ([IO.Path]::GetFullPath($a10ProcessPath) -ne $a10ExpectedExe) { throw 'Diagnostic new PID executable differs; no ownership claimed' }
            $a10ResolvedPath = [IO.Path]::GetFullPath($a10ProcessPath)
            $a10PathReadSource = 'fresh-GetProcess'
          }
          $a10CimRows = @()
          try { $a10CimRows = @(Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $a10SelectedPid) -OperationTimeoutSec 1 -ErrorAction Stop) } catch { $a10CimRows = @() }
          if ($a10CimRows.Count -gt 1) { throw 'Diagnostic new PID CIM identity ambiguous; no ownership claimed' }
          if ($a10CimRows.Count -eq 1) {
            $a10Cim = $a10CimRows[0]
            if ([int]$a10Cim.ProcessId -ne $a10SelectedPid -or $a10Cim.Name -ne 'SlayTheSpire2.exe') { throw 'Diagnostic new PID CIM identity changed; no ownership claimed' }
            if ($a10Cim.ExecutablePath) {
              if ([IO.Path]::GetFullPath($a10Cim.ExecutablePath) -ne $a10ExpectedExe) { throw 'Diagnostic new PID CIM executable differs; no ownership claimed' }
              if (!$a10ResolvedPath) {
                $a10ResolvedPath = [IO.Path]::GetFullPath($a10Cim.ExecutablePath)
                $a10PathReadSource = 'fresh-CIM-between-PID-ticks-checks'
              }
            }
          }
          $a10RecheckedClient = Get-Process -Id $a10SelectedPid -ErrorAction SilentlyContinue
          if (!$a10RecheckedClient -or $a10RecheckedClient.ProcessName -ne 'SlayTheSpire2' -or $a10RecheckedClient.StartTime.ToUniversalTime().Ticks.ToString() -ne $a10SelectedTicks) { throw 'Diagnostic new PID identity changed after image read; no ownership claimed' }
          $a10RecheckedNew = @(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $_.StartTime.ToUniversalTime() -ge $a10LaunchStarted })
          if ($a10RecheckedNew.Count -ne 1 -or [int]$a10RecheckedNew[0].Id -ne $a10SelectedPid) { throw 'Diagnostic unique new PID changed after image read; no ownership claimed' }
          if ($a10PathWait.ElapsedMilliseconds -ge $a10PathWaitLimitMs) { throw 'Diagnostic new PID executable unresolved within 10000ms; no ownership claimed' }
          if ($a10ResolvedPath) { $a10Client = $a10RecheckedClient; break }
          Start-Sleep -Milliseconds 250
        } while ($true)
        $a10PathWait.Stop()
