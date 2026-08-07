# e2e findings

Notes from actually running [the storage e2e suite](README.md) against a real
cluster, kept separate from [testing.md](../testing.md) because that document
describes the harness as designed; this one is what happened when it ran.
Append a dated section per run that turns up something worth remembering —
a false failure worth not re-investigating, a real gap worth a follow-up, a
setup step the docs didn't cover. Delete an entry once its finding is fixed,
filed, or folded into testing.md.

## 2026-08-06 — first run with agent auth enabled (smoke profile)

The driver had only ever been run against the agent with
`agent.allowInsecure: true` (`--allow-insecure-agent`, no TLS, no client
cert). This run's purpose was to exercise the real path — TLS + pinned client
certificate — for the first time, end to end through the smoke profile.

*Superseded same day, see the next entry: the auth model this run stood up
(server certificate matched by subject, verified via system CA roots) was
replaced with thumbprint pinning on both sides before the failures below were
fully chased down. `agent.caCertificate`, `Tls:SubjectName`, and the
`ca-certificates` Dockerfile fix mentioned below no longer exist — kept here
only because the *symptoms* (HostFiltering rejecting a bare IP, the port
choice) are still accurate and still worth knowing.*

- **The agent's `HostFiltering` middleware rejects a bare-IP `agent.address`.**
  `Program.cs` restricts the allowed `Host` header to exactly the configured
  hostname (`Tls:HostName` as of the next entry), independent of which
  certificate is presented. Addressing the agent by IP gets back Kestrel's own
  `400 Bad Request - Invalid Hostname` from every sidecar's probe. Worth
  knowing before assuming a TLS or firewall problem: the handshake succeeds
  and the rejection happens one layer up, in HTTP host validation.
  `agent.hostAliases` (`deploy/helm/hyperv-csi/values.yaml`) resolves the name
  from the controller pod when there's no real DNS record for it, as here.

- **Picked the agent's TLS port to match the existing firewall rule, not a new
  one.** The dev host already had an inbound rule for 5012 (the old plaintext
  dev port); reused it for the TLS listener rather than opening 8443, which
  would have needed a new rule. Worth remembering next time a port choice
  seems arbitrary — check what's already allowed through the host firewall
  before picking one.

### Smoke run: 20 passed, 15 failed, 35 of 100 selected specs actually ran

(35-of-100 matches testing.md's note that a dry run's count is an upper bound —
patterns the driver doesn't support get skipped from inside the test body.)

Junit and full log: `_artifacts/smoke-20260806-190157/`.

**~11 failures were a Windows e2e-client artifact, not a driver defect** — see
the next entry, which fixed it by running the suite from a linux/amd64
container instead of native Windows.

**3 volume-expand failures look like a real gap: `pvc.status.allocatedResources`
never updates.** Confirmed still present after the client-OS fix; see the next
entry for the up-to-date failure list. Not investigated further here since
it's a distinct piece of driver work from getting auth-enabled e2e running;
worth its own pass at `external-resizer`'s expectations around
`allocatedResources` / `RecoverVolumeExpansionFailure` status conditions.

**Single-node cluster caveat.** `csidevnode01` is the only schedulable node
here, so `multiVolume` and every cross-node case are untested regardless of
skip lists — the smoke profile already excludes them (`skips-smoke.txt`), so
this run doesn't say anything new about them. It will matter once
`-TestProfile full` runs: the cross-node detach/attach case needs a second
node to actually exercise, not just to avoid a skip.

No leftover namespaces or PVs from the run — the framework's cleanup runs on
failure too, and the `reclaimPolicy: Delete` StorageClass took every VHDX with
it.

## 2026-08-06 — dockerized harness, thumbprint-pinned auth on both sides

Two follow-ups to the run above, done together because the second needed a
clean re-run to verify and the first was already mid-flight.

### Why: a Windows e2e-client sends Linux pods the wrong path separator

The ~11 non-expand failures above all failed the same way — a shell command
exec'd into a Linux test pod with a Windows-style backslash path instead of a
POSIX one, e.g. `"test -d \opt\0"` instead of `test -d /opt/0`, or
`rm -r \test-volume\provisioning-7172` instead of
`rm -r /test-volume/provisioning-7172`. The `e2e.test` binary `run-e2e.ps1`
downloaded was `windows/amd64` (matched to the control machine, not the
cluster), and something in the upstream volume test helpers builds the
in-container path with the client OS's native separator rather than a forward
slash — a client-side artifact of driving e2e from Windows, not a bug in this
repo or upstream's tests when run correctly.

Fix: `run-e2e.ps1` no longer runs `e2e.test` natively. It builds a
linux/amd64 image from the new `test/e2e/docker/Dockerfile` and runs
`run-e2e.sh` — unchanged, and now the *only* implementation — inside it, with
the repo and the resolved kubeconfig bind-mounted in. `run-e2e.sh` itself
needed one addition along the way: the framework's volume/subpath helpers
shell out to a `kubectl` binary by name (not just the Go client library) for
file injection, and neither the `kubernetes-test` tarball nor the minimal
runner image includes one. `resolve_kubectl` in `run-e2e.sh` now fetches it
from `dl.k8s.io`, version-matched and checksummed the same way as
`e2e.test`/`ginkgo`, into the same cache directory.

Net effect: `docker build` + `docker run` are now the only things
`run-e2e.ps1` does natively; whichever OS runs it, the actual test client is
always linux/amd64.

### Why: the CA-trust model for the agent's server certificate was scrapped

Separately, the auth model stood up in the run above — agent server
certificate matched by subject name and verified by the driver via system CA
roots, client certificate pinned by thumbprint — was replaced with thumbprint
pinning on *both* sides: no CA, no chain, on either connection. `TlsOptions`
gained `AllowedThumbprints` (agent picks which store certificate to serve by
pin, same shape as `AuthenticationOptions.AllowedClientCertificateThumbprints`
already had) and dropped subject-based `CertificateSelector` matching
entirely; `SubjectName` was renamed to `HostName`, since matching a
certificate's subject was never its job — Kestrel's Host-header filtering was.
The driver's `agentclient.NewMutualTLS` gained
`serverCertificateThumbprints` and now sets `InsecureSkipVerify` with a
`VerifyPeerCertificate` callback checking the presented certificate's SHA-1
fingerprint (and validity window) against that list — the mirror image of
`ClientCertificateAuthenticator.IsAllowed` on the agent side. One consequence
worth knowing: `csi-driver/Dockerfile`'s `ca-certificates` package, added in
the run above to make CA verification work at all, went back out — nothing in
this container verifies a certificate chain anymore, so nothing needs a trust
store.

### Re-run: 32 passed, 3 failed — every non-expand failure is gone

Both fixes verified together in one re-run (`_artifacts/smoke-<timestamp>/`
under whichever run most recently followed this entry). The 3 remaining
failures are exactly the `allocatedResources` gap from the run above,
unchanged:

- `Verify if offline PVC expansion works`
- `should resize volume when PVC is edited while pod is using it`
- `should resize volume when PVC is edited and the pod is re-created on the
  same node after controller resize is finished`

All three: `ControllerExpandVolume` and `NodeExpandVolume` both report
success, but `pvc.status.allocatedResources` stays `0` instead of the
requested size — one case waited 74s past `NodeExpandVolume` finishing before
the test gave up, so it isn't purely a short test-side timeout racing a slow
update. Still not investigated further here; worth its own pass at
`external-resizer`'s expectations around `allocatedResources` /
`RecoverVolumeExpansionFailure` status conditions, same as noted above.
