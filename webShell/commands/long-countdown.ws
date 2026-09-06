PowerShell 长任务示例
PowerShell
Long
for ($i = 1; $i -le 60; $i++) {
    Write-Output "Running step $i/60"
    Start-Sleep -Seconds 1
}
Write-Output "Task completed"
