//go:build linux

package fsstats

import (
	"os"
	"path/filepath"
	"testing"
)

func TestStatfsReportsAPlausibleFilesystem(t *testing.T) {
	// Whatever backs the test's temp directory, the invariants below hold for
	// any real filesystem, and they are what catches the mistakes this package
	// can actually make: forgetting to multiply by the block size, or reading
	// free where available was meant.
	stats, err := Statfs(t.TempDir())
	if err != nil {
		t.Fatalf("Statfs: %v", err)
	}

	if stats.TotalBytes <= 0 {
		t.Errorf("TotalBytes = %d, want a positive size", stats.TotalBytes)
	}
	if stats.UsedBytes < 0 || stats.UsedBytes > stats.TotalBytes {
		t.Errorf("UsedBytes = %d, want it within 0..%d", stats.UsedBytes, stats.TotalBytes)
	}
	if stats.AvailableBytes < 0 || stats.AvailableBytes > stats.TotalBytes {
		t.Errorf("AvailableBytes = %d, want it within 0..%d", stats.AvailableBytes, stats.TotalBytes)
	}
	// A byte count that came back as a raw block count would be off by the
	// block size, which is at least 512 on anything Linux mounts.
	if stats.TotalBytes < 512 {
		t.Errorf("TotalBytes = %d, too small to have been scaled by the block size", stats.TotalBytes)
	}

	// Some filesystems (tmpfs before it is written to, btrfs) report no inode
	// limit at all, so a zero total is legitimate; the accounting still has to
	// be self-consistent.
	if stats.TotalInodes < 0 || stats.UsedInodes < 0 || stats.FreeInodes < 0 {
		t.Errorf("negative inode counts: %+v", stats)
	}
	if stats.TotalInodes > 0 && stats.UsedInodes+stats.FreeInodes != stats.TotalInodes {
		t.Errorf("used %d + free %d inodes != total %d",
			stats.UsedInodes, stats.FreeInodes, stats.TotalInodes)
	}
}

func TestStatfsFailsOnAPathThatDoesNotExist(t *testing.T) {
	_, err := Statfs(filepath.Join(t.TempDir(), "never-created"))
	if !os.IsNotExist(err) {
		t.Errorf("error = %v, want a not-exist error", err)
	}
}
