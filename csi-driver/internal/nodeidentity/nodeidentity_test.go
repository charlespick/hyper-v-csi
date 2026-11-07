package nodeidentity

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime/schema"
)

const (
	productUUID = "b1263b41-9217-4a68-8143-132a0ca4507c"
	vmID        = "7a446141-becd-4c7e-968a-65257139f98c"
)

func nodeWith(annotations map[string]string) *corev1.Node {
	return &corev1.Node{ObjectMeta: metav1.ObjectMeta{Name: "n1", Annotations: annotations}}
}

func TestRecordedVMID(t *testing.T) {
	for name, tc := range map[string]struct {
		annotations map[string]string
		uuid        string
		want        string
		ok          bool
	}{
		"matching record": {
			annotations: map[string]string{VMIDAnnotation: vmID, SourceSystemUUIDAnnotation: productUUID},
			uuid:        productUUID, want: vmID, ok: true,
		},
		"case and braces do not matter": {
			annotations: map[string]string{
				VMIDAnnotation:             "{7A446141-BECD-4C7E-968A-65257139F98C}",
				SourceSystemUUIDAnnotation: "B1263B41-9217-4A68-8143-132A0CA4507C",
			},
			uuid: "{" + productUUID + "}", want: vmID, ok: true,
		},
		"no record": {
			uuid: productUUID,
		},
		"stale record from another systemUUID": {
			annotations: map[string]string{VMIDAnnotation: vmID, SourceSystemUUIDAnnotation: "c0ffee00-1111-2222-3333-444455556666"},
			uuid:        productUUID,
		},
		"VM ID without a source": {
			annotations: map[string]string{VMIDAnnotation: vmID},
			uuid:        productUUID,
		},
		"source without a VM ID": {
			annotations: map[string]string{SourceSystemUUIDAnnotation: productUUID},
			uuid:        productUUID,
		},
		"VM ID not a GUID": {
			annotations: map[string]string{VMIDAnnotation: "csidevnode01", SourceSystemUUIDAnnotation: productUUID},
			uuid:        productUUID,
		},
		"empty current UUID never matches": {
			annotations: map[string]string{VMIDAnnotation: vmID, SourceSystemUUIDAnnotation: ""},
			uuid:        "",
		},
	} {
		t.Run(name, func(t *testing.T) {
			got, ok := RecordedVMID(nodeWith(tc.annotations), tc.uuid)
			if got != tc.want || ok != tc.ok {
				t.Errorf("RecordedVMID = (%q, %v), want (%q, %v)", got, ok, tc.want, tc.ok)
			}
		})
	}
}

func TestReadProductUUID(t *testing.T) {
	dir := t.TempDir()

	good := filepath.Join(dir, "good")
	// sysfs ends the value with a newline.
	if err := os.WriteFile(good, []byte("B1263B41-9217-4A68-8143-132A0CA4507C\n"), 0o400); err != nil {
		t.Fatal(err)
	}
	if got, err := ReadProductUUID(good); err != nil || got != productUUID {
		t.Errorf("ReadProductUUID = (%q, %v), want (%q, nil)", got, err, productUUID)
	}

	bad := filepath.Join(dir, "bad")
	if err := os.WriteFile(bad, []byte("To be filled by O.E.M.\n"), 0o400); err != nil {
		t.Fatal(err)
	}
	if _, err := ReadProductUUID(bad); err == nil {
		t.Error("ReadProductUUID accepted a value that is not a GUID")
	}

	if _, err := ReadProductUUID(filepath.Join(dir, "missing")); err == nil {
		t.Error("ReadProductUUID succeeded on a missing file")
	}
}

type sequenceGetter struct {
	calls   atomic.Int32
	answers []func() (*corev1.Node, error)
}

func (s *sequenceGetter) Get(context.Context, string, metav1.GetOptions) (*corev1.Node, error) {
	i := int(s.calls.Add(1)) - 1
	if i >= len(s.answers) {
		i = len(s.answers) - 1
	}
	return s.answers[i]()
}

func TestWaitForVMIDWaitsThroughErrorsAndStaleRecordsUntilItMatches(t *testing.T) {
	getter := &sequenceGetter{answers: []func() (*corev1.Node, error){
		func() (*corev1.Node, error) {
			return nil, apierrors.NewNotFound(schema.GroupResource{Resource: "nodes"}, "n1")
		},
		func() (*corev1.Node, error) { return nodeWith(nil), nil },
		// The previous VM's record, still on a Node this VM re-registered to.
		func() (*corev1.Node, error) {
			return nodeWith(map[string]string{
				VMIDAnnotation: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", SourceSystemUUIDAnnotation: "c0ffee00-1111-2222-3333-444455556666",
			}), nil
		},
		func() (*corev1.Node, error) {
			return nodeWith(map[string]string{VMIDAnnotation: vmID, SourceSystemUUIDAnnotation: productUUID}), nil
		},
	}}

	got, err := WaitForVMID(context.Background(), getter, "n1", productUUID, time.Millisecond)

	if err != nil || got != vmID {
		t.Errorf("WaitForVMID = (%q, %v), want (%q, nil)", got, err, vmID)
	}
	if calls := getter.calls.Load(); calls != 4 {
		t.Errorf("Get called %d times, want 4", calls)
	}
}

func TestWaitForVMIDStopsWhenCancelled(t *testing.T) {
	getter := &sequenceGetter{answers: []func() (*corev1.Node, error){
		func() (*corev1.Node, error) { return nodeWith(nil), nil },
	}}
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()

	_, err := WaitForVMID(ctx, getter, "n1", productUUID, time.Millisecond)

	if !errors.Is(err, context.DeadlineExceeded) {
		t.Errorf("err = %v, want the context's error", err)
	}
}
