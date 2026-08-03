FROM golang:1.26 AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -o /out/hyperv-csi-driver ./cmd/hyperv-csi-driver

FROM gcr.io/distroless/static-debian12
COPY --from=build /out/hyperv-csi-driver /hyperv-csi-driver
ENTRYPOINT ["/hyperv-csi-driver"]
