param(
    [string]$GodotPath = 'C:\Godot_v4.6.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
)

$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$taskProject = (Join-Path $taskRoot 'godot') -replace '\\', '/'
$taskEndpoint = 'http://127.0.0.1:19085/mcp'
$taskLogs = Join-Path $taskProject '.tmp/mcp'

function Get-ConnectedProject {
    $taskPayload = @{
        jsonrpc = '2.0'; id = 1; method = 'tools/call'
        params = @{ name = 'get_project_info'; arguments = @{} }
    } | ConvertTo-Json -Depth 10 -Compress
    $taskReply = Invoke-RestMethod -Uri $taskEndpoint -Method Post -ContentType 'application/json' -Body $taskPayload -TimeoutSec 2
    if ($taskReply.error -or $taskReply.result.isError) { throw 'MCP project read failed.' }
    return ($taskReply.result.content[0].text | ConvertFrom-Json).project_path.TrimEnd('/')
}

$taskConnected = $null
try { $taskConnected = Get-ConnectedProject } catch [System.Net.Http.HttpRequestException] {} catch [System.Threading.Tasks.TaskCanceledException] {}
if ($taskConnected) {
    if ($taskConnected -ne $taskProject) { throw "Port 19085 belongs to another project: $taskConnected" }
    Write-Output "Godot MCP ready: $taskEndpoint ($taskProject)"
    exit 0
}

if (-not (Test-Path -LiteralPath $GodotPath)) { throw "Godot executable not found: $GodotPath" }
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
$taskEditor = Start-Process -FilePath $GodotPath -WindowStyle Hidden -PassThru -ArgumentList @(
    '--headless', '--editor', '--path', ('"' + $taskProject + '"'),
    '--', '--mcp-server', '--mcp-port=19085', '--mcp-transport=http'
) -RedirectStandardOutput (Join-Path $taskLogs 'server.log') -RedirectStandardError (Join-Path $taskLogs 'server.stderr.log')
$taskEditor.Id | Set-Content -LiteralPath (Join-Path $taskLogs 'server.pid')
$taskDeadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    if ($taskEditor.HasExited) { throw "MCP editor exited. See $taskLogs" }
    try { $taskConnected = Get-ConnectedProject } catch { Start-Sleep -Milliseconds 250; continue }
    if ($taskConnected -ne $taskProject) { throw "MCP project mismatch: $taskConnected" }
    Write-Output "Godot MCP ready: $taskEndpoint ($taskProject)"
    exit 0
} while ([DateTime]::UtcNow -lt $taskDeadline)
throw "MCP startup timed out. See $taskLogs"
