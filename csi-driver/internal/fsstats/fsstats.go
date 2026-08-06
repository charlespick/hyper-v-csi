// Package fsstats reports a mounted filesystem's capacity and inode
// accounting — the numbers NodeGetVolumeStats hands back to kubelet.
//
// It exists as its own package for one reason: the syscall behind it is
// Linux-only, and confining the build tags here keeps internal/driver
// buildable (and `go vet ./...`-able) on the Windows machines this repo is
// developed on. The node plugin itself only ever runs in a Linux guest.
package fsstats

// Stats is one filesystem's accounting, in the two units CSI asks for.
//
// AvailableBytes and UsedBytes do not sum to TotalBytes, and that is not a
// rounding artifact: available excludes the blocks reserved for root, while
// used counts them as consumed. It is the same convention df reports, and
// reconciling the two would mean picking one of them to misreport.
type Stats struct {
	TotalBytes     int64
	UsedBytes      int64
	AvailableBytes int64

	TotalInodes int64
	UsedInodes  int64
	FreeInodes  int64
}
