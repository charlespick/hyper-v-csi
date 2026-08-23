package driver

import (
	"fmt"
	"reflect"
)

// secretsRedacted is what a redacted CSI request logs in place of its real
// Secrets map.
const secretsRedacted = "***stripped***"

// redactedRequest formats a CSI request the way klog's own %+v would, except
// an exported "Secrets" field - the map[string]string CreateVolumeRequest,
// ControllerPublishVolumeRequest, NodeStageVolumeRequest,
// CreateSnapshotRequest and others carry for StorageClass/
// VolumeSnapshotClass-referenced provisioner/publish secrets - prints as a
// fixed marker instead of its real contents.
//
// values.yaml documents --v=4 as the ordinary way to debug a stuck call, so
// this driver's own full-request log line must never be the thing that puts
// a secret into a pod's logs, whether or not this particular request type
// happens to carry one today. Returns a plain string rather than a
// fmt.Stringer so it does not depend on how klog's own value formatter
// chooses to treat one.
func redactedRequest(req any) string {
	v := reflect.ValueOf(req)
	if v.Kind() != reflect.Ptr || v.IsNil() || v.Elem().Kind() != reflect.Struct {
		return fmt.Sprintf("%+v", req)
	}

	elem := v.Elem()
	field := elem.FieldByName("Secrets")
	if !field.IsValid() || field.Kind() != reflect.Map || field.Len() == 0 {
		return fmt.Sprintf("%+v", req)
	}

	// A copy, never the real request: it's still in flight to the handler
	// that owns it, and this must not mutate it out from under that call.
	clone := reflect.New(elem.Type())
	clone.Elem().Set(elem)

	redacted := reflect.MakeMap(field.Type())
	placeholder := reflect.ValueOf(secretsRedacted).Convert(field.Type().Elem())
	for _, key := range field.MapKeys() {
		redacted.SetMapIndex(key, placeholder)
	}
	clone.Elem().FieldByName("Secrets").Set(redacted)

	return fmt.Sprintf("%+v", clone.Interface())
}
