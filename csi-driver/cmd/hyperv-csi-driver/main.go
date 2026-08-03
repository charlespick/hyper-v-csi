// Command hyperv-csi-driver is the in-cluster CSI plugin. It runs as the
// same binary in both controller and node mode (see design.md's
// "csi-controller / csi-node (Go)" diagram); --mode selects which gRPC
// servers get registered on the CSI unix socket.
package main

import (
	"flag"
	"fmt"
	"log"
	"net"
	"os"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/driver"
)

func main() {
	var (
		endpoint     = flag.String("endpoint", "unix:///csi/csi.sock", "CSI endpoint")
		mode         = flag.String("mode", "node", "driver mode: controller or node")
		nodeID       = flag.String("node-id", "", "node ID (required in node mode)")
		agentAddress = flag.String("agent-address", "", "hyperv-csi-agent base URL (required in controller mode)")
	)
	flag.Parse()

	if *mode != "controller" && *mode != "node" {
		log.Fatalf("invalid --mode %q: must be \"controller\" or \"node\"", *mode)
	}

	d := driver.New(*nodeID, agentclient.New(*agentAddress))

	listener, err := listen(*endpoint)
	if err != nil {
		log.Fatalf("failed to listen on %s: %v", *endpoint, err)
	}

	server := grpc.NewServer()
	csi.RegisterIdentityServer(server, d.IdentityServer())

	switch *mode {
	case "controller":
		csi.RegisterControllerServer(server, d.ControllerServer())
	case "node":
		csi.RegisterNodeServer(server, d.NodeServer())
	}

	log.Printf("hyperv-csi-driver starting in %s mode on %s", *mode, *endpoint)
	if err := server.Serve(listener); err != nil {
		log.Fatalf("gRPC server exited: %v", err)
	}
}

func listen(endpoint string) (net.Listener, error) {
	proto, addr, err := parseEndpoint(endpoint)
	if err != nil {
		return nil, err
	}
	if proto == "unix" {
		if err := os.Remove(addr); err != nil && !os.IsNotExist(err) {
			return nil, err
		}
	}
	return net.Listen(proto, addr)
}

func parseEndpoint(endpoint string) (proto, addr string, err error) {
	const unixPrefix = "unix://"
	const tcpPrefix = "tcp://"
	switch {
	case len(endpoint) > len(unixPrefix) && endpoint[:len(unixPrefix)] == unixPrefix:
		return "unix", endpoint[len(unixPrefix):], nil
	case len(endpoint) > len(tcpPrefix) && endpoint[:len(tcpPrefix)] == tcpPrefix:
		return "tcp", endpoint[len(tcpPrefix):], nil
	default:
		return "", "", fmt.Errorf("invalid endpoint %q: must start with unix:// or tcp://", endpoint)
	}
}
