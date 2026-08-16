# Workload

A synthetic PostgreSQL workload for exercising Kasten backup/restore against
this driver: continuous, realistic PV IO with no real user traffic, and a
built-in way to tell — from the data itself — when a snapshot was taken and
when a restore picked back up.

## What it is

One StatefulSet, one PVC on the `hyperv-csi` StorageClass, two containers:

| Container | Does |
| --- | --- |
| `postgres` | Plain `postgres:16`, data on the PV |
| `workload` | Two loops against it: a heartbeat insert every 2s, and `pgbench` running rate-limited (20 tps) traffic against a `pgbench -i -s 5` dataset |

`pgbench` alone wouldn't tell you anything about *when* something happened —
its data changes but carries no readable timeline. The `heartbeat` table
(`id, ts, source`) carries that job; `pgbench` supplies the volume and
randomness of a real workload underneath it.

## Deploy

```bash
kubectl apply -f namespace.yaml -f configmap-init.yaml -f statefulset.yaml
kubectl -n csi-workload-test rollout status statefulset/workload-postgres
```

Point Kasten's backup policy at the `csi-workload-test` namespace with
whatever schedule you're testing (e.g. every 24h), same as any other
application.

## Reading a snapshot/restore out of the data

```bash
./verify.sh                      # last 50 heartbeat rows + gap since previous
./verify.sh csi-workload-test 200
```

`gap_since_prev` is normally ~2s. A row where it's much larger marks a point
the writer wasn't running: the row before is roughly when a snapshot was
taken (or, if the pod was simply down, when it stopped), and the row after is
when the — possibly restored — instance resumed. `source` is the pod
hostname, so a restore into a new pod shows up as a `source` change alongside
the gap.

## Notes

- `POSTGRES_PASSWORD` is a fixed value in `statefulset.yaml`. This is a
  disposable test fixture, not a credential worth managing.
- The PVC has no `storageClassName` override beyond `hyperv-csi` — if the
  chart's default StorageClass name changes, update it here too.
- Deleting the namespace tears down the StatefulSet and PVC together; the PVC
  has no `retain` of its own beyond whatever `hyperv-csi`'s reclaim policy is
  set to.
