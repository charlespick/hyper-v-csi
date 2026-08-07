FROM golang:1.26 AS build
ARG VERSION=dev
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -ldflags "-X github.com/charlespick/hyper-v-csi/csi-driver/internal/driver.Version=${VERSION}" -o /out/hyperv-csi-driver ./cmd/hyperv-csi-driver

# Not distroless/static: the node plugin's k8s.io/mount-utils shells out to
# blkid, mkfs.<fstype>, mount, and umount to stage/unstage a volume, none of
# which a static distroless image has.
FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
        e2fsprogs \
        util-linux \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/hyperv-csi-driver /hyperv-csi-driver
ENTRYPOINT ["/hyperv-csi-driver"]
