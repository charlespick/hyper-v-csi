//go:build linux

package fsstats

import "golang.org/x/sys/unix"

// Statfs reads path's filesystem accounting through statfs(2).
//
// path has to be inside the mount being measured, not merely name it: statfs
// answers for whatever filesystem backs the path it is given, so calling it on
// a directory that is not actually a mount point cheerfully reports the node's
// root filesystem instead. Confirming the mount is the caller's job — see
// nodeServer.volumeStats, which does exactly that first.
func Statfs(path string) (Stats, error) {
	var fs unix.Statfs_t
	if err := unix.Statfs(path, &fs); err != nil {
		return Stats{}, err
	}

	// Bsize is the transfer block size; on every filesystem Linux reports
	// through statfs it is also the unit Blocks/Bfree/Bavail are counted in.
	blockSize := int64(fs.Bsize)

	return Stats{
		TotalBytes: int64(fs.Blocks) * blockSize,
		// Bfree, not Bavail: used is what the filesystem has actually handed
		// out, which includes the root-reserved blocks Bavail holds back.
		UsedBytes:      int64(fs.Blocks-fs.Bfree) * blockSize,
		AvailableBytes: int64(fs.Bavail) * blockSize,

		TotalInodes: int64(fs.Files),
		UsedInodes:  int64(fs.Files - fs.Ffree),
		FreeInodes:  int64(fs.Ffree),
	}, nil
}
