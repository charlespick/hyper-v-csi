#Requires -Version 7.0
<#
.SYNOPSIS
Runs the upstream Kubernetes external storage e2e suite against a cluster that
already has hyperv-csi installed.

.DESCRIPTION
Orchestrates run-e2e.sh (its own Linux/macOS twin) inside a linux/amd64
container built from docker/Dockerfile, rather than running e2e.test natively
on Windows. That isn't just convenience: a windows/amd64 e2e.test builds
in-container exec paths for the Linux test pods using the client's native
path separator, so several upstream volume test helpers fail every time - a
Linux pod sees `test -d \opt\0` instead of `test -d /opt/0`. Running the
client itself as linux/amd64, whatever OS calls this script, is what avoids
that class of false failure.

The repository and the resolved kubeconfig are bind-mounted into the
container; everything else - downloading e2e.test/ginkgo, building the skip
regex, running the suite - is run-e2e.sh's job, unchanged. This script's own
job is just resolving what run-e2e.sh needs from the Windows side (the
cluster's Kubernetes version, the kubeconfig to mount) and translating
parameters into its flags.

Nothing here installs or configures the driver, the agent, or the cluster. See
testing.md for what has to be true before this will pass.

.EXAMPLE
./run-e2e.ps1 -DryRun
Lists the tests that would run without touching a cluster. Good for checking a
change to the skip lists.

.EXAMPLE
./run-e2e.ps1
The gentle first run: the smoke profile against the current kubectl context.

.EXAMPLE
./run-e2e.ps1 -TestProfile full -Procs 4
Everything the driver is expected to pass, four tests at a time.
#>
[CmdletBinding()]
param(
    # smoke: the gentle first run — skips.txt plus skips-smoke.txt.
    # full:  everything this driver is expected to pass — skips.txt only.
    [ValidateSet('smoke', 'full')]
    [string]$TestProfile = 'smoke',

    # Defaults to $env:KUBECONFIG, then ~/.kube/config - kubectl's own default -
    # rather than leaving it unset: e2e.test's own config loader tries
    # in-cluster config first when given no --kubeconfig at all, which fails
    # outside a pod rather than falling back to the default file the way
    # kubectl does.
    [string]$KubeConfig = $env:KUBECONFIG,

    # kubectl/e2e.test context to use. Defaults to the kubeconfig's current one.
    [string]$Context = '',

    # Which e2e.test to run. Defaults to the cluster's own server version, which
    # is what upstream supports: the suite is only guaranteed to match the
    # cluster it shipped with. Resolved here (not inside the container) so the
    # container never needs kubectl - only e2e.test's own kubeconfig-based
    # client talks to the cluster.
    [string]$KubernetesVersion = '',

    # Tests in flight at once. 1 is deliberate for a first run — see testing.md
    # on why concurrency is the thing this driver is least characterised under.
    [int]$Procs = 1,

    # Overrides the default focus, which is every External Storage test for this
    # driver. Use it to run one test: -Focus 'should store data'.
    [string]$Focus = '',

    # Extra skip regexes, on top of the profile's. For pinning down a failure
    # mid-run, not for silencing something permanently — that belongs in
    # skips.txt with a reason next to it.
    [string[]]$Skip = @(),

    [string]$ArtifactsDir = '',

    # Enumerates the tests that would run and exits. Needs no cluster.
    [switch]$DryRun,

    # Leaves the test namespaces behind when a test fails, so there is something
    # to kubectl describe. They are deleted on success either way.
    [switch]$KeepNamespacesOnFailure,

    [int]$AllowedNotReadyNodes = 0,

    # Ginkgo's ceiling for the whole run, not per test.
    [string]$Timeout = '4h',

    # Skips the download and expects the binaries to already be cached.
    [switch]$NoDownload,

    # Anything after -- is passed through to e2e.test verbatim.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Off, so a failing test run comes back as an exit code to pass on rather than
# a PowerShell exception. The only native calls left are docker build/run, and
# both are checked via $LASTEXITCODE.
$PSNativeCommandUseErrorActionPreference = $false

$here = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $here '..' '..')).Path
$dockerDir = Join-Path $here 'docker'
$image = 'hyperv-csi-e2e-runner:latest'

if (-not $KubeConfig) {
    $default = Join-Path $HOME '.kube' 'config'
    if (Test-Path $default -PathType Leaf) { $KubeConfig = $default }
}

function Invoke-Kubectl {
    param([string[]]$KubectlArgs)

    $prefix = @()
    if ($KubeConfig) { $prefix += "--kubeconfig=$KubeConfig" }
    if ($Context) { $prefix += "--context=$Context" }
    & kubectl @prefix @KubectlArgs
}

# The cluster's own version, so the suite matches what it is testing. Falls back
# to the published stable release when there is no cluster to ask, which is the
# -DryRun case.
function Resolve-KubernetesVersion {
    if ($KubernetesVersion) { return $KubernetesVersion }

    try {
        $json = Invoke-Kubectl @('version', '-o', 'json') 2>$null | ConvertFrom-Json
        if ($json.PSObject.Properties.Name -contains 'serverVersion' -and $json.serverVersion.gitVersion) {
            $version = $json.serverVersion.gitVersion
            Write-Host "Cluster reports Kubernetes $version" -ForegroundColor DarkGray
            return $version
        }
    } catch {
        Write-Warning "Could not read the cluster's version ($($_.Exception.Message))."
    }

    $stable = (Invoke-WebRequest -UseBasicParsing 'https://dl.k8s.io/release/stable.txt').Content.Trim()
    Write-Warning "Falling back to the current stable release, $stable. Pass -KubernetesVersion to pin it."
    return $stable
}

# Translates an absolute Windows path under $repoRoot into the path it appears
# at inside the container, where $repoRoot is mounted at /repo. Throws for
# anything outside the repo - only $repoRoot is mounted, so nothing else is
# reachable from inside the container.
function ConvertTo-ContainerPath {
    param([string]$WindowsPath)

    $resolved = (Resolve-Path $WindowsPath).Path
    if (-not $resolved.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$WindowsPath is outside the repository ($repoRoot); only the repository is mounted into the e2e runner container."
    }

    $relative = $resolved.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
    if ($relative) { return "/repo/$relative" }
    return '/repo'
}

$version = Resolve-KubernetesVersion

if (-not $ArtifactsDir) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $ArtifactsDir = Join-Path $here '_artifacts' "$TestProfile-$stamp"
}
New-Item -ItemType Directory -Force $ArtifactsDir | Out-Null
$ArtifactsDir = (Resolve-Path $ArtifactsDir).Path
$containerArtifactsDir = ConvertTo-ContainerPath $ArtifactsDir

Write-Host ''
Write-Host "Building $image from $dockerDir" -ForegroundColor DarkGray
& docker build --platform linux/amd64 -t $image $dockerDir
if ($LASTEXITCODE -ne 0) { throw "docker build failed with exit code $LASTEXITCODE" }

$dockerArgs = @(
    'run', '--rm'
    '-v', "${repoRoot}:/repo"
    '-w', '/repo/test/e2e'
)

# Skipped, not mounted, for -DryRun with no kubeconfig on disk yet: Ginkgo's
# dry-run mode never executes SynchronizedBeforeSuite, so nothing ever opens
# it. Mounting a path Docker doesn't find would silently bind an empty
# directory there instead, which is worse than just not passing it.
$haveKubeConfig = $KubeConfig -and (Test-Path $KubeConfig -PathType Leaf)
if ($haveKubeConfig) {
    $dockerArgs += @('-v', "${KubeConfig}:/kubeconfig:ro")
} elseif (-not $DryRun) {
    throw "No kubeconfig found (checked -KubeConfig, `$env:KUBECONFIG, and $(Join-Path $HOME '.kube' 'config')). Pass -KubeConfig explicitly, or -DryRun if you don't need a cluster yet."
}

$sh = @('--profile', $TestProfile, '--kubernetes-version', $version, '--procs', "$Procs")
if ($haveKubeConfig) { $sh += @('--kubeconfig', '/kubeconfig') }
if ($Context) { $sh += @('--context', $Context) }
if ($Focus) { $sh += @('--focus', $Focus) }
foreach ($pattern in $Skip) { $sh += @('--skip', $pattern) }
$sh += @('--artifacts-dir', $containerArtifactsDir, '--timeout', $Timeout, '--allowed-not-ready-nodes', "$AllowedNotReadyNodes")
if ($DryRun) { $sh += '--dry-run' }
if ($KeepNamespacesOnFailure) { $sh += '--keep-namespaces-on-failure' }
if ($NoDownload) { $sh += '--no-download' }
if ($ExtraArgs.Count -gt 0) { $sh += @('--') + $ExtraArgs }

Write-Host "profile    $TestProfile" -ForegroundColor Cyan
Write-Host "kubeconfig $(if ($haveKubeConfig) { $KubeConfig } else { '(none - dry run)' })"
Write-Host "artifacts  $ArtifactsDir"
Write-Host ''

# bash explicitly, not ./run-e2e.sh: a Windows bind mount doesn't reliably
# preserve the executable bit, so relying on it would work by accident.
& docker @dockerArgs $image bash ./run-e2e.sh @sh
$exitCode = $LASTEXITCODE

Write-Host ''
Write-Host "JUnit report: $(Join-Path $ArtifactsDir 'junit.xml')"
exit $exitCode
