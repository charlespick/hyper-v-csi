BINARY := hyperv-csi-driver
VERSION ?= dev

.PHONY: build test test-race vet fmt

build:
	CGO_ENABLED=0 go build -ldflags "-X github.com/charlespick/hyper-v-csi/csi-driver/internal/driver.Version=$(VERSION)" -o bin/$(BINARY) ./cmd/hyperv-csi-driver

test:
	go test ./...

# Separate from `test` because the race detector needs cgo, and cgo needs a C
# toolchain that the Windows box this driver is developed on does not have -
# `go test -race` there fails outright with "requires cgo" rather than merely
# running slower. Keeping it as its own target means `make test` stays usable
# during development while CI, which runs on Linux, still gets the check.
#
# It is the node fencing package that makes this worth a target of its own.
# That package decides when to force-delete a node's pods and detach its
# disks, it reaches that decision from state touched by informer callbacks,
# reconcile workers and the leader lifecycle at once, and every concurrency
# defect found in it so far was found by a human reading the code rather than
# by anything that could fail a build. This is the thing that fails the build.
test-race:
	CGO_ENABLED=1 go test -race ./...

vet:
	go vet ./...

fmt:
	gofmt -l .
