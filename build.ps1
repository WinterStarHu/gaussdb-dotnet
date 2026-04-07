$ErrorActionPreference = "Stop"

$target = "Default"
$framework = $null
$apiKey = $null
$noPush = $false
$stable = $false

foreach ($arg in $args) {
    switch -Regex ($arg) {
        '^--target=(.+)$' { $target = $Matches[1] }
        '^--framework=(.+)$' { $framework = $Matches[1] }
        '^--apiKey=(.+)$' { $apiKey = $Matches[1] }
        '^--noPush(?:=(true))?$' { $noPush = $true }
        '^--stable(?:=(true))?$' { $stable = $true }
    }
}

$repoRoot = $PSScriptRoot
$localDotnetExe = Join-Path $repoRoot ".dotnet\dotnet.exe"
$useLocalDotnet = $false

if (Test-Path $localDotnetExe) {
    $dotnetExe = $localDotnetExe
    $useLocalDotnet = $true
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw "dotnet not found. Checked $localDotnetExe and PATH."
    }

    $dotnetExe = $dotnetCommand.Source
}

if ($useLocalDotnet) {
    $env:DOTNET_ROOT = Join-Path $repoRoot ".dotnet"
}
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"
$env:MSBuildEnableWorkloadResolver = "false"
if (-not $env:DOTNET_CLI_HOME) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-cli-home"
}

$solutionPath = Join-Path $repoRoot "GaussDB.slnx"
$packageOutput = Join-Path $repoRoot "artifacts\packages"

$buildProjects = @(
    @{ Path = ".\src\GaussDB.SourceGenerators\GaussDB.SourceGenerators.csproj"; UseFramework = $false; NoDependencies = $false },
    @{ Path = ".\src\GaussDB\GaussDB.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\src\GaussDB.DependencyInjection\GaussDB.DependencyInjection.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\src\GaussDB.GeoJSON\GaussDB.GeoJSON.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\src\GaussDB.Json.NET\GaussDB.Json.NET.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\src\GaussDB.NetTopologySuite\GaussDB.NetTopologySuite.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\src\GaussDB.NodaTime\GaussDB.NodaTime.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\src\GaussDB.OpenTelemetry\GaussDB.OpenTelemetry.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\example\GetStarted\GetStarted.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\test\GaussDB.Benchmarks\GaussDB.Benchmarks.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\test\GaussDB.NativeAotTests\GaussDB.NativeAotTests.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\test\GaussDB.Specification.Tests\GaussDB.Specification.Tests.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\test\GaussDB.Tests\GaussDB.Tests.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\test\GaussDB.DependencyInjection.Tests\GaussDB.DependencyInjection.Tests.csproj"; UseFramework = $true; NoDependencies = $true },
    @{ Path = ".\test\GaussDB.PluginTests\GaussDB.PluginTests.csproj"; UseFramework = $true; NoDependencies = $true }
)

$testProjects = @(
    ".\test\GaussDB.Tests\GaussDB.Tests.csproj",
    ".\test\GaussDB.DependencyInjection.Tests\GaussDB.DependencyInjection.Tests.csproj"
)

$packProjects = @(
    ".\src\GaussDB\GaussDB.csproj"
)

$commonBuildProperties = @(
    "-m:1",
    "-p:NuGetAudit=false"
)

function Write-TaskBanner {
    param(
        [string]$Name,
        [string]$Description,
        [bool]$Executing
    )

    $suffix = if ($Executing) { "executing" } else { "executed" }
    Write-Host "===== Task [$Name] $Description $suffix ======"
}

function Invoke-DotnetCommand {
    param(
        [string[]]$Arguments
    )

    $pretty = ($Arguments | ForEach-Object {
        if ($_ -match '[\s;"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join " "

    Write-Host "Executing command:"
    Write-Host "    $dotnetExe $pretty"
    Write-Host

    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $dotnetExe $pretty"
    }

    Write-Host
}

function Invoke-ProjectBuild {
    param(
        [hashtable]$Project,
        [string]$TargetFramework
    )

    $projectPath = $Project.Path
    $arguments = @("build", $projectPath)
    if ($Project.UseFramework -and -not [string]::IsNullOrWhiteSpace($TargetFramework)) {
        $arguments += @("-f", $TargetFramework)
    }
    if ($Project.NoDependencies) {
        $arguments += "--no-dependencies"
    }
    $arguments += $commonBuildProperties

    try {
        Invoke-DotnetCommand -Arguments $arguments
    }
    catch {
        if (-not $Project.UseFramework -or [string]::IsNullOrWhiteSpace($TargetFramework)) {
            throw
        }

        Write-Host "Retrying build without restore for $projectPath ($TargetFramework)..." -ForegroundColor Yellow
        $retryArguments = @("build", $projectPath, "-f", $TargetFramework, "--no-restore")
        if ($Project.NoDependencies) {
            $retryArguments += "--no-dependencies"
        }
        $retryArguments += $commonBuildProperties
        Invoke-DotnetCommand -Arguments $retryArguments
    }
}

function Get-BuildProjects {
    param([string]$TargetFramework)

    foreach ($project in $buildProjects) {
        if ($TargetFramework -eq "net8.0" -and $project.Path -eq ".\test\GaussDB.NativeAotTests\GaussDB.NativeAotTests.csproj") {
            continue
        }

        $project
    }
}

function Invoke-BuildTarget {
    Write-TaskBanner -Name "build" -Description "build" -Executing $true
    try {
        if ([string]::IsNullOrWhiteSpace($framework)) {
            $solutionArguments = @("build", $solutionPath)
            $solutionArguments += $commonBuildProperties
            Invoke-DotnetCommand -Arguments $solutionArguments
            return
        }

        foreach ($project in Get-BuildProjects -TargetFramework $framework) {
            Invoke-ProjectBuild -Project $project -TargetFramework $framework
        }
    }
    finally {
        Write-TaskBanner -Name "build" -Description "build" -Executing $false
    }
}

function Invoke-TestTarget {
    Write-TaskBanner -Name "test" -Description "dotnet test" -Executing $true
    try {
        Invoke-BuildTarget

        foreach ($project in $testProjects) {
            $args = @(
                "test",
                "--blame",
                "--collect", "XPlat Code Coverage;Format=cobertura,opencover;ExcludeByAttribute=ExcludeFromCodeCoverage,Obsolete,GeneratedCode,CompilerGenerated",
                "--logger", $(if ($env:GITHUB_ACTIONS -eq "true") { "GitHubActions" } else { "console;verbosity=d" }),
                "-v:d"
            )

            if (-not [string]::IsNullOrWhiteSpace($framework)) {
                $args += @("-f", $framework)
            }

            $args += $commonBuildProperties
            $args += $project
            Invoke-DotnetCommand -Arguments $args
        }
    }
    finally {
        Write-TaskBanner -Name "test" -Description "dotnet test" -Executing $false
    }
}

function Invoke-PackTarget {
    Write-TaskBanner -Name "pack" -Description "dotnet pack" -Executing $true
    try {
        Invoke-BuildTarget

        if (Test-Path $packageOutput) {
            Remove-Item -LiteralPath $packageOutput -Recurse -Force
        }
        New-Item -ItemType Directory -Path $packageOutput | Out-Null

        foreach ($project in $packProjects) {
            $args = @("pack", $project, "-o", $packageOutput)
            if ($stable -or -not [string]::IsNullOrEmpty($env:VERSION)) {
                if (-not [string]::IsNullOrEmpty($env:VERSION)) {
                    $args += @("-p", "VersionPrefix=$($env:VERSION)")
                }
            }
            else {
                $args += @("--version-suffix", "preview-$(Get-Date -Format 'yyyyMMdd-HHmmss')")
            }

            $args += $commonBuildProperties

            Invoke-DotnetCommand -Arguments $args
        }

        if ($noPush) {
            Write-Host "Skip push there's noPush specified"
            return
        }

        if ([string]::IsNullOrWhiteSpace($apiKey)) {
            $apiKey = $env:NUGET_API_KEY
        }

        if ([string]::IsNullOrWhiteSpace($apiKey)) {
            Write-Host "Skip push since there's no apiKey found"
            return
        }

        Get-ChildItem -Path $packageOutput -Filter *.nupkg | ForEach-Object {
            Invoke-DotnetCommand -Arguments @(
                "nuget", "push", $_.FullName,
                "-s", "https://api.nuget.org/v3/index.json",
                "-k", $apiKey,
                "--skip-duplicate"
            )
        }
    }
    finally {
        Write-TaskBanner -Name "pack" -Description "dotnet pack" -Executing $false
    }
}

switch ($target) {
    "build" { Invoke-BuildTarget }
    "test" { Invoke-TestTarget }
    "pack" { Invoke-PackTarget }
    "Default" { Invoke-PackTarget }
    default { throw "Unknown target: $target" }
}
