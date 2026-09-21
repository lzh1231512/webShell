ws-scrcpy service
PowerShell
Service
[Metadata]
Port=8000
ProcessName=node
CommandLineContains=dist\index.js
[/Metadata]
Set-Location 'D:\zack\ws-scrcpy-master'
if (-not (Test-Path '.\dist\index.js')) {
    npm run dist
    if ($LASTEXITCODE -ne 0) {
        throw 'ws-scrcpy build failed.'
    }
}
node .\dist\index.js
