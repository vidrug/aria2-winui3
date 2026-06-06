# ui-tests.ps1 — batch UI tests for Aria2Gui
param([Parameter(Mandatory)][int]$AppPid)

$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $results = @()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++; $script:results += @{ name = $Name; status = "PASS" }
        } else {
            $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" }
        }
    } catch {
        $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
    }
}

New-Item -ItemType Directory -Force -Path "screenshots" | Out-Null

# ─── Engine + toolbar ───
Test-UI "Engine running (status bar)" { winapp ui wait-for "EngineStatusText" -a $AppPid --value "aria2 работает" -t 15000 }
Test-UI "Add button exists"       { winapp ui wait-for "AddDownloadButton" -a $AppPid -t 3000 }
Test-UI "ResumeAll button exists" { winapp ui wait-for "ResumeAllButton" -a $AppPid -t 3000 }
Test-UI "PauseAll button exists"  { winapp ui wait-for "PauseAllButton" -a $AppPid -t 3000 }
Test-UI "Clear button exists"     { winapp ui wait-for "ClearStoppedButton" -a $AppPid -t 3000 }
Test-UI "Settings button exists"  { winapp ui wait-for "SettingsButton" -a $AppPid -t 3000 }
Test-UI "Counts show empty state" { winapp ui wait-for "CountsText" -a $AppPid --value "Активных: 0" --contains -t 5000 }
winapp ui screenshot -a $AppPid -o "screenshots/01-initial.png" 2>$null

# ─── Add-download dialog ───
Test-UI "Open add dialog" { winapp ui invoke "AddDownloadButton" -a $AppPid }
Test-UI "Urls box appears" { winapp ui wait-for "UrlsBox" -a $AppPid -t 4000 }
Test-UI "Pick torrent button in dialog" { winapp ui wait-for "PickTorrentButton" -a $AppPid -t 2000 }
winapp ui screenshot -a $AppPid -o "screenshots/02-add-dialog.png" 2>$null
Test-UI "Type URL" { winapp ui set-value "UrlsBox" "https://proof.ovh.net/files/10Mb.dat" -a $AppPid }
Test-UI "Click Add (primary)" { winapp ui invoke "PrimaryButton" -a $AppPid }
Test-UI "Dialog dismissed" { winapp ui wait-for "UrlsBox" -a $AppPid --gone -t 5000 }

# ─── Download appears and completes ───
Test-UI "Download appears in counts" { winapp ui wait-for "CountsText" -a $AppPid --value "Активных: 1" --contains -t 10000 }
winapp ui screenshot -a $AppPid -o "screenshots/03-downloading.png" 2>$null
Test-UI "Download completes" { winapp ui wait-for "CountsText" -a $AppPid --value "Завершённых: 1" --contains -t 60000 }
Test-UI "Row open-folder button exists" { winapp ui wait-for "RowOpenFolderButton" -a $AppPid -t 3000 }
Test-UI "Row remove button exists" { winapp ui wait-for "RowRemoveButton" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots/04-completed.png" 2>$null

# ─── Remove the completed row ───
Test-UI "Remove download row" { winapp ui invoke "RowRemoveButton" -a $AppPid }
Test-UI "Counts back to zero" { winapp ui wait-for "CountsText" -a $AppPid --value "Завершённых: 0" --contains -t 10000 }

# ─── Settings dialog ───
Test-UI "Open settings" { winapp ui invoke "SettingsButton" -a $AppPid }
Test-UI "Settings fields appear" { winapp ui wait-for "DownLimitBox" -a $AppPid -t 4000 }
winapp ui screenshot -a $AppPid -o "screenshots/05-settings.png" 2>$null
Test-UI "Set download limit 3 MB/s" { winapp ui set-value "DownLimitBox" "3" -a $AppPid }
Test-UI "Commit via focus shift" { winapp ui focus "UpLimitBox" -a $AppPid }
Test-UI "Save settings" { winapp ui invoke "PrimaryButton" -a $AppPid }
Test-UI "Settings dismissed" { winapp ui wait-for "DownLimitBox" -a $AppPid --gone -t 5000 }

# Persisted to disk? Packaged dev runs virtualize AppData into the package LocalCache.
$candidates = @(
    "$env:LOCALAPPDATA\Packages\5E3C0559-7A88-4535-94C7-6700E2187906_1z32rh13vfry6\LocalCache\Local\Aria2Gui\settings.json",
    "$env:LOCALAPPDATA\Aria2Gui\settings.json"
)
Start-Sleep -Seconds 1
$settingsFile = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$settings = if ($settingsFile) { Get-Content $settingsFile -Raw | ConvertFrom-Json } else { $null }
if ($settings -and $settings.MaxDownloadLimit -eq "3072K") {
    $pass++; $results += @{ name = "Settings persisted (3072K)"; status = "PASS" }
} else {
    $fail++; $results += @{ name = "Settings persisted (3072K)"; status = "FAIL"; detail = "MaxDownloadLimit=$($settings.MaxDownloadLimit) file=$settingsFile" }
}

# ─── Re-open settings (pause lets the previous ShowAsync fully unwind) ───
Start-Sleep -Seconds 2
Test-UI "Re-open settings" { winapp ui invoke "SettingsButton" -a $AppPid }
Start-Sleep -Seconds 1
# NumberBox value is not readable via get-value (returns the header); the
# settings.json assertion above already covers the round-trip.
Test-UI "Settings fields shown again" { winapp ui wait-for "DownLimitBox" -a $AppPid -t 5000 }
Test-UI "Close settings" { winapp ui invoke "CloseButton" -a $AppPid }
Test-UI "Settings closed" { winapp ui wait-for "DownLimitBox" -a $AppPid --gone -t 5000 }

# ─── Accessibility audit ───
$allElements = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).elements
$appElements = @($allElements | Where-Object {
    $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|Edit' -and
    $_.name -notmatch 'Minimize|Maximize|Close|System' -and
    $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
})
$missingId = @($appElements | Where-Object { -not $_.automationId })
if ($missingId.Count -eq 0) {
    $pass++; $results += @{ name = "All app controls have AutomationId"; status = "PASS" }
} else {
    $names = ($missingId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", "
    $fail++; $results += @{ name = "AutomationId coverage"; status = "FAIL"; detail = "Missing: $names" }
}

winapp ui screenshot -a $AppPid -o "screenshots/06-final.png" 2>$null

Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
    Write-Host "  FAIL: $($_.name) — $($_.detail)" -ForegroundColor Red
}
$results | ConvertTo-Json | Out-File "test-results.json"
if ($fail -gt 0) { exit 1 } else { exit 0 }
