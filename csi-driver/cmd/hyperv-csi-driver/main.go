// Command hyperv-csi-driver is the in-cluster CSI plugin. It runs as the
// same binary in both controller and node mode (see design.md's
// "csi-controller / csi-node (Go)" diagram); --mode selects which gRPC
// servers get registered on the CSI unix socket.
package main

import (
	"context"
	"flag"
	"fmt"
	"net"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/rest"
	"k8s.io/klog/v2"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/driver"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodediscovery"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodefencing"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodeidentity"
)

// nodeIdentityPollInterval is how often a starting node plugin re-reads its
// Node object for the VM ID the controller records. Discovery is one agent
// round trip after the node registers, so a short interval gets the plugin
// serving promptly; it is one keyed GET per node per interval, only while
// starting.
const nodeIdentityPollInterval = 2 * time.Second

func main() {
	klog.InitFlags(nil)
	defer klog.Flush()

	var (
		endpoint = flag.String("endpoint", "unix:///csi/csi.sock", "CSI endpoint")
		mode     = flag.String("mode", "node",
			"driver mode: controller, node, or node-identity (wait until this node's VM ID is recorded on its Node object, then exit - the node DaemonSet's init container)")
		nodeID   = flag.String("node-id", "",
			"node ID override. In node mode this is the Hyper-V VM ID the controller records on this node's Node object when unset, which is the supported configuration")
		nodeName = flag.String("node-name", os.Getenv("NODE_NAME"),
			"name of the Kubernetes Node this plugin runs on, whose recorded VM ID it reports. Defaults to $NODE_NAME. Node mode only")
		agentAddress = flag.String("agent-address", "", "hyperv-csi-agent base URL (required in controller mode)")

		// Off by default, and deliberately so - see Driver.AllowMissingDiskID.
		// This exists to let a PV created before resolve github issue 30's
		// disk ID check shipped keep staging across an upgrade (kubelet
		// replays its persisted, disk-ID-less volume_context), rather than
		// failing NodeStageVolume with InvalidArgument, until the PV is
		// replaced by one whose volume_context carries the disk ID. Node
		// mode only.
		allowMissingDiskID = flag.Bool("allow-missing-disk-id", false,
			"stage a volume whose volume context has no disk id by trusting the controller/LUN slot alone, skipping the check that the resolved disk is actually this volume's own VHDX. Migration escape hatch for PVs created before that check existed; leave this off once every PV has been replaced. Node mode only")

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
			klog.Fatal("--agent-address is required in controller mode")
		}
		if *allowMissingDiskID {
			klog.Fatal("--allow-missing-disk-id is a node-mode flag; the controller server does not run NodeStageVolume")
		}
	case "node", "node-identity":
		if *nodeFencing {
			klog.Fatal("--node-fencing is a controller-mode flag; the node server does not watch Node objects")
		}
	default:
		klog.Fatalf("invalid --mode %q: must be \"controller\", \"node\" or \"node-identity\"", *mode)
	}

	// Kubelet stops the container with SIGTERM. Established here, ahead of
	// everything that can block, because the node plugin's wait for its VM ID
	// below and the controller's background loops all have to stop on it, as
	// well as the gRPC server.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// Both modes talk to Kubernetes for node identity: the controller records
	// each node's VM ID on its Node object, and the node plugin reads its own
	// back. In-cluster config is what a pod under its own ServiceAccount uses,
	// the same way every sidecar in this chart talks to the API server. The
	// one configuration that needs no client is a node plugin given --node-id.
	var kubeClient kubernetes.Interface
	if *mode == "controller" || *nodeID == "" {
		config, err := rest.InClusterConfig()
		if err != nil {
			klog.Fatalf("building in-cluster Kubernetes config: %v", err)
		}
		kubeClient, err = kubernetes.NewForConfig(config)
		if err != nil {
			klog.Fatalf("building Kubernetes client: %v", err)
		}
	}

	if *mode == "node" || *mode == "node-identity" {
		// The node's identity is its Hyper-V VM ID. Reported verbatim by
		// NodeGetInfo, recorded by kubelet in the CSINode object, and handed
		// back to ControllerPublishVolume by external-attacher, which is what
		// lets the agent resolve a node to a VM by identity rather than by
		// matching names.
		//
		// The guest cannot learn it from firmware - only its SMBIOS UUID - so
		// the controller discovers it from that UUID and records it on the
		// Node. This waits for that record, and accepts it only when it was
		// discovered from this VM's own product_uuid. No fallback to the node
		// name: that would let a node register before its identity is known
		// and attach against whatever VM happens to share its name.
		//
		// In the chart, the long wait happens in an init container running
		// --mode=node-identity, which exits once the record is there. Waiting
		// there rather than here is what keeps the sidecars from starting
		// early: node-driver-registrar gives up on a CSI socket that has not
		// appeared within a fixed 30 seconds and would crash-loop through any
		// longer wait, then sit out its backoff after the identity arrived.
		// This container repeats the same check, which by then passes at once.
		if *nodeID == "" {
			if *nodeName == "" {
				klog.Fatal("--node-name (or $NODE_NAME from the downward API) is required to read this node's recorded VM ID")
			}

			productUUID, err := nodeidentity.ReadProductUUID(nodeidentity.DefaultProductUUIDPath)
			if err != nil {
				klog.Fatal(err)
			}

			klog.InfoS("waiting for this node's Hyper-V VM ID to be recorded on its Node object",
				"node", *nodeName, "productUUID", productUUID)
			id, err := nodeidentity.WaitForVMID(ctx, nodeidentity.Nodes(kubeClient), *nodeName, productUUID, nodeIdentityPollInterval)
			if err != nil {
				// The only way out of the wait is ctx, so this is a stop
				// signal arriving before the node was identified - an
				// ordinary shutdown, not a failure.
				klog.InfoS("stopped while waiting for this node's Hyper-V VM ID", "reason", err)
				return
			}

			klog.InfoS("node identity read from the Node object", "node", *nodeName, "vmId", id)
			*nodeID = id
		} else {
			klog.Warningf("--node-id %q was set explicitly, bypassing this node's VM ID recorded by discovery", *nodeID)
		}

		if *mode == "node-identity" {
			return
		}
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
			klog.Fatal(err)
		}
	}

	d := driver.New(*nodeID, agent, *allowMissingDiskID)

	if *mode == "controller" {
		// Unconditional, unlike fencing: without a recorded VM ID no node
		// plugin finishes starting, so nothing could attach anywhere.
		discoverer, err := nodediscovery.New(kubeClient, agent)
		if err != nil {
			klog.Fatal(err)
		}

		go func() {
			// Like fencing below, a watch that cannot be set up is logged
			// loudly rather than taking down the CSI RPCs, which keep serving
			// the nodes that are already identified.
			if err := discoverer.Run(ctx); err != nil && ctx.Err() == nil {
				klog.Errorf("node discovery stopped; new nodes will not be identified until this pod restarts: %v", err)
			}
		}()
	}

	if *nodeFencing {
		fencer, err := nodefencing.New(nodefencing.Config{
			KubeClient:    kubeClient,
			ClusterStates: agent,
			DriverName:    driver.DriverName,
			GracePeriod:   nodeFencingGracePeriod,
			PollInterval:  nodeFencingPollInterval,
			Confirmations: nodeFencingConfirmations,
		})
		if err != nil {
			klog.Fatal(err)
		}

		identity, err := leaderElectionIdentity()
		if err != nil {
			klog.Fatalf("node fencing: %v", err)
		}
		if *nodeFencingLeaseNamespace == "" {
			klog.Fatal("node fencing: --node-fencing-lease-namespace is required (or set POD_NAMESPACE from the downward API)")
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
				klog.Warningf("node fencing stopped; unreachable nodes will not be fenced until this pod restarts: %v", err)
			}
		}()
	}

	listener, err := listen(*endpoint)
	if err != nil {
		klog.Fatalf("failed to listen on %s: %v", *endpoint, err)
	}

	server := grpc.NewServer(grpc.UnaryInterceptor(driver.LoggingInterceptor))
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
		klog.Info("shutdown signal received, draining gRPC server")
		server.GracefulStop()
	}()

	klog.InfoS("hyperv-csi-driver starting", "mode", *mode, "endpoint", *endpoint, "version", driver.Version)
	if err := server.Serve(listener); err != nil {
		klog.Fatalf("gRPC server exited: %v", err)
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

		klog.Warningf("talking to %s without TLS or a client certificate", address)
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
