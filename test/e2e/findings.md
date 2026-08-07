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

### Getting there needed two fixes and two new chart knobs

Nothing here is smoke-profile-specific; all four apply to any TLS-authenticated
run of this driver, including production.

- **`csi-driver/Dockerfile` was missing `ca-certificates`.** The image builds
  on `debian:12-slim`, which doesn't include it. Without it `/etc/ssl/certs`
  doesn't exist and Go's `x509.SystemCertPool()` is empty, so the driver
  cannot verify *any* server certificate — a self-signed test one or a real
  Let's Encrypt one. This would have blocked production TLS too, not just
  this test. Fixed by adding `ca-certificates` to the image's `apt-get
  install` list.

- **The agent's `HostFiltering` middleware rejects a bare-IP `agent.address`.**
  `Program.cs` restricts the allowed `Host` header to exactly
  `Tls:SubjectName`, and `CertificateSelector` only matches a certificate's CN
  or DNS SANs — never an IP SAN — so `agent.address` has to name the same DNS
  name the certificate was issued for. Addressing the agent by IP gets back
  Kestrel's own `400 Bad Request - Invalid Hostname` from every sidecar's
  probe. Worth knowing before assuming a TLS or firewall problem: the
  handshake succeeds and the rejection happens one layer up, in HTTP host
  validation.

- **Added `agent.caCertificate`** (`deploy/helm/hyperv-csi/values.yaml`): PEM
  content, mounted into the controller and pointed at via `SSL_CERT_FILE`, for
  trusting an agent server certificate that isn't publicly trusted — i.e. this
  self-signed test setup. Empty by default; a real deployment's Let's Encrypt
  certificate needs nothing here.

- **Added `agent.hostAliases`** (same file): standard `Pod.spec.hostAliases`
  shape, for resolving the agent's DNS name from the controller pod when
  there's no real DNS record for it, as here. Empty by default.

- **Picked the agent's TLS port to match the existing firewall rule, not a new
  one.** The dev host already had an inbound rule for 5012 (the old plaintext
  dev port); reused it for the TLS listener rather than opening 8443, which
  would have needed a new rule. Worth remembering next time a port choice
  seems arbitrary — check what's already allowed through the host firewall
  before picking one.

With those in place, a hand-provisioned PVC against the e2e StorageClass
(`reclaimPolicy: Delete`) bound and later deleted for real, confirming the
mutual-TLS path works before spending an hour on the full suite.

### Smoke run: 20 passed, 15 failed, 35 of 100 selected specs actually ran

(35-of-100 matches testing.md's note that a dry run's count is an upper bound —
patterns the driver doesn't support get skipped from inside the test body.)

Junit and full log: `_artifacts/smoke-20260806-190157/`.

**~11 failures are a Windows e2e-client artifact, not a driver defect.** Every
one fails on a shell command exec'd into a Linux test pod with Windows-style
backslash paths instead of POSIX ones — e.g.

```
"test -d \opt\0" should succeed, but failed with exit code 1
rm -r \test-volume\provisioning-7172: No such file or directory
```

The `e2e.test` binary `run-e2e.ps1` downloads is `windows/amd64` (matched to
the control machine, not the cluster), and something in the upstream volume
test helpers builds the in-container path with the OS-native separator instead
of a forward slash. `\opt\0` is not `/opt/0` to a POSIX shell. This hit every
`subPath` test, `should store data`, `should allow exec of files on the
volume`, and two of the pod-based `volume-expand` cases. Not investigated
further — it's upstream `kubernetes/kubernetes` test-framework behavior when
driven from a Windows client against a Linux cluster, outside this repo. If it
matters later, the fix would be running `e2e.test` from a Linux/WSL control
machine instead of native Windows; testing.md's build steps don't need to
change either way, since the binaries are already picked by cluster OS/arch,
not the driver's.

**3 volume-expand failures look like a real gap: `pvc.status.allocatedResources`
never updates.** `Verify if offline PVC expansion works` and `should resize
volume when PVC is edited while pod is using it` / `...and the pod is
re-created on the same node...` all fail the same way — `ControllerExpandVolume`
and `NodeExpandVolume` both report success (confirmed in the driver's own
CSI-level tests and in earlier manual runs per `CSI Spec.md`), but the PVC's
`status.allocatedResources` stays `0` instead of the requested size. One case
waited 74s past `NodeExpandVolume` finishing before failing, so it isn't
purely a race with a short test-side timeout. Not a Windows-client artifact —
no backslash-path exec involved. Not investigated further here since it's a
distinct piece of driver work from getting auth-enabled e2e running; worth its
own pass at `external-resizer`'s expectations around `allocatedResources` /
`RecoverVolumeExpansionFailure` status conditions.

**Single-node cluster caveat.** `csidevnode01` is the only schedulable node
here, so `multiVolume` and every cross-node case are untested regardless of
skip lists — the smoke profile already excludes them (`skips-smoke.txt`), so
this run doesn't say anything new about them. It will matter once
`-TestProfile full` runs: the cross-node detach/attach case needs a second
node to actually exercise, not just to avoid a skip.

No leftover namespaces or PVs from the run — the framework's cleanup runs on
failure too, and the `reclaimPolicy: Delete` StorageClass took every VHDX with
it.
