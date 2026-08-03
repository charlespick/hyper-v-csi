# CSI Spec Implementation

The "Idempotency Key" column lists the **raw identifier** sent to the agent. The operation type
is never baked into the key — it travels as a separate field, and the agent dedupes in-flight
jobs on the (operation type, idempotency key) pair.

"Status" tracks implementation, not design. **Pending testing** means the code is written and
covered by unit tests but has never run against a real failover cluster or Hyper-V host.

| CSI Surface Call | Implementation Spot | Description | Idempotency Key | Status |
|---|---|---|---|---|
| GetPluginInfo | Both | Returns the plugin's name and version so Kubernetes can identify it. | N/A | Pending testing |
| GetPluginCapabilities | Both | Reports which optional CSI features this plugin supports. | N/A | Over advertising until project is finished |
| Probe | Both | Health check confirming the plugin is ready to serve requests. | N/A | Stub — always reports ready |
| CreateVolume | Controller | Provisions a new volume and returns its identifier. | Volume name | Pending testing |
| DeleteVolume | Controller | Removes a previously provisioned volume. | Volume ID | Not started |
| ControllerPublishVolume | Controller | Attaches a volume to a specified node. | Volume ID + node ID | Not started |
| ControllerUnpublishVolume | Controller | Detaches a volume from a specified node. | Volume ID + node ID | Not started |
| ValidateVolumeCapabilities | Controller | Confirms a volume supports the requested access mode and type. | Volume ID (lookup only) | Not started |
| ControllerGetCapabilities | Controller | Reports which controller RPCs this plugin implements. | N/A | Over advertising until project is finished |
| ControllerExpandVolume | Controller | Grows a volume's underlying storage. | Volume ID | Not started |
| CreateSnapshot | Controller | Creates a point-in-time snapshot of a volume. | Snapshot name | Not started |
| DeleteSnapshot | Controller | Removes a previously created snapshot. | Snapshot ID | Not started |
| ListSnapshots | Controller | Lists existing snapshots known to the plugin. | Snapshot ID or source volume ID (optional filter, lookup only) | Not started |
| NodeStageVolume | Node | Makes a volume ready for use on a node (format and node-wide mount). | Volume ID + staging target path | Not started |
| NodeUnstageVolume | Node | Undoes NodeStageVolume, releasing the node-wide mount. | Volume ID + staging target path | Not started |
| NodePublishVolume | Node | Bind-mounts a staged volume into a specific pod's path. | Volume ID + target path | Not started |
| NodeUnpublishVolume | Node | Removes a pod's bind-mount of a volume. | Volume ID + target path | Not started |
| NodeGetVolumeStats | Node | Reports usage and capacity stats for a mounted volume. | Volume ID + volume path (lookup only) | Not started |
| NodeExpandVolume | Node | Grows the filesystem on a node after the underlying volume was expanded. | Volume ID + volume path | Not started |
| NodeGetCapabilities | Node | Reports which node RPCs this plugin implements. | N/A | Over advertising until project is finished |
| NodeGetInfo | Node | Reports node identity/topology info used for scheduling and attach decisions. | N/A | Pending testing |

**Over advertising until project is finished** means the RPC works — it's a declaration, and
declarations are just constants — but it announces capabilities whose RPCs are still stubs. The
lists describe the finished driver, not today's code, so the sidecars will call things that return
Unimplemented. Either trim each list to what's actually built, or land the missing RPCs, before
running this in a cluster. What each one currently overstates:

- `GetPluginCapabilities` — volume expansion (ONLINE), while both ControllerExpandVolume and
  NodeExpandVolume are stubs. It correctly omits VOLUME_ACCESSIBILITY_CONSTRAINTS.
- `ControllerGetCapabilities` — PUBLISH_UNPUBLISH_VOLUME, EXPAND_VOLUME, CREATE_DELETE_SNAPSHOT,
  and LIST_SNAPSHOTS. Only CREATE_DELETE_VOLUME is half true: CreateVolume works, DeleteVolume
  doesn't.
- `NodeGetCapabilities` — STAGE_UNSTAGE_VOLUME, EXPAND_VOLUME, and GET_VOLUME_STATS, none of
  which are implemented.

**CreateVolume gaps.** StorageClass `parameters` are ignored rather than consumed or rejected, the
access *type* (mount vs block) is not validated, and `volume_context` is left empty.
`VolumeContentSource` returns Unimplemented by design; restore-from-snapshot is a separate slice.
