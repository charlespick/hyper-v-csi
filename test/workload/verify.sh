#!/usr/bin/env bash
# Prints the most recent heartbeat rows with the gap since the previous row.
# gap_since_prev is normally ~2s. A row where it's much larger marks a point
# the writer wasn't running: the row before is roughly when a snapshot was
# taken (or, if the pod was simply down, when it stopped), and the row after
# is when the -- possibly restored -- instance resumed. `source` is the pod
# hostname, so a restore into a new pod shows up as a source change too.
set -euo pipefail

NAMESPACE="${1:-csi-workload-test}"
LIMIT="${2:-50}"

kubectl -n "$NAMESPACE" exec -c postgres statefulset/workload-postgres -- \
  psql -U postgres -d workload -c \
  "SELECT id, ts, source, ts - lag(ts) OVER (ORDER BY id) AS gap_since_prev
   FROM heartbeat ORDER BY id DESC LIMIT $LIMIT;"
