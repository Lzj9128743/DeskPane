@echo off
chcp 65001 >nul
title DeskPane - 一键恢复桌面文件
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$ErrorActionPreference='SilentlyContinue';" ^
"Get-Process -Name 'DeskPane*' | Stop-Process -Force;" ^
"$desktop = [Environment]::GetFolderPath('Desktop');" ^
"$appData = Join-Path $env:LOCALAPPDATA 'DeskPane';" ^
"$ledgerPath = Join-Path $appData 'staging-ledger.json';" ^
"$stagingRoot = Join-Path $appData 'staging';" ^
"$count = 0;" ^
"function Move-Safe($src, $dst) {" ^
"  if (-not (Test-Path -LiteralPath $src)) { return $false }" ^
"  $target = $dst;" ^
"  if (Test-Path -LiteralPath $target) {" ^
"    $dir = Split-Path $dst -Parent; $name = [IO.Path]::GetFileNameWithoutExtension($dst); $ext = [IO.Path]::GetExtension($dst);" ^
"    for ($i=2; $i -lt 1000; $i++) { $c = Join-Path $dir \"$name ($i)$ext\"; if (-not (Test-Path -LiteralPath $c)) { $target = $c; break } }" ^
"  }" ^
"  Move-Item -LiteralPath $src -Destination $target -Force; return (Test-Path -LiteralPath $target);" ^
"}" ^
"if (Test-Path -LiteralPath $ledgerPath) {" ^
"  $entries = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json;" ^
"  foreach ($e in @($entries)) {" ^
"    $dst = if ($e.OriginalDesktopPath) { $e.OriginalDesktopPath } else { Join-Path $desktop (Split-Path $e.StagedPath -Leaf) };" ^
"    if (Move-Safe $e.StagedPath $dst) { $count++ }" ^
"  }" ^
"  Remove-Item -LiteralPath $ledgerPath -Force;" ^
"}" ^
"foreach ($root in @($stagingRoot, (Join-Path ([IO.Path]::GetPathRoot($desktop)) '.DeskPaneStaging'))) {" ^
"  if (Test-Path -LiteralPath $root) {" ^
"    foreach ($sub in Get-ChildItem -LiteralPath $root -Directory) {" ^
"      foreach ($item in Get-ChildItem -LiteralPath $sub.FullName -Force) {" ^
"        if (Move-Safe $item.FullName (Join-Path $desktop $item.Name)) { $count++ }" ^
"      }" ^
"      if ((Get-ChildItem -LiteralPath $sub.FullName -Force | Measure-Object).Count -eq 0) { Remove-Item -LiteralPath $sub.FullName -Force }" ^
"    }" ^
"  }" ^
"}" ^
"Add-Type -AssemblyName PresentationFramework;" ^
"[System.Windows.MessageBox]::Show(\"已安全恢复 $count 个文件/图标到桌面。\", 'DeskPane 一键恢复');"
