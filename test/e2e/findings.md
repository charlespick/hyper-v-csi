# e2e findings

Notes from actually running [the storage e2e suite](README.md) against a real
cluster, kept separate from [testing.md](../testing.md) because that document
describes the harness as designed; this one is what happened when it ran.
Append a dated section per run that turns up something worth remembering —
a false failure worth not re-investigating, a real gap worth a follow-up, a
setup step the docs didn't cover. Delete an entry once its finding is fixed,
filed, or folded into testing.md.

## 2026-08-06 — first auth-enabled run, smoke profile: 35 of 35 passing

The driver had only ever been run against the agent with
`agent.allowInsecure: true`. Getting a real TLS + mutual-auth run green took
three rounds, each fixing a distinct false failure or real gap; all three
fixes are permanent and documented where the code lives, so only the pointers
remain here:

- **The e2e client itself has to be linux/amd64.** A windows/amd64 `e2e.test`
  builds in-container exec paths for the Linux test pods with the wrong path
  separator, failing every subPath/exec/write test. `run-e2e.ps1` now runs
  `run-e2e.sh` inside a container instead of natively — see `run-e2e.sh`'s own
  header comment and testing.md's "What is in `test/e2e/`" table.

- **Both TLS certificates are self-signed and thumbprint-pinned, on both
  sides.** No CA on either connection. See README's "TLS and authentication"
  section for the current shape, and `agent.hostAliases` /
  `agent.serverCertificateThumbprints` in `deploy/helm/hyperv-csi/values.yaml`
  for what a deployment without real DNS or a real CA needs to set.

- **The resizer sidecar needs `RecoverVolumeExpansionFailure` explicitly
  enabled**, or `pvc.status.allocatedResources` silently never gets written —
  no error, no log line, it just skips that part of its job. Fixed in
  `deploy/helm/hyperv-csi/templates/controller.yaml`, with the reasoning in
  the comment next to the flag.

**Still open, not something a code fix resolves:** `csidevnode01` is the only
schedulable node in this cluster, so `multiVolume` and every cross-node case
stay untested regardless of skip lists — the smoke profile already excludes
them, so this doesn't block it, but it means `-TestProfile full` needs a
second node to actually exercise the cross-node detach/attach case rather
than just skip it.
