# Registers (or re-registers) two tasks. Needs admin.
#   "ThinkPad Fan Full"        - runs FanFull.ps1 as SYSTEM at every boot, hidden, no time limit, on battery or
#                                mains, restarted if it ever dies. Re-applies full speed every 5 s.
#   "ThinkPad Fan Full Resume" - fires on Windows' "system has resumed" events and runs FanFull.ps1 -Once, so the
#                                fan is back at full speed within a second or two of waking from sleep.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'FanFull.ps1'
$ns = 'Root/Microsoft/Windows/TaskScheduler'

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew -StartWhenAvailable

# --- main loop task ---
$name = 'ThinkPad Fan Full'
$action  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$trigger = New-ScheduledTaskTrigger -AtStartup
Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'Keeps the ThinkPad fan at full speed (EC register 0x2F = 0x40), re-applied every 5 s.' | Out-Null
Start-ScheduledTask -TaskName $name
"Registered and started '$name'."

# --- resume task: event trigger on Kernel-Power 107 / Power-Troubleshooter 1 (system resumed) ---
$name = 'ThinkPad Fan Full Resume'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -Once"
$evt = New-CimInstance -CimClass (Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace $ns) -ClientOnly
$evt.Enabled = $true
$evt.Subscription = @"
<QueryList>
  <Query Id="0" Path="System">
    <Select Path="System">*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107]]</Select>
    <Select Path="System">*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]</Select>
  </Query>
</QueryList>
"@
$resumeSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $name -Action $action -Trigger $evt -Settings $resumeSettings -Principal $principal `
    -Description 'Forces the ThinkPad fan to full speed immediately after the system resumes from sleep.' | Out-Null
"Registered '$name'."
