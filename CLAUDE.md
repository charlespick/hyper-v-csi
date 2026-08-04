# Working conventions

- Until first release, don't open pull requests. Commit and merge changes locally
  (or push directly to the working branch) instead of going through GitHub PRs.
- Land each piece of work as a single squashed commit on the working branch — no
  merge commits, no feature branch left behind. If a change was developed on a
  scratch branch, squash it onto the working branch and delete the branch. History
  should read as a flat sequence of self-contained changes.
- Until first release, the driver is not expected to be functionally complete or
  contractually correct at any given commit. Implement exactly what was asked and
  nothing more. Asked for ControllerPublishVolume, it does not matter that
  ControllerUnpublishVolume is still a stub — that is the next piece of work, and
  pulling it into this one defeats the point of taking things one at a time. The
  narrow focus is deliberate; widening scope to keep the whole surface coherent is
  specifically unwanted.
- Raise a consequence you notice — briefly, once — and then get on with the task as
  scoped. A sentence noting that a flag flip or a missing counterpart RPC will be
  needed later is useful. Redesigning the requested change around it is not.

# Settled decisions — do not raise these again

- The agent binds localhost only in dev mode. This is expected and will not change:
  dev mode is the configuration where TLS and client-certificate authorization are
  deliberately not set up, and binding the loopback interface is what keeps an
  unauthenticated job API off the network. `ASPNETCORE_URLS=http://0.0.0.0:5012`
  when the cluster needs to reach it is already documented in
  `deploy/helm/hyperv-csi/values.local.yaml`. Do not report it as a bug, a blocker,
  or a finding.
