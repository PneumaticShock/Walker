# Runs SkillBench headless and prints one line per case. Close the Unity editor on this project first.
#   powershell -File Tools/bench.ps1 -Filter "walk 0.5 level|getup squat" -Label check -Repeats 2
# Exit code: 0 every case passed, 1 some failed, 2 error. Report: BalanceResults/skills-<Label>.json.
param(
    [string]$Filter = "walk 0.5 level",
    [string]$Label = "headless",
    [int]$Repeats = 1,
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Unity.exe"
)
$project = Split-Path -Parent $PSScriptRoot
$log = Join-Path $project "Logs\bench-$Label.log"
& $Unity -batchmode -projectPath $project -executeMethod HeadlessBench.Run `
    -benchLabel $Label -benchFilter $Filter -benchRepeats $Repeats -logFile $log | Out-Null
$code = $LASTEXITCODE
$report = Join-Path $project "BalanceResults\skills-$Label.json"
if (Test-Path $report) {
    $rows = (Get-Content $report -Raw | ConvertFrom-Json).rows
    foreach ($r in $rows) { "{0,-30} {1}  {2}" -f $r.name, $(if ($r.passed) { "PASS" } else { "fail" }), $r.why }
} else { "No report; see $log" }
exit $code
