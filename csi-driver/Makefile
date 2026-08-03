BINARY := hyperv-csi-driver
VERSION ?= dev

.PHONY: build test vet fmt

build:
	CGO_ENABLED=0 go build -ldflags "-X github.com/charlespick/hyper-v-csi/csi-driver/internal/driver.Version=$(VERSION)" -o bin/$(BINARY) ./cmd/hyperv-csi-driver

test:
	go test ./...

vet:
	go vet ./...

fmt:
	gofmt -l .
