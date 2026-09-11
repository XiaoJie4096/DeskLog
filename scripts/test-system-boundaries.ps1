param([Parameter(Mandatory)][ValidateSet('Lock','Sleep','Hibernate')][string]$Action)
$ErrorActionPreference = 'Stop'
# Run only with explicit user authorization: these operations affect the interactive session.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RijiPowerTest {
    [StructLayout(LayoutKind.Sequential)] public struct PowerStatus {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("user32.dll", SetLastError=true)] public static extern bool LockWorkStation();
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateWaitableTimer(IntPtr attrs, bool reset, string name);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr callback, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] public static extern bool CancelWaitableTimer(IntPtr timer);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("powrprof.dll", SetLastError=true)] public static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
}
'@
if ($Action -eq 'Lock') {
    if (-not [RijiPowerTest]::LockWorkStation()) { throw 'Windows 拒绝了锁屏请求。' }
    Write-Output '锁屏请求已接受。请使用正常方式解锁；日迹独立测试实例继续记录边界证据。'
    return
}
$powerStatus = New-Object RijiPowerTest+PowerStatus
if (-not [RijiPowerTest]::GetSystemPowerStatus([ref]$powerStatus)) { throw '无法确认当前电源来源，不执行挂起。' }
if ($powerStatus.ACLineStatus -notin @(0,1)) { throw '电源来源未知，不执行挂起。' }
$schemeOutput = & powercfg /getactivescheme
if ($LASTEXITCODE -ne 0) { throw '无法读取电源方案。' }
$scheme = [regex]::Match(($schemeOutput -join ' '), '[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}').Value
if (-not $scheme) { throw '无法识别电源方案。' }
$query = & powercfg /query $scheme SUB_SLEEP RTCWAKE
if ($LASTEXITCODE -ne 0) { throw '无法读取唤醒策略。' }
$values = [regex]::Matches(($query -join ' '), '0x([0-9a-fA-F]{8})')
if ($values.Count -ne 2) { throw '无法识别原有唤醒策略，不更改系统。' }
$index = if ($powerStatus.ACLineStatus -eq 1) { 0 } else { 1 }
$original = [Convert]::ToInt32($values[$index].Groups[1].Value, 16)
$option = if ($index -eq 0) { '/setacvalueindex' } else { '/setdcvalueindex' }
$timer = [IntPtr]::Zero
$changed = $false
try {
    & powercfg $option $scheme SUB_SLEEP RTCWAKE 1
    if ($LASTEXITCODE -ne 0) { throw '无法临时启用唤醒定时器，不执行挂起。' }
    $changed = $true
    & powercfg /setactive $scheme
    if ($LASTEXITCODE -ne 0) { throw '无法应用唤醒策略。' }
    $timer = [RijiPowerTest]::CreateWaitableTimer([IntPtr]::Zero, $true, $null)
    if ($timer -eq [IntPtr]::Zero) { throw '无法建立自动唤醒定时器。' }
    [long]$due = -450000000
    if (-not [RijiPowerTest]::SetWaitableTimer($timer, [ref]$due, 0, [IntPtr]::Zero, [IntPtr]::Zero, $true)) { throw '无法设定自动唤醒。' }
    if ([Runtime.InteropServices.Marshal]::GetLastWin32Error() -eq 50) { throw '系统不支持定时器唤醒，不执行挂起。' }
    Write-Output "已设置 45 秒后唤醒；原唤醒策略 $original 将在返回后恢复。开始 $Action。"
    $before = [DateTimeOffset]::UtcNow
    if (-not [RijiPowerTest]::SetSuspendState(($Action -eq 'Hibernate'), $false, $false)) { throw ('挂起失败，Windows 错误：' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    Write-Output "挂起调用已返回，经过 $(([DateTimeOffset]::UtcNow - $before).TotalSeconds) 秒。实际挂起与恢复以日迹和系统事件为准。"
} finally {
    if ($timer -ne [IntPtr]::Zero) { [void][RijiPowerTest]::CancelWaitableTimer($timer); [void][RijiPowerTest]::CloseHandle($timer) }
    if ($changed) {
        & powercfg $option $scheme SUB_SLEEP RTCWAKE $original
        if ($LASTEXITCODE -ne 0) { Write-Error "恢复失败：请将电源方案 $scheme 的 RTCWAKE 恢复为 $original。" }
        & powercfg /setactive $scheme
        Write-Output "已恢复原唤醒策略：$original。"
    }
}
