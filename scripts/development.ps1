[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('run', 'down', 'test', 'migrate-database', 'recreate-database')]
    [string]$Command,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ApiProject = Join-Path $ProjectRoot 'src/FaceVerification.Api/FaceVerification.Api.csproj'
$LaunchSettingsFile = Join-Path $ProjectRoot 'src/FaceVerification.Api/Properties/launchSettings.json'
$InfrastructureProject = Join-Path $ProjectRoot 'src/FaceVerification.Infrastructure/FaceVerification.Infrastructure.csproj'
$PidDirectory = Join-Path $ProjectRoot '.run'
$PidFile = Join-Path $PidDirectory 'face-verification-api.pid'

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "Required command '$Name' is not installed or not available on PATH." }
}

function New-DevelopmentEnvironmentFile([string]$EnvironmentFile) {
    $exampleFile = Join-Path $ProjectRoot '.env.example'
    if (-not (Test-Path -LiteralPath $exampleFile)) { throw 'Missing .env.example; the development environment cannot be initialized.' }

    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $content = Get-Content -LiteralPath $exampleFile | ForEach-Object {
        if ($_ -match '^POSTGRES_PASSWORD=') { "POSTGRES_PASSWORD=$password" } else { $_ }
    }
    Set-Content -LiteralPath $EnvironmentFile -Value $content -Encoding utf8NoBOM
    Write-Host 'Created local .env with a generated development-only PostgreSQL password. The file is ignored by Git.'
}

function Import-DevelopmentEnvironment {
    $environmentFile = Join-Path $ProjectRoot '.env'
    if (-not (Test-Path -LiteralPath $environmentFile)) { New-DevelopmentEnvironmentFile $environmentFile }
    foreach ($line in Get-Content -LiteralPath $environmentFile) {
        if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
        $name, $value = $line -split '=', 2
        [Environment]::SetEnvironmentVariable($name.Trim(), $value.Trim(), 'Process')
    }
    if (-not $env:POSTGRES_DB -or -not $env:POSTGRES_USER -or -not $env:POSTGRES_PASSWORD) { throw '.env must define POSTGRES_DB, POSTGRES_USER, and POSTGRES_PASSWORD.' }
    $port = if ($env:POSTGRES_PORT) { $env:POSTGRES_PORT } else { '5432' }
    $env:ConnectionStrings__FaceVerification = "Host=localhost;Port=$port;Database=$($env:POSTGRES_DB);Username=$($env:POSTGRES_USER);Password=$($env:POSTGRES_PASSWORD)"
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
}

function Assert-Docker {
    Assert-Command docker
    try { docker info *> $null } catch {
        if ($IsWindows) {
            $desktop = Join-Path $env:ProgramFiles 'Docker/Docker/Docker Desktop.exe'
            if (Test-Path -LiteralPath $desktop) { Start-Process -FilePath $desktop -WindowStyle Hidden | Out-Null }
        }
        $deadline = [DateTime]::UtcNow.AddMinutes(2)
        do { Start-Sleep -Seconds 2; try { docker info *> $null; return } catch { } } while ([DateTime]::UtcNow -lt $deadline)
        throw 'Docker Engine did not become available within two minutes.'
    }
}

function Start-Postgres {
    Assert-Docker
    docker compose --project-directory $ProjectRoot up -d postgres
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    do {
        $health = docker inspect --format '{{.State.Health.Status}}' face-verification-postgres 2>$null
        if ($health -eq 'healthy') { return }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'PostgreSQL did not become healthy within two minutes.'
}

function Get-OwnedApiProcess {
    if (-not (Test-Path -LiteralPath $PidFile)) { return $null }
    $stored = (Get-Content -LiteralPath $PidFile -Raw).Trim()
    if ($stored -notmatch '^\d+$') { Remove-Item -LiteralPath $PidFile -Force; return $null }
    $process = Get-Process -Id ([int]$stored) -ErrorAction SilentlyContinue
    if (-not $process) { Remove-Item -LiteralPath $PidFile -Force; return $null }
    $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $stored").CommandLine
    if ($commandLine -notlike '*FaceVerification.Api*') { throw "PID $stored exists but does not belong to FaceVerification.Api; it will not be stopped." }
    return $process
}

function Stop-OwnedApi {
    $process = Get-OwnedApiProcess
    if (-not $process) { return }
    $children = Get-CimInstance Win32_Process | Where-Object ParentProcessId -eq $process.Id
    foreach ($child in $children) { Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $process.Id -Force
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

function Invoke-Migration {
    $migrations = @(dotnet ef migrations list --project $InfrastructureProject --startup-project $ApiProject --no-connect 2>&1 | Where-Object { $_ -match 'InitialCreate' })
    if ($migrations.Count -ne 1) { throw 'Exactly one migration named InitialCreate must exist.' }
    dotnet ef database update InitialCreate --project $InfrastructureProject --startup-project $ApiProject
    if ($LASTEXITCODE -ne 0) { throw 'InitialCreate could not be applied.' }
}

Push-Location $ProjectRoot
try {
    switch ($Command) {
        'run' {
            Assert-Command dotnet; Assert-Command pwsh; Import-DevelopmentEnvironment; Start-Postgres
            if (Get-OwnedApiProcess) { throw 'FaceVerification.Api is already running from this script.' }
            $historyExists = docker compose exec -T postgres psql -U $env:POSTGRES_USER -d $env:POSTGRES_DB -tAc "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'public' AND tablename = '__EFMigrationsHistory')" 2>$null
            $initialCreateApplied = if ($historyExists -eq 't') {
                docker compose exec -T postgres psql -U $env:POSTGRES_USER -d $env:POSTGRES_DB -tAc "SELECT EXISTS (SELECT 1 FROM `"__EFMigrationsHistory`" WHERE `"MigrationId`" LIKE '%_InitialCreate')" 2>$null
            } else { 'f' }
            if ($initialCreateApplied -ne 't') { Write-Warning 'The database is not prepared. Run just migrate-database before using verification endpoints.' }
            New-Item -ItemType Directory -Force -Path $PidDirectory | Out-Null
            $process = Start-Process dotnet -ArgumentList @('run', '--project', $ApiProject, '--launch-profile', 'http') -NoNewWindow -PassThru
            Set-Content -LiteralPath $PidFile -Value $process.Id
            $launchSettings = Get-Content -LiteralPath $LaunchSettingsFile -Raw | ConvertFrom-Json
            $apiUrl = $launchSettings.profiles.http.applicationUrl.TrimEnd('/')
            Write-Host ''
            Write-Host 'FaceVerification.Api is starting locally:' -ForegroundColor Green
            Write-Host "  API:     $apiUrl"
            Write-Host "  Scalar:  $apiUrl/scalar/v1"
            Write-Host "  OpenAPI: $apiUrl/openapi/v1.json"
            Write-Host ''
            try { $process.WaitForExit(); exit $process.ExitCode } finally { Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue }
        }
        'down' {
            Assert-Command docker; Stop-OwnedApi
            if (-not $env:POSTGRES_DB) { $env:POSTGRES_DB = 'unused' }
            if (-not $env:POSTGRES_USER) { $env:POSTGRES_USER = 'unused' }
            if (-not $env:POSTGRES_PASSWORD) { $env:POSTGRES_PASSWORD = 'unused' }
            docker compose --project-directory $ProjectRoot down
            if ($LASTEXITCODE -ne 0) { throw 'docker compose down failed.' }
            $running = docker ps --filter 'name=face-verification-' --format '{{.Names}}'
            if ($running) { throw "Project containers remain running: $running" }
        }
        'test' {
            Assert-Command dotnet
            dotnet restore FaceVerification.slnx
            if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
            dotnet build FaceVerification.slnx --no-restore
            if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
            $projects = @(Get-ChildItem -Path tests -Recurse -Filter '*.UnitTests.csproj')
            if ($projects.Count -eq 0) { throw 'No unit test projects were found.' }
            foreach ($project in $projects) {
                dotnet test $project.FullName --no-build --no-restore
                if ($LASTEXITCODE -ne 0) { throw "Unit tests failed: $($project.Name)" }
            }
            Write-Host "All $($projects.Count) unit test project(s) passed."
        }
        'migrate-database' {
            Assert-Command dotnet; Import-DevelopmentEnvironment; Start-Postgres; Invoke-Migration
            Write-Host 'InitialCreate was applied successfully.'
        }
        'recreate-database' {
            Assert-Command dotnet; Import-DevelopmentEnvironment
            if ($env:ASPNETCORE_ENVIRONMENT -ne 'Development') { throw 'Database recreation is only allowed in Development.' }
            Write-Warning "This will permanently delete the development database '$($env:POSTGRES_DB)'."
            if (-not $Force) {
                $confirmation = Read-Host "Type the exact database name '$($env:POSTGRES_DB)' to continue"
                if ($confirmation -cne $env:POSTGRES_DB) { throw 'Database recreation was cancelled.' }
            }
            Stop-OwnedApi; Start-Postgres
            dotnet ef database drop --force --project $InfrastructureProject --startup-project $ApiProject
            if ($LASTEXITCODE -ne 0) { throw 'Database drop failed.' }
            Invoke-Migration
            $applied = docker compose exec -T postgres psql -U $env:POSTGRES_USER -d $env:POSTGRES_DB -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory"'
            if ($applied -notmatch 'InitialCreate') { throw 'InitialCreate was not found in migration history.' }
            Write-Host "Development database '$($env:POSTGRES_DB)' was recreated successfully."
        }
    }
}
finally { Pop-Location }
