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
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/rest"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/driver"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/hypervkvp"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodefencing"
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

		// Off by default, and deliberately so. This mechanism force-deletes
		// pods and detaches disks on the strength of a WMI read, which is
		// cluster consensus rather than a hardware guarantee; opting in is an
		// operator saying their deployment's fencing story is good enough for
		// that. Controller mode only.
		nodeFencing = flag.Bool("node-fencing", false,
			"watch for unreachable nodes and, once Windows Failover Clustering confirms their VM is not running, apply node.kubernetes.io/out-of-service=nodeshutdown:NoExecute so their pods can be force-deleted and their disks detached. Controller mode only")
		nodeFencingGracePeriod = flag.Duration("node-fencing-grace-period", nodefencing.DefaultGracePeriod,
			"how long a node must carry the unreachable taint before the cluster is asked about it at all. Kubernetes applies that taint within roughly 40s of NotReady, which an ordinary guest reboot also produces")
		nodeFencingPollInterval = flag.Duration("node-fencing-poll-interval", nodefencing.DefaultPollInterval,
			"how often to ask the cluster about nodes that are past their grace period")
		nodeFencingConfirmations = flag.Int("node-fencing-confirmations", nodefencing.DefaultConfirmations,
			"how many consecutive confirmed-not-running readings are required before fencing. Any other reading, including an error, resets the count to zero")
		nodeFencingLeaseNamespace = flag.String("node-fencing-lease-namespace", os.Getenv("POD_NAMESPACE"),
			"namespace holding the node-fencing leader election Lease. Defaults to $POD_NAMESPACE")
		nodeFencingLeaseName = flag.String("node-fencing-lease-name", nodefencing.DefaultLeaseName,
			"name of the node-fencing leader election Lease")
	)
	var agentServerCertThumbprints stringSliceFlag
	flag.Var(&agentServerCertThumbprints, "agent-server-cert-thumbprint",
		"SHA-1 thumbprint of a certificate the agent's own (self-signed) server certificate is allowed to match. "+
			"Repeatable; list two during a rotation. Required alongside --agent-client-cert")
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
		if *nodeFencing {
			log.Fatal("--node-fencing is a controller-mode flag; the node server has no Kubernetes client and does not watch Node objects")
		}
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
		agent, err = buildAgentClient(*agentAddress, *agentClientCert, *agentClientKey, agentServerCertThumbprints, *allowInsecureAgent)
		if err != nil {
			log.Fatal(err)
		}
	}

	// Only controller mode needs it: ControllerExpandVolume is the one RPC
	// that has to ask Kubernetes something CSI's own request does not carry,
	// which node (if any) currently has a volume attached. In-cluster config
	// is what a pod running under its own ServiceAccount uses, the same way
	// every sidecar in this chart already talks to the API server.
	var kubeClient kubernetes.Interface
	if *mode == "controller" {
		config, err := rest.InClusterConfig()
		if err != nil {
			log.Fatalf("building in-cluster Kubernetes config: %v", err)
		}
		kubeClient, err = kubernetes.NewForConfig(config)
		if err != nil {
			log.Fatalf("building Kubernetes client: %v", err)
		}
	}

	d := driver.New(*nodeID, agent, kubeClient)

	// Kubelet stops the container with SIGTERM. Established here rather than
	// just before Serve because the node-fencing controller below shares it:
	// one signal has to stop both the gRPC server and every background loop.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if *nodeFencing {
		fencer, err := nodefencing.New(nodefencing.Config{
			KubeClient:    kubeClient,
			ClusterStates: agent,
			DriverName:    driver.DriverName,
			GracePeriod:   *nodeFencingGracePeriod,
			PollInterval:  *nodeFencingPollInterval,
			Confirmations: *nodeFencingConfirmations,
		})
		if err != nil {
			log.Fatal(err)
		}

		identity, err := leaderElectionIdentity()
		if err != nil {
			log.Fatalf("node fencing: %v", err)
		}
		if *nodeFencingLeaseNamespace == "" {
			log.Fatal("node fencing: --node-fencing-lease-namespace is required (or set POD_NAMESPACE from the downward API)")
		}

		go func() {
			// Losing the lease or failing to elect is not fatal to the CSI
			// RPCs, which keep serving; it only means nothing is fencing.
			// Logged loudly rather than taking the whole controller down,
			// since a driver that cannot fence is still a driver that can
			// provision, attach and snapshot.
			err := fencer.Run(ctx, nodefencing.LeaderElectionOptions{
				Namespace: *nodeFencingLeaseNamespace,
				LeaseName: *nodeFencingLeaseName,
				Identity:  identity,
			})
			if err != nil && ctx.Err() == nil {
				log.Printf("WARNING: node fencing stopped; unreachable nodes will not be fenced until this pod restarts: %v", err)
			}
		}()
	}

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

	// Drain in-flight RPCs instead of dying mid-call. Serve returns nil after
	// GracefulStop.
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
func buildAgentClient(address, certificateFile, keyFile string, serverCertThumbprints []string, allowInsecure bool) (*agentclient.Client, error) {
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

	if len(serverCertThumbprints) == 0 {
		return nil, fmt.Errorf(
			"--agent-server-cert-thumbprint is required alongside --agent-client-cert; the agent's server certificate is self-signed, so there is no other way to trust it")
	}

	return agentclient.NewMutualTLS(address, certificateFile, keyFile, serverCertThumbprints)
}

// leaderElectionIdentity names this replica in the fencing lease. $POD_NAME
// from the downward API is the right answer in the deployment; the hostname is
// the same string in practice and covers running the binary outside a pod.
// Two replicas sharing an identity would both believe they hold the lease, so
// this refuses to guess rather than defaulting to something non-unique.
func leaderElectionIdentity() (string, error) {
	if name := os.Getenv("POD_NAME"); name != "" {
		return name, nil
	}

	hostname, err := os.Hostname()
	if err != nil {
		return "", fmt.Errorf("no $POD_NAME and the hostname is unreadable, so this replica has no unique identity for leader election: %w", err)
	}

	return hostname, nil
}

// stringSliceFlag implements flag.Value so --agent-server-cert-thumbprint can
// be repeated, matching the shape of the agent's own AllowedClientCertificateThumbprints
// config array.
type stringSliceFlag []string

func (s *stringSliceFlag) String() string { return strings.Join(*s, ",") }

func (s *stringSliceFlag) Set(value string) error {
	*s = append(*s, value)
	return nil
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
