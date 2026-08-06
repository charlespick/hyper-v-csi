//go:build !linux

package fsstats

import (
	"fmt"
	"runtime"
)

// Statfs is the stand-in that keeps this package, and everything importing it,
// compiling on a developer's Windows or macOS machine. Nothing reaches it in
// production: the node plugin ships in a Linux image (see the Dockerfile) and
// only runs inside a Linux guest, and the controller never calls it at all. It
// returns an error rather than zeroes so that if that ever stops being true,
// the RPC fails loudly instead of reporting an empty disk.
func Statfs(path string) (Stats, error) {
	return Stats{}, fmt.Errorf("filesystem statistics are not available on %s", runtime.GOOS)
}
