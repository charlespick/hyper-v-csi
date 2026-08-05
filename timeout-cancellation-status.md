# Timeout/Cancellation Status (Crash Recovery Note)

Date: 2026-08-04
Branch: centralized-agent-design

## Recurring issue to fix

The repeated class of failures is **cooperative cancellation being treated like hard interruption** for host/cluster management calls.

- `CancellationToken` is cooperative only.
- In `System.Management` WMI/RPC calls, once blocked in-flight, token cancellation usually cannot interrupt the call.
- Result: operations can be logically timed out but physically still running/stuck, which can wedge serialized job chains.

## What is already addressed

A partial, substrate-level fix is in progress:

- Added `CimDeadline` with absolute per-operation budget for MI calls.
- Added MI overload of `CimJobs.WaitForCompletion(...)` bounded by deadline.
- Migrated `CimVirtualDiskManager` to MI + deadline.
- Added `Microsoft.Management.Infrastructure` package to service project.
- Updated `CimHyperVHostClient` empty-drive cleanup to call `RemoveResourceSettings` via MI with a bounded `CimDeadline` and host timeout budget.
- Migrated `CimHyperVHostClient` attach/detach `AddResourceSettings` and `RemoveResourceSettings` calls to MI + deadline-bounded job waiting.
- Removed the temporary `System.Management` `CimJobs.WaitForCompletion(...)` overload; service code now uses deadline-bounded MI waiting.
- Updated detach behavior to fail on malformed node identity (fail-closed); cluster/host resolution failures remain surfaced as errors.
- Updated `CSI Spec.md` to match current registry-first + WMI key-lookup VM ownership resolution flow.
- Updated `MsClusterService` owner-node lookup to use MI query options with `CimDeadline` and cancellation.

## What remains (high-level)

1. Continue host-client migration (`CimHyperVHostClient`) for read/query paths still on `System.Management` (active settings lookup, related-instance traversals, and slot/discovery reads).
2. Complete rollback/sweep design beyond bounded cleanup call semantics.
3. Update tests to avoid false confidence from fakes that honor token cancellation in ways real WMI calls do not.
4. Continue auditing docs for timeout/cancellation wording and remaining implementation drift.

## Why this is important

This is not a cosmetic timeout mismatch: in worst cases it can pin per-target job queues and block attach/detach progress for Kubernetes workloads behind retry loops.

## Current status summary

- **Direction is correct** (deadline-based MI substrate exists).
- **Critical path is incomplete** (attach/detach host client and cluster lookup still mostly on legacy blocking behavior).
- Next meaningful milestone is landing the host client migration safely; several other fixes depend on it.
