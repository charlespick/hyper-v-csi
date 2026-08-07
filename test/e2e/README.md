# Storage e2e

The upstream Kubernetes external storage suite, pointed at this driver. No test
code lives here — `testdriver.yaml` describes what the driver can back and the
suite selects its own tests from that.

```powershell
./run-e2e.ps1 -DryRun   # list what would run; needs no cluster
./run-e2e.ps1           # the gentle first run, against the current context
```

```bash
./run-e2e.sh --dry-run
./run-e2e.sh
```

Read [testing.md](../../testing.md) before the first run against a real cluster.
Two things in particular: the StorageClass here reclaims with `Delete`, so every
volume a run provisions is deleted for real, and the list of what is
deliberately not tested — snapshots, stress, and node failover — is there rather
than here.
