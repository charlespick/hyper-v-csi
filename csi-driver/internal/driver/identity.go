package driver

import (
	"context"
	"time"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/types/known/wrapperspb"
)

type identityServer struct {
	csi.UnimplementedIdentityServer
	driver *Driver
}

func (s *identityServer) GetPluginInfo(ctx context.Context, req *csi.GetPluginInfoRequest) (*csi.GetPluginInfoResponse, error) {
	return &csi.GetPluginInfoResponse{
		Name:          DriverName,
		VendorVersion: Version,
	}, nil
}

func (s *identityServer) GetPluginCapabilities(ctx context.Context, req *csi.GetPluginCapabilitiesRequest) (*csi.GetPluginCapabilitiesResponse, error) {
	return &csi.GetPluginCapabilitiesResponse{
		Capabilities: []*csi.PluginCapability{
			{
				Type: &csi.PluginCapability_Service_{
					Service: &csi.PluginCapability_Service{
						Type: csi.PluginCapability_Service_CONTROLLER_SERVICE,
					},
				},
			},
			{
				Type: &csi.PluginCapability_VolumeExpansion_{
					VolumeExpansion: &csi.PluginCapability_VolumeExpansion{
						Type: csi.PluginCapability_VolumeExpansion_ONLINE,
					},
				},
			},
		},
	}, nil
}

// probeBudget bounds the health check. Deliberately far shorter than the
// agent client's own 30s request timeout: Probe is asked whether the plugin can
// serve requests *now*, and an answer that takes half a minute to arrive has
// already answered. The caller's deadline still wins through
// clampToCallerDeadline, the same way it does for a job poll.
const probeBudget = 5 * time.Second

// Probe reports readiness. It is a reachability check against the agent
// endpoint and nothing more: it must not block on a specific Hyper-V host,
// since which host serves an operation is resolved per operation, and a probe
// that consulted one would report the whole driver unready for a single host
// being down.
//
// Reaching the agent is worth checking rather than assuming, because it is the
// only thing every controller RPC depends on, and because the agent authorizes
// in the TLS handshake — so a probe that gets an answer has also confirmed the
// client certificate this driver was deployed with is one the agent pins. That
// is a misconfiguration worth surfacing at startup rather than at the first
// CreateVolume, since the sidecars call Probe before they call anything else.
func (s *identityServer) Probe(ctx context.Context, req *csi.ProbeRequest) (*csi.ProbeResponse, error) {
	// Node mode, where no agent address is configured and no node RPC calls the
	// agent: staging, publishing and stats are all local to the guest. Reporting
	// unready for an unreachable dependency this plugin does not have would
	// block a node plugin that is perfectly able to mount.
	if s.driver.Agent == nil {
		return &csi.ProbeResponse{Ready: wrapperspb.Bool(true)}, nil
	}

	probeCtx, cancel := context.WithTimeout(ctx, clampToCallerDeadline(ctx, probeBudget))
	defer cancel()

	if err := s.driver.Agent.Healthz(probeCtx); err != nil {
		// A probe that stopped because the *caller* gave up says nothing about
		// the agent's health, and reporting it as an unhealthy agent would send
		// an operator looking at the wrong component. Same distinction
		// enqueueFailed draws for a job that never got enqueued.
		if ctxErr := ctx.Err(); ctxErr != nil {
			return nil, status.FromContextError(ctxErr).Err()
		}

		// FAILED_PRECONDITION with the reason attached, rather than a plain
		// ready:false. ProbeResponse carries no message field, so an
		// unready-but-successful answer is silent — and the sidecars log the
		// error from a failed probe while they retry, which is the difference
		// between an operator seeing "connection refused" or "bad certificate"
		// and seeing a provisioner that simply never starts.
		//
		// Retried, not fatal: the agent is a clustered role, and a failover
		// window is an expected transient state rather than a broken driver.
		return nil, status.Errorf(codes.FailedPrecondition,
			"hyperv-csi-agent at %s is not reachable: %v", s.driver.Agent.BaseURL, err)
	}

	return &csi.ProbeResponse{Ready: wrapperspb.Bool(true)}, nil
}
