# CSI Spec Implementation

| CSI Surface Call | Implementation Spot | Description | Idempotency Key | |
|---|---|---|---|---|
| GetPluginInfo | Both | Returns the plugin's name and version so Kubernetes can identify it. | N/A | |
| GetPluginCapabilities | Both | Reports which optional CSI features this plugin supports. | N/A | |
| Probe | Both | Health check confirming the plugin is ready to serve requests. | N/A | |
| CreateVolume | Controller | Provisions a new volume and returns its identifier. | Volume name | |
| DeleteVolume | Controller | Removes a previously provisioned volume. | Volume ID | |
| ControllerPublishVolume | Controller | Attaches a volume to a specified node. | Volume ID + node ID | |
| ControllerUnpublishVolume | Controller | Detaches a volume from a specified node. | Volume ID + node ID | |
| ValidateVolumeCapabilities | Controller | Confirms a volume supports the requested access mode and type. | Volume ID (lookup only) | |
| ControllerGetCapabilities | Controller | Reports which controller RPCs this plugin implements. | N/A | |
| ControllerExpandVolume | Controller | Grows a volume's underlying storage. | Volume ID | |
| CreateSnapshot | Controller | Creates a point-in-time snapshot of a volume. | Snapshot name | |
| DeleteSnapshot | Controller | Removes a previously created snapshot. | Snapshot ID | |
| ListSnapshots | Controller | Lists existing snapshots known to the plugin. | Snapshot ID or source volume ID (optional filter, lookup only) | |
| NodeStageVolume | Node | Makes a volume ready for use on a node (format and node-wide mount). | Volume ID + staging target path | |
| NodeUnstageVolume | Node | Undoes NodeStageVolume, releasing the node-wide mount. | Volume ID + staging target path | |
| NodePublishVolume | Node | Bind-mounts a staged volume into a specific pod's path. | Volume ID + target path | |
| NodeUnpublishVolume | Node | Removes a pod's bind-mount of a volume. | Volume ID + target path | |
| NodeGetVolumeStats | Node | Reports usage and capacity stats for a mounted volume. | Volume ID + volume path (lookup only) | |
| NodeExpandVolume | Node | Grows the filesystem on a node after the underlying volume was expanded. | Volume ID + volume path | |
| NodeGetCapabilities | Node | Reports which node RPCs this plugin implements. | N/A | |
| NodeGetInfo | Node | Reports node identity/topology info used for scheduling and attach decisions. | N/A | |
