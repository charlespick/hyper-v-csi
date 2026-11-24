FROM golang:1.26.11-alpine3.23@sha256:23f198931d4bd83b4f5a613bd75656257ae8bf832dcbc2db80e1050b5219ca2a AS build
ARG VERSION=dev
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -ldflags "-X github.com/charlespick/hyper-v-csi/csi-driver/internal/driver.Version=${VERSION}" -o /out/hyperv-csi-driver ./cmd/hyperv-csi-driver

# Not distroless/static: the node plugin's k8s.io/mount-utils shells out to
# blkid, mkfs.<fstype>, mount, and umount to stage/unstage a volume, none of
# which a static distroless image has.
FROM debian:13.7-slim@sha256:a72b4433de1cf7d6bae0c4cee74d0547f58601a643b3375f070c51600d63b6b2
RUN apt-get update && apt-get install -y --no-install-recommends \
        e2fsprogs \
        util-linux \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/hyperv-csi-driver /hyperv-csi-driver
ENTRYPOINT ["/hyperv-csi-driver"]
