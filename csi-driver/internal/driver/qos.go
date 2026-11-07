package driver

import (
	"context"
	"fmt"
	"sort"
	"strconv"
	"strings"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"k8s.io/apimachinery/pkg/api/resource"
	"k8s.io/klog/v2"
)

// operationModifyVolume is the operationType the agent dispatches on; it must
// match JobDispatcher.ModifyVolume on the .NET side.
const operationModifyVolume = "ModifyVolume"

// The keys a VolumeAttributesClass's parameters may carry. They reach the
// driver as mutable_parameters in CreateVolume and ControllerModifyVolume;
// nothing else in Kubernetes can set them - see docs/storage-qos.md for why
// StorageClass parameters and annotations are not a way in.
const (
	// qosKeyMinIOPS is the reservation, in normalized IOPS (the policy's
	// MinimumIOPS).
	qosKeyMinIOPS = "minIOPS"
	// qosKeyMaxIOPS is the limit, in normalized IOPS (MaximumIOPS).
	qosKeyMaxIOPS = "maxIOPS"
	// qosKeyMaxBandwidth is the bandwidth limit, a Kubernetes quantity read as
	// bytes per second (MaximumIOBandwidth).
	qosKeyMaxBandwidth = "maxBandwidth"
	// qosKeyQoS takes exactly one value, qosNone, and no other key with it.
	qosKeyQoS = "qos"

	// qosNone turns QoS off. It exists because Kubernetes refuses to set a
	// PVC's volumeAttributesClassName back to nil once one has been applied,
	// and a class must have at least one parameter: a class whose only
	// parameter is qos: "none" is the supported way back, and it leaves
	// exactly the Hyper-V state of a volume that never had a class.
	qosNone = "none"
)

// qosSettings is the payload half of QoS, matching QosSettings on the .NET
// side. Every field is the value Hyper-V's Dedicated policy takes as-is; 0
// means no reservation, or no limit. A nil *qosSettings means the volume has
// no QoS at all - no Hyper-V policy - which is a different state from a
// policy whose values are all 0, and one this driver never creates.
type qosSettings struct {
	MinimumIops                    uint64 `json:"minimumIops"`
	MaximumIops                    uint64 `json:"maximumIops"`
	MaximumBandwidthBytesPerSecond uint64 `json:"maximumBandwidthBytesPerSecond"`
}

// key renders the settings for an idempotency key, nil included, so two
// requests for different QoS never dedupe onto one job.
func (q *qosSettings) key() string {
	if q == nil {
		return qosNone
	}
	return fmt.Sprintf("%d,%d,%d", q.MinimumIops, q.MaximumIops, q.MaximumBandwidthBytesPerSecond)
}

// parseQoS turns a VolumeAttributesClass's parameters into the QoS the volume
// should have, or nil for none.
//
// Full replacement, deliberately: a volume's QoS is exactly what its current
// class says, and a key the class leaves out means 0 - no reservation, or no
// limit - not "leave whatever is there". The CSI spec asks plugins not to act
// on an absent key, but Kubernetes always sends the whole of the new class, so
// reading absence as 0 is what makes the volume's QoS a function of its class
// alone, with no history to reconcile.
//
// An empty map is no QoS, which is CreateVolume's case for a PVC with no
// class. ControllerModifyVolume never sends one - a class has at least one
// parameter - and refuses it before calling this.
func parseQoS(parameters map[string]string) (*qosSettings, error) {
	if len(parameters) == 0 {
		return nil, nil
	}

	for _, key := range sortedKeys(parameters) {
		switch key {
		case qosKeyMinIOPS, qosKeyMaxIOPS, qosKeyMaxBandwidth, qosKeyQoS:
		default:
			return nil, status.Errorf(codes.InvalidArgument,
				"unknown VolumeAttributesClass parameter %q; the supported keys are %s, %s, %s and %s",
				key, qosKeyMinIOPS, qosKeyMaxIOPS, qosKeyMaxBandwidth, qosKeyQoS)
		}
	}

	if value, ok := parameters[qosKeyQoS]; ok {
		if value != qosNone {
			return nil, status.Errorf(codes.InvalidArgument,
				"VolumeAttributesClass parameter %s must be %q, got %q", qosKeyQoS, qosNone, value)
		}
		if len(parameters) > 1 {
			return nil, status.Errorf(codes.InvalidArgument,
				"VolumeAttributesClass parameter %s: %q cannot be combined with any other parameter", qosKeyQoS, qosNone)
		}
		return nil, nil
	}

	var settings qosSettings
	var err error
	if settings.MinimumIops, err = parseIOPS(parameters, qosKeyMinIOPS); err != nil {
		return nil, err
	}
	if settings.MaximumIops, err = parseIOPS(parameters, qosKeyMaxIOPS); err != nil {
		return nil, err
	}
	if settings.MaximumBandwidthBytesPerSecond, err = parseBandwidth(parameters, qosKeyMaxBandwidth); err != nil {
		return nil, err
	}

	// A maximum of 0 is "no limit", so a reservation above it is not a
	// contradiction; Hyper-V refuses the pair only when the limit is real.
	if settings.MaximumIops > 0 && settings.MinimumIops > settings.MaximumIops {
		return nil, status.Errorf(codes.InvalidArgument,
			"VolumeAttributesClass parameter %s (%d) exceeds %s (%d)",
			qosKeyMinIOPS, settings.MinimumIops, qosKeyMaxIOPS, settings.MaximumIops)
	}

	// Turning QoS off has exactly one spelling. An all-zero class would leave
	// a policy behind that limits nothing, and the invariant this design
	// rests on - a volume has a policy if and only if it has QoS - would no
	// longer say anything.
	if settings == (qosSettings{}) {
		return nil, status.Errorf(codes.InvalidArgument,
			"every QoS parameter in this VolumeAttributesClass is 0; use %s: %q to turn QoS off", qosKeyQoS, qosNone)
	}

	return &settings, nil
}

func parseIOPS(parameters map[string]string, key string) (uint64, error) {
	value, ok := parameters[key]
	if !ok {
		return 0, nil
	}

	// ParseUint, not a quantity: these are counts of normalized I/Os, and
	// refusing "1k" or "+5" keeps one spelling of each value.
	iops, err := strconv.ParseUint(value, 10, 64)
	if err != nil {
		return 0, status.Errorf(codes.InvalidArgument,
			"VolumeAttributesClass parameter %s must be a non-negative integer, got %q", key, value)
	}
	return iops, nil
}

func parseBandwidth(parameters map[string]string, key string) (uint64, error) {
	value, ok := parameters[key]
	if !ok {
		return 0, nil
	}

	quantity, err := resource.ParseQuantity(value)
	if err != nil {
		return 0, status.Errorf(codes.InvalidArgument,
			"VolumeAttributesClass parameter %s must be a quantity of bytes per second such as \"200Mi\", got %q: %v",
			key, value, err)
	}
	if quantity.Sign() < 0 {
		return 0, status.Errorf(codes.InvalidArgument,
			"VolumeAttributesClass parameter %s must not be negative, got %q", key, value)
	}
	bytes, exact := quantity.AsInt64()
	if !exact {
		return 0, status.Errorf(codes.InvalidArgument,
			"VolumeAttributesClass parameter %s must be a whole number of bytes per second, got %q", key, value)
	}
	return uint64(bytes), nil
}

// rejectQoSInStorageClass refuses a StorageClass that tries to set QoS. Its
// parameters are fixed for the life of the PV, so QoS set there could never be
// changed; a VolumeAttributesClass is the only way to set it, and saying so is
// better than ignoring the key the way other StorageClass parameters still
// are (issue #23).
func rejectQoSInStorageClass(parameters map[string]string) error {
	for _, key := range sortedKeys(parameters) {
		switch key {
		case qosKeyMinIOPS, qosKeyMaxIOPS, qosKeyMaxBandwidth, qosKeyQoS:
			return status.Errorf(codes.InvalidArgument,
				"StorageClass parameter %q is not supported: QoS is set through a VolumeAttributesClass "+
					"(spec.volumeAttributesClassName on the PVC), not the StorageClass", key)
		}
	}
	return nil
}

func sortedKeys(m map[string]string) []string {
	keys := make([]string, 0, len(m))
	for key := range m {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

// modifyVolumePayload is the operation-specific half of the agent's job
// envelope, matching ModifyVolumePayload on the .NET side. Qos is sent as an
// explicit null for "no QoS" rather than omitted, so the agent can tell that
// apart from a payload that forgot the field.
type modifyVolumePayload struct {
	VolumeID string       `json:"volumeId"`
	Qos      *qosSettings `json:"qos"`
}

// ControllerModifyVolume applies a VolumeAttributesClass change - which, in
// this driver, is only ever QoS. Idempotency key: volume ID and the requested
// QoS, as "<volumeId>#qos=<settings>".
//
// The settings are in the key for the reason ControllerExpandVolume's size is
// in its own: the agent hands back any job for the same key that is still
// running, so a PVC repointed again before the first change finished would
// otherwise come back as the earlier class's job. The agent serializes
// changes to one volume regardless of key.
//
// external-resizer calls this when a PVC's spec.volumeAttributesClassName
// differs from status.currentVolumeAttributesClassName, and records the new
// class only once this returns success. InvalidArgument leaves the PVC
// Infeasible, which pointing it back at the previous class cancels; anything
// else is retried.
func (s *controllerServer) ControllerModifyVolume(ctx context.Context, req *csi.ControllerModifyVolumeRequest) (*csi.ControllerModifyVolumeResponse, error) {
	klog.V(2).InfoS("ControllerModifyVolume", "volumeId", req.GetVolumeId(), "mutableParameters", req.GetMutableParameters())

	if req.GetVolumeId() == "" {
		klog.V(2).InfoS("ControllerModifyVolume: rejected, no volume id")
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if len(req.GetMutableParameters()) == 0 {
		klog.V(2).InfoS("ControllerModifyVolume: rejected, no mutable parameters", "volumeId", req.GetVolumeId())
		return nil, status.Errorf(codes.InvalidArgument,
			"mutable parameters are required; to turn QoS off use a VolumeAttributesClass with %s: %q", qosKeyQoS, qosNone)
	}

	qos, err := parseQoS(req.GetMutableParameters())
	if err != nil {
		klog.V(2).InfoS("ControllerModifyVolume: rejected, invalid mutable parameters", "volumeId", req.GetVolumeId(), "err", err)
		return nil, err
	}

	key := req.GetVolumeId() + "#qos=" + qos.key()
	job, err := s.driver.Agent.EnqueueJob(ctx, key, operationModifyVolume, modifyVolumePayload{
		VolumeID: req.GetVolumeId(),
		Qos:      qos,
	})
	if err != nil {
		return nil, enqueueFailed(ctx, err, "enqueueing ControllerModifyVolume for %s", req.GetVolumeId())
	}

	// Nothing to decode: the agent reads the policy - and the disk's policy
	// ID, when the disk is attached - back before reporting success, so the
	// job succeeding is the whole answer.
	if _, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget); err != nil {
		return nil, err
	}

	klog.V(2).InfoS("ControllerModifyVolume: volume modified", "volumeId", req.GetVolumeId(), "qos", describeQoS(qos))
	return &csi.ControllerModifyVolumeResponse{}, nil
}

// describeQoS is the settings as a log reads them.
func describeQoS(q *qosSettings) string {
	if q == nil {
		return qosNone
	}
	parts := []string{
		fmt.Sprintf("minIOPS=%d", q.MinimumIops),
		fmt.Sprintf("maxIOPS=%d", q.MaximumIops),
		fmt.Sprintf("maxBandwidth=%d", q.MaximumBandwidthBytesPerSecond),
	}
	return strings.Join(parts, " ")
}
