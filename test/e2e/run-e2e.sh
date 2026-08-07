#!/usr/bin/env bash
#
# Runs the upstream Kubernetes external storage e2e suite against a cluster that
# already has hyperv-csi installed. The Linux/macOS twin of run-e2e.ps1 — same
# flags, same skip lists, same defaults — because the cluster is driven from a
# Windows desktop today and from CI later.
#
# Nothing here installs or configures the driver, the agent, or the cluster. See
# testing.md for what has to be true before this will pass.
#
#   ./run-e2e.sh --dry-run              # list what would run, no cluster needed
#   ./run-e2e.sh                        # the gentle first run
#   ./run-e2e.sh --profile full --procs 4

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
test_driver="$here/testdriver.yaml"

# smoke: the gentle first run — skips.txt plus skips-smoke.txt.
# full:  everything this driver is expected to pass — skips.txt only.
profile=smoke
kubeconfig="${KUBECONFIG:-}"
context=""
kubernetes_version=""
procs=1
focus=""
extra_skips=()
artifacts_dir=""
dry_run=false
keep_namespaces_on_failure=false
allowed_not_ready_nodes=0
timeout=4h
no_download=false
extra_args=()

# The header comment above, minus the shebang, is the help text.
usage() {
	awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "${BASH_SOURCE[0]}"
	exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
	case "$1" in
	--profile) profile="$2"; shift 2 ;;
	--kubeconfig) kubeconfig="$2"; shift 2 ;;
	--context) context="$2"; shift 2 ;;
	--kubernetes-version) kubernetes_version="$2"; shift 2 ;;
	--procs) procs="$2"; shift 2 ;;
	--focus) focus="$2"; shift 2 ;;
	--skip) extra_skips+=("$2"); shift 2 ;;
	--artifacts-dir) artifacts_dir="$2"; shift 2 ;;
	--timeout) timeout="$2"; shift 2 ;;
	--allowed-not-ready-nodes) allowed_not_ready_nodes="$2"; shift 2 ;;
	--dry-run) dry_run=true; shift ;;
	--keep-namespaces-on-failure) keep_namespaces_on_failure=true; shift ;;
	--no-download) no_download=true; shift ;;
	-h | --help) usage 0 ;;
	--) shift; extra_args=("$@"); break ;;
	*) echo "unknown flag: $1" >&2; usage 1 ;;
	esac
done

case "$profile" in
smoke | full) ;;
*) echo "--profile must be smoke or full, got $profile" >&2; exit 1 ;;
esac

kubectl_args=()
[[ -n "$kubeconfig" ]] && kubectl_args+=("--kubeconfig=$kubeconfig")
[[ -n "$context" ]] && kubectl_args+=("--context=$context")

# The cluster's own version, so the suite matches what it is testing. Falls back
# to the published stable release when there is no cluster to ask, which is the
# --dry-run case.
resolve_version() {
	if [[ -n "$kubernetes_version" ]]; then
		echo "$kubernetes_version"
		return
	fi

	local version
	version="$(kubectl "${kubectl_args[@]}" version -o json 2>/dev/null |
		sed -n 's/.*"gitVersion": *"\(v[^"]*\)".*/\1/p' | tail -n 1 || true)"
	if [[ -n "$version" ]]; then
		echo "$version"
		return
	fi

	version="$(curl -fsSL https://dl.k8s.io/release/stable.txt)"
	echo "Could not read the cluster's version; falling back to stable, $version." >&2
	echo "$version"
}

sha512_of() {
	if command -v sha512sum >/dev/null; then
		sha512sum "$1" | cut -d' ' -f1
	else
		shasum -a 512 "$1" | cut -d' ' -f1
	fi
}

# Downloads and verifies the kubernetes-test tarball, once, into a gitignored
# cache. Echoes the directory holding e2e.test and ginkgo.
resolve_test_binaries() {
	local version="$1"
	local os arch cache bin_dir name url tarball expected actual

	case "$(uname -s)" in
	Linux) os=linux ;;
	Darwin) os=darwin ;;
	*) echo "unsupported OS $(uname -s); use run-e2e.ps1 on Windows" >&2; exit 1 ;;
	esac
	case "$(uname -m)" in
	x86_64 | amd64) arch=amd64 ;;
	aarch64 | arm64) arch=arm64 ;;
	*) echo "unsupported architecture $(uname -m)" >&2; exit 1 ;;
	esac

	cache="$here/.bin/$version"
	bin_dir="$cache/kubernetes/test/bin"
	if [[ -x "$bin_dir/e2e.test" ]]; then
		echo "$bin_dir"
		return
	fi

	if [[ "$no_download" == true ]]; then
		echo "No cached e2e.test for $version under $cache, and --no-download was given." >&2
		exit 1
	fi

	name="kubernetes-test-$os-$arch.tar.gz"
	url="https://dl.k8s.io/$version/$name"
	mkdir -p "$cache"
	tarball="$cache/$name"

	echo "Downloading $url" >&2
	curl -fsSL "$url" -o "$tarball"
	expected="$(curl -fsSL "$url.sha512" | tr -d '[:space:]')"

	# The tarball is an executable this script is about to run, so the
	# published checksum is checked rather than trusted to the transport.
	actual="$(sha512_of "$tarball")"
	if [[ "$actual" != "$expected" ]]; then
		rm -f "$tarball"
		echo "SHA512 mismatch for $name" >&2
		echo "  expected $expected" >&2
		echo "  actual   $actual" >&2
		exit 1
	fi

	tar -xzf "$tarball" -C "$cache"
	rm -f "$tarball"
	echo "$bin_dir"
}

# One regex per non-comment line. See skips.txt for the format and the rule that
# every line carries its reason.
read_patterns() {
	local path="$1"
	[[ -f "$path" ]] || { echo "Missing skip list $path" >&2; exit 1; }
	sed -e 's/[[:space:]]*$//' -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$path"
}

version="$(resolve_version)"
bin_dir="$(resolve_test_binaries "$version")"

# Read back rather than duplicated, so the driver name lives in exactly one
# place on this side of the fence. DriverInfo.Name is the only key at this
# indentation in testdriver.yaml.
driver_name="$(sed -n 's/^  Name:[[:space:]]*\(.*[^[:space:]]\)[[:space:]]*$/\1/p' "$test_driver" | head -n 1)"
if [[ -z "$driver_name" ]]; then
	echo "Could not read DriverInfo.Name from $test_driver" >&2
	exit 1
fi

if [[ -z "$focus" ]]; then
	# Dots escaped so they match themselves and nothing else. A CSI driver name
	# is limited to alphanumerics, '-', '.' and '_', so '.' is the only regex
	# metacharacter that can appear in one.
	focus="External.Storage.*$(printf '%s' "$driver_name" | sed 's/[.]/\\./g')"
fi

skips=()
while IFS= read -r line; do skips+=("$line"); done < <(read_patterns "$here/skips.txt")
if [[ "$profile" == smoke ]]; then
	while IFS= read -r line; do skips+=("$line"); done < <(read_patterns "$here/skips-smoke.txt")
fi
skips+=("${extra_skips[@]+"${extra_skips[@]}"}")

if [[ -z "$artifacts_dir" ]]; then
	artifacts_dir="$here/_artifacts/$profile-$(date +%Y%m%d-%H%M%S)"
fi
mkdir -p "$artifacts_dir"
artifacts_dir="$(cd "$artifacts_dir" && pwd)"

ginkgo_args=(
	"--focus=$focus"
	"--timeout=$timeout"
	"--procs=$procs"
	# --junit-report has to stay a bare filename: Ginkgo resolves it against
	# --output-dir, and an absolute Windows path gets joined onto the suite's
	# directory rather than recognised as absolute. Same flags on both scripts.
	"--output-dir=$artifacts_dir"
	"--junit-report=junit.xml"
)
for pattern in "${skips[@]}"; do ginkgo_args+=("--skip=$pattern"); done
if [[ "$dry_run" == true ]]; then ginkgo_args+=(--dry-run -v); fi

# --storage.testdriver is read while flags are still being parsed, before
# --repo-root has been applied, so it has to be absolute. The StorageClass path
# inside it resolves against --repo-root and is therefore relative.
e2e_args=(
	"--storage.testdriver=$test_driver"
	"--repo-root=$repo_root"
	# Where e2e.test dumps cluster state on a failure. It writes its own
	# per-process junit_NN.xml here too; the aggregated one to read is Ginkgo's
	# junit.xml next to it.
	"--report-dir=$artifacts_dir"
	# skeleton is the no-cloud-provider provider. It is also what keeps the
	# SSH-dependent tests from trying: they check for a provider that has SSH.
	"--provider=skeleton"
	"--allowed-not-ready-nodes=$allowed_not_ready_nodes"
)
[[ -n "$kubeconfig" ]] && e2e_args+=("--kubeconfig=$kubeconfig")
[[ -n "$context" ]] && e2e_args+=("--context=$context")
[[ "$keep_namespaces_on_failure" == true ]] && e2e_args+=("--delete-namespace-on-failure=false")
e2e_args+=("${extra_args[@]+"${extra_args[@]}"}")

echo
echo "profile    $profile"
echo "driver     $driver_name"
echo "e2e.test   $version ($bin_dir)"
echo "artifacts  $artifacts_dir"
echo "skipping   ${#skips[@]} pattern(s): $(printf '%s | ' "${skips[@]}")"
echo

set +e
"$bin_dir/ginkgo" "${ginkgo_args[@]}" "$bin_dir/e2e.test" -- "${e2e_args[@]}"
exit_code=$?
set -e

echo
echo "JUnit report: $artifacts_dir/junit.xml"
exit "$exit_code"
