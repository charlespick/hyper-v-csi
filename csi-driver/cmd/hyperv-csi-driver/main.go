// Command hyperv-csi-driver is the in-cluster CSI plugin. It runs as the
// same binary in both controller and node mode (see design.md's
// "csi-controller / csi-node (Go)" diagram); --mode selects which gRPC
// servers get registered on the CSI unix socket.
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/signal"
	"strings"
	"syscall"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/driver"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/hypervkvp"
)

func main() {
	var (
		endpoint = flag.String("endpoint", "unix:///csi/csi.sock", "CSI endpoint")
		mode     = flag.String("mode", "node", "driver mode: controller or node")
		nodeID   = flag.String("node-id", "",
			"node ID override. In node mode this is read from the Hyper-V key-value pools when unset, which is the supported configuration")
		kvpPoolDir = flag.String("kvp-pool-dir", hypervkvp.DefaultPoolDir,
			"directory holding the Hyper-V key-value pool files hv_kvp_daemon maintains")
		agentAddress = flag.String("agent-address", "", "hyperv-csi-agent base URL (required in controller mode)")

		agentClientCert = flag.String("agent-client-cert", "",
			"PEM client certificate presented to the agent, whose fingerprint the agent pins (required in controller mode)")
		agentClientKey = flag.String("agent-client-key", "",
			"PEM private key for --agent-client-cert")
		allowInsecureAgent = flag.Bool("allow-insecure-agent", false,
			"talk to the agent without TLS or a client certificate. Development only: the credentials are what stop anyone who can reach the agent from creating and deleting volumes")
	)
	flag.Parse()

	// Fail fast on mode-specific required flags: an empty node ID otherwise
	// surfaces as a kubelet registration failure far from the actual cause,
	// and an empty agent address as a broken client at the first job.
	switch *mode {
	case "controller":
		if *agentAddress == "" {
			log.Fatal("--agent-address is required in controller mode")
		}
	case "node":
		// The node's identity is its Hyper-V VM ID, which only the guest can
		// learn and only through the host's key-value pools. Reported verbatim
		// by NodeGetInfo, recorded by kubelet in the CSINode object, and handed
		// back to ControllerPublishVolume by external-attacher, which is what
		// lets the agent resolve a node to a VM by identity rather than by
		// matching names.
		//
		// Fatal rather than falling back to the node name: a fallback would let
		// a node whose Data Exchange service is off register anyway and attach
		// against whatever VM happens to share its name.
		if *nodeID == "" {
			id, err := hypervkvp.VirtualMachineID(*kvpPoolDir)
			if err != nil {
				log.Fatalf("resolving this node's Hyper-V VM ID: %v", err)
			}

			log.Printf("node identity resolved from the Hyper-V key-value pools: VM %s", id)
			*nodeID = id
		} else {
			log.Printf("WARNING: --node-id %q was set explicitly, bypassing resolution of this node's identity from the Hyper-V key-value pools", *nodeID)
		}
	default:
		log.Fatalf("invalid --mode %q: must be \"controller\" or \"node\"", *mode)
	}

	// Built in either mode when an address is given. Node mode has no RPC that
	// calls the agent yet, but building the client here rather than leaving it
	// nil means the first one that does gets a working client instead of a nil
	// dereference, and it holds node mode to the same credential rules.
	var agent *agentclient.Client
	if *agentAddress != "" {
		var err error
		agent, err = buildAgentClient(*agentAddress, *agentClientCert, *agentClientKey, *allowInsecureAgent)
		if err != nil {
			log.Fatal(err)
		}
	}

	d := driver.New(*nodeID, agent)

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

	// Kubelet stops the container with SIGTERM; drain in-flight RPCs instead
	// of dying mid-call. Serve returns nil after GracefulStop.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		<-ctx.Done()
		log.Print("shutdown signal received, draining gRPC server")
		server.GracefulStop()
	}()

	log.Printf("hyperv-csi-driver starting in %s mode on %s", *mode, *endpoint)
	if err := server.Serve(listener); err != nil {
		log.Fatalf("gRPC server exited: %v", err)
	}
}

// buildAgentClient refuses to fall back to an unauthenticated connection
// silently. Losing mutual TLS isn't a degraded mode — it removes the only thing
// standing between the agent's job API and anything that can route to it — so
// dropping it has to be something an operator asked for in writing.
func buildAgentClient(address, certificateFile, keyFile string, allowInsecure bool) (*agentclient.Client, error) {
	hasCertificate := certificateFile != "" || keyFile != ""

	if hasCertificate && (certificateFile == "" || keyFile == "") {
		return nil, fmt.Errorf("--agent-client-cert and --agent-client-key must be given together")
	}

	if !hasCertificate {
		if !allowInsecure {
			return nil, fmt.Errorf(
				"--agent-client-cert and --agent-client-key are required in controller mode; pass --allow-insecure-agent only for local development")
		}

		log.Printf("WARNING: talking to %s without TLS or a client certificate", address)
		return agentclient.New(address), nil
	}

	if !strings.HasPrefix(address, "https://") {
		return nil, fmt.Errorf(
			"--agent-address %q must be https:// when a client certificate is configured; over plaintext the certificate proves nothing", address)
	}

	return agentclient.NewMutualTLS(address, certificateFile, keyFile)
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
