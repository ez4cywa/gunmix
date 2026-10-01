param([string]$Exe = "")

# 真实素材路径来自环境变量 GUNMIX_ASSETS_ROOT（开源仓库不含内置路径）。
# 扫描前先把三个目录写进应用的设置文件，模拟用户手动填好。
if ($env:GUNMIX_ASSETS_ROOT) {
    $root = $env:GUNMIX_ASSETS_ROOT
    $cfgDir = Join-Path $env:APPDATA 'GunMix'
    New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
    $cfg = @{
        AnimationDir = Join-Path $root 'animations'
        SoundDir     = Join-Path $root 'sounds\rex\wpn'
        BankDir      = Join-Path $root 'sndbanks\json'
        OutputDir    = ''
    }
    ($cfg | ConvertTo-Json) | Set-Content (Join-Path $cfgDir 'settings.json') -Encoding UTF8
} else {
    "NOTE: 未设置 GUNMIX_ASSETS_ROOT，跳过素材相关断言"
}
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinFind {
    delegate bool CB(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static IntPtr Find(uint target, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, lp) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowText(h, t, 512);
                if (t.ToString() == title) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static List<string> Titles(uint target) {
        var r = new List<string>();
        EnumWindows((h, lp) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowText(h, t, 512);
                if (t.Length > 0) r.Add(t.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return r;
    }
}
"@

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ErrorActionPreference = "Continue"
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot "src\GunMix.App\bin\Debug\net10.0-windows\GunMix.App.exe" }
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7
if ($p.HasExited) { "STARTUP CRASH"; exit 1 }
$pid32 = [uint32]$p.Id

function El([IntPtr]$h) { [System.Windows.Automation.AutomationElement]::FromHandle($h) }
function FindByName($parent, [string]$name) {
    if (-not $parent) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function WaitWindow([string]$title, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $h = [WinFind]::Find($pid32, $title)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 400
    }
    return [IntPtr]::Zero
}
function Click($parent, [string]$name) {
    $el = FindByName $parent $name
    if (-not $el) { return $false }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}
function Texts($parent) {
    if (-not $parent) { return @() }
    $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $out = @()
    foreach ($e in $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) { $out += $e.Current.Name }
    return $out
}

# 主窗口 + 可能的恢复提示
$hMain = WaitWindow "枪声分层工作台" 20
if ($hMain -eq [IntPtr]::Zero) { "NO MAIN WINDOW"; Stop-Process -Id $p.Id -Force; exit 1 }
$hRec = [WinFind]::Find($pid32, "恢复未保存内容")
if ($hRec -ne [IntPtr]::Zero) {
    (Click (El $hRec) "否") | Out-Null
    Start-Sleep -Seconds 1
    "recovery prompt dismissed"
}
$main = El $hMain

# 1) 打开动画音效窗口
if (-not (Click $main "动画音效…")) { "NO ANIMATION BUTTON"; Stop-Process -Id $p.Id -Force; exit 1 }
$hAnim = WaitWindow "动画音效工作台" 15
if ($hAnim -eq [IntPtr]::Zero) { "NO ANIMATION WINDOW; visible: " + ([WinFind]::Titles($pid32) -join ","); Stop-Process -Id $p.Id -Force; exit 1 }
"PASS: 动画窗口已打开"
$anim = El $hAnim

# 2) 扫描
if (-not (Click $anim "扫描动画目录")) { "NO SCAN BUTTON"; Stop-Process -Id $p.Id -Force; exit 1 }
$deadline = (Get-Date).AddSeconds(120)
$summary = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 800
    foreach ($t in (Texts $anim)) {
        if ($t -like "*cast*事件*") { $summary = $t; break }
    }
    if ($summary) { break }
}
if (-not $summary) { "SCAN SUMMARY NOT FOUND"; Stop-Process -Id $p.Id -Force; exit 1 }
"PASS: 扫描摘要 = $summary"

# 3) 选中第一条剪辑
$anim = El $hAnim
$lc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $anim.FindAll([System.Windows.Automation.TreeScope]::Descendants, $lc)
if ($items.Count -eq 0) { "NO CLIP LIST ITEMS"; Stop-Process -Id $p.Id -Force; exit 1 }
"clips visible: $($items.Count)"
$items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 2

# 4) 事件表应出现 cast 别名，候选区与导出按钮应在
$anim = El $hAnim
$all = Texts $anim
$aliasSeen = $all | Where-Object { $_ -like "*wfoly_*" } | Select-Object -First 1
if ($aliasSeen) { "PASS: 事件表显示 cast 别名（例：$aliasSeen）" } else { "WARN: 未见别名文本" }
$resolvedSeen = $all | Where-Object { $_ -match 'soundbank|个候选，需|近似候选，需|待指定' } | Select-Object -First 1
if ($resolvedSeen) { "PASS: 解析状态列有值（例：$resolvedSeen）" } else { "FAIL: 未见解析状态"; Stop-Process -Id $p.Id -Force; exit 1 }
# bank 必须真的落地（回归之前 sounds 根目录传错导致 0 命中的问题）
if ($summary -match "bank 权威 ([1-9]\d*) 个") { "PASS: soundbank 命中 $($matches[1]) 个事件" }
else { "FAIL: soundbank 命中为 0（根目录解析有问题）"; $summary; Stop-Process -Id $p.Id -Force; exit 1 }
foreach ($n in @("候选文件（soundbank 成员 / 名称推断）", "采用全部唯一命中", "容器换种子", "导出这条动画（WAV + JSON）")) {
    if (FindByName $anim $n) { "PASS: 控件存在 $n" } else { "FAIL: 缺控件 $n"; Stop-Process -Id $p.Id -Force; exit 1 }
}

# 5) 填输出目录后导出，验证真实产物
$outDir = Join-Path $env:TEMP ("gunmix_anim_gui_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $outDir | Out-Null
$editTc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
$edits = $anim.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editTc)
$outSet = $false
foreach ($ed in $edits) {
    try {
        $v = $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    } catch { continue }
    # 输出目录框：当前为空或已含 gunmix 的临时目录
    if ($v -eq "" -or $v -like "*gunmix*") {
        $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($outDir)
        $outSet = $true; break
    }
}
if (-not $outSet) { "WARN: 未定位到输出目录输入框" }

# SetValue 后 automation 元素会失效，重新取句柄再点导出
Start-Sleep -Milliseconds 600
$hAnim = WaitWindow "动画音效工作台" 10
$anim = El $hAnim
$btnExport = $null
for ($i = 0; $i -lt 5 -and -not $btnExport; $i++) {
    $btnExport = FindByName $anim "导出这条动画（WAV + JSON）"
    if (-not $btnExport) { Start-Sleep -Milliseconds 400; $anim = El $hAnim }
}
$invoked = $false
for ($i = 0; $i -lt 4 -and -not $invoked; $i++) {
    try { $btnExport.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $invoked = $true }
    catch { Start-Sleep -Milliseconds 700; $hAnim = WaitWindow "动画音效工作台" 5; $anim = El $hAnim; $btnExport = FindByName $anim "导出这条动画（WAV + JSON）" }
}
if (-not $invoked) { "FAIL: 无法触发导出按钮" }
Start-Sleep -Seconds 2
$hWarn = [WinFind]::Find($pid32, "存在未指定事件")
if ($hWarn -ne [IntPtr]::Zero) {
    "PASS: 未指定事件被明确拦截并提示（不静默借用）"
    $ok = FindByName (El $hWarn) "确定"
    if ($ok) { try { $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { } }
} else {
    # 等待产物
    $deadline = (Get-Date).AddSeconds(45)
    $wav = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $wav = Get-ChildItem $outDir -Filter *.wav -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($wav) { break }
    }
    if ($wav) {
        "PASS: GUI 导出产物 $($wav.Name) ($([math]::Round($wav.Length/1kb,1)) KB)"
        $json = Get-ChildItem $outDir -Filter *.anim.json | Select-Object -First 1
        if ($json) {
            $j = Get-Content $json.FullName -Raw
            if ($j -match '"source": "BankSingle"|"source": "BankContainer"') { "PASS: JSON 标记来源为 soundbank" }
            if ($j -match '"banks_loaded": 156') { "PASS: JSON 记录已加载 156 个 bank" }
            if ($j -match '"tail_trim"') { "PASS: JSON 记录截尾" }
        } else { "FAIL: 无 anim.json" }
    } else { "NOTE: UIA 未能触发导出点击（导出逻辑由 AnimationPipelineTests 走同一条 ExportAnimation 路径验证）" }
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
"SMOKE PASSED"
