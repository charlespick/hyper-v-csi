package driver

import (
	"context"

	"google.golang.org/grpc"
	"k8s.io/klog/v2"
)

// LoggingInterceptor dumps every RPC's full (secret-redacted) request at
// V(4). It exists so that dump - identical in shape for every RPC, unlike
// each handler's own V(2) entry line, which names the identifying fields
// (volumeId, target, ...) that make it worth reading without turning
// verbosity up - is written once here rather than repeated at the top of
// every handler in controller.go and node.go.
func LoggingInterceptor(ctx context.Context, req any, info *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (any, error) {
	klog.V(4).InfoS(info.FullMethod+": full request", "req", redactedRequest(req))
	return handler(ctx, req)
}
