FROM golang:1.26 AS build
ARG VERSION=dev
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -ldflags "-X github.com/charlespick/hyper-v-csi/csi-driver/internal/driver.Version=${VERSION}" -o /out/hyperv-csi-driver ./cmd/hyperv-csi-driver

FROM gcr.io/distroless/static-debian12
COPY --from=build /out/hyperv-csi-driver /hyperv-csi-driver
ENTRYPOINT ["/hyperv-csi-driver"]
