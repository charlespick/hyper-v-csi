#Requires -Version 7.0
<#
.SYNOPSIS
Runs the upstream Kubernetes external storage e2e suite against a cluster that
already has hyperv-csi installed.

.DESCRIPTION
Downloads the e2e.test and ginkgo binaries matching the cluster's own
Kubernetes version, builds the skip regex from skips.txt (plus skips-smoke.txt
for the smoke profile), and runs the suite against testdriver.yaml.

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

    [string]$KubeConfig = $env:KUBECONFIG,

    # kubectl/e2e.test context to use. Defaults to the kubeconfig's current one.
    [string]$Context = '',

    # Which e2e.test to run. Defaults to the cluster's own server version, which
    # is what upstream supports: the suite is only guaranteed to match the
    # cluster it shipped with.
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

# Off, so a failing test run comes back as an exit code to pass on rather than a
# PowerShell exception. Every native call below either checks $LASTEXITCODE or
# means to ignore it.
$PSNativeCommandUseErrorActionPreference = $false

$here = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $here '..' '..')).Path
$testDriver = Join-Path $here 'testdriver.yaml'

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

# Downloads and verifies the kubernetes-test tarball for $version, once, into a
# gitignored cache. Returns the directory holding e2e.test and ginkgo.
function Resolve-TestBinaries {
    param([string]$Version)

    $osName = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'darwin' } else { 'linux' }
    $exe = if ($IsWindows) { '.exe' } else { '' }
    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'amd64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture $_" }
    }

    $cache = Join-Path $here '.bin' $Version
    $binDir = Join-Path $cache 'kubernetes' 'test' 'bin'
    if (Test-Path (Join-Path $binDir "e2e.test$exe")) { return $binDir }

    if ($NoDownload) {
        throw "No cached e2e.test for $Version under $cache, and -NoDownload was given."
    }

    $name = "kubernetes-test-$osName-$arch.tar.gz"
    $url = "https://dl.k8s.io/$Version/$name"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $tarball = Join-Path $cache $name

    Write-Host "Downloading $url" -ForegroundColor Cyan
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -UseBasicParsing $url -OutFile $tarball
        $expected = (Invoke-WebRequest -UseBasicParsing "$url.sha512").Content.Trim().ToLower()
    } finally {
        $ProgressPreference = $previous
    }

    # The tarball is an executable this script is about to run, so the published
    # checksum is checked rather than trusted to the transport.
    $actual = (Get-FileHash $tarball -Algorithm SHA512).Hash.ToLower()
    if ($actual -ne $expected) {
        Remove-Item $tarball -Force
        throw "SHA512 mismatch for $name`n  expected $expected`n  actual   $actual"
    }

    tar -xzf $tarball -C $cache
    if ($LASTEXITCODE -ne 0) { throw "Extracting $tarball failed." }
    Remove-Item $tarball -Force

    return $binDir
}

# One regex per non-comment line. See skips.txt for the format and the rule
# that every line carries its reason.
function Read-Patterns {
    param([string]$Path)

    if (-not (Test-Path $Path)) { throw "Missing skip list $Path" }
    Get-Content $Path |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
}

# Read back rather than duplicated, so the driver name lives in exactly one
# place on this side of the fence. DriverInfo.Name is the only key at this
# indentation in testdriver.yaml.
function Get-DriverName {
    $match = Select-String -Path $testDriver -Pattern '^  Name:\s*(\S+)\s*$' | Select-Object -First 1
    if (-not $match) { throw "Could not read DriverInfo.Name from $testDriver" }
    return $match.Matches[0].Groups[1].Value
}

$version = Resolve-KubernetesVersion
$binDir = Resolve-TestBinaries -Version $version
$exeSuffix = if ($IsWindows) { '.exe' } else { '' }
$ginkgo = Join-Path $binDir "ginkgo$exeSuffix"
$e2eTest = Join-Path $binDir "e2e.test$exeSuffix"

$driverName = Get-DriverName
if (-not $Focus) {
    $Focus = 'External.Storage.*' + [regex]::Escape($driverName)
}

$skips = @(Read-Patterns (Join-Path $here 'skips.txt'))
if ($TestProfile -eq 'smoke') {
    $skips += @(Read-Patterns (Join-Path $here 'skips-smoke.txt'))
}
$skips += $Skip

if (-not $ArtifactsDir) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $ArtifactsDir = Join-Path $here '_artifacts' "$TestProfile-$stamp"
}
New-Item -ItemType Directory -Force $ArtifactsDir | Out-Null
$ArtifactsDir = (Resolve-Path $ArtifactsDir).Path

$ginkgoArgs = @(
    "--focus=$Focus"
    "--timeout=$Timeout"
    "--procs=$Procs"
    # --junit-report has to stay a bare filename: Ginkgo resolves it against
    # --output-dir, and an absolute Windows path gets joined onto the suite's
    # directory rather than recognised as absolute.
    "--output-dir=$ArtifactsDir"
    '--junit-report=junit.xml'
)
foreach ($pattern in $skips) { $ginkgoArgs += "--skip=$pattern" }
if ($DryRun) { $ginkgoArgs += @('--dry-run', '-v') }

# --storage.testdriver is read while flags are still being parsed, before
# --repo-root has been applied, so it has to be absolute. The StorageClass path
# inside it resolves against --repo-root and is therefore relative.
$e2eArgs = @(
    "--storage.testdriver=$testDriver"
    "--repo-root=$repoRoot"
    # Where e2e.test dumps cluster state on a failure. It writes its own
    # per-process junit_NN.xml here too; the aggregated one to read is Ginkgo's
    # junit.xml next to it.
    "--report-dir=$ArtifactsDir"
    # skeleton is the no-cloud-provider provider. It is also what keeps the
    # SSH-dependent tests from trying: they check for a provider that has SSH.
    '--provider=skeleton'
    "--allowed-not-ready-nodes=$AllowedNotReadyNodes"
)
if ($KubeConfig) { $e2eArgs += "--kubeconfig=$KubeConfig" }
if ($Context) { $e2eArgs += "--context=$Context" }
if ($KeepNamespacesOnFailure) { $e2eArgs += '--delete-namespace-on-failure=false' }
$e2eArgs += $ExtraArgs

Write-Host ''
Write-Host "profile    $TestProfile" -ForegroundColor Cyan
Write-Host "driver     $driverName"
Write-Host "e2e.test   $version ($binDir)"
Write-Host "artifacts  $ArtifactsDir"
Write-Host "skipping   $($skips.Count) pattern(s): $($skips -join ' | ')"
Write-Host ''

& $ginkgo @ginkgoArgs $e2eTest -- @e2eArgs
$exitCode = $LASTEXITCODE

Write-Host ''
Write-Host "JUnit report: $(Join-Path $ArtifactsDir 'junit.xml')"
exit $exitCode
