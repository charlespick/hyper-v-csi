package agentclient

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha1" //nolint:gosec // fingerprint algorithm, matching the agent's own pin format
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/pem"
	"io"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// TestMutualTLSPresentsTheClientCertificate drives a real TLS handshake rather
// than asserting on config fields: the agent authorizes callers purely by the
// fingerprint of the certificate presented during that handshake, so "did we
// actually present it" is the only thing worth checking.
func TestMutualTLSPresentsTheClientCertificate(t *testing.T) {
	clientCertPEM, clientKeyPEM, clientCert := selfSigned(t, "hyperv-csi-driver")
	certFile, keyFile := writePair(t, clientCertPEM, clientKeyPEM)

	var presented *x509.Certificate
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if len(r.TLS.PeerCertificates) > 0 {
			presented = r.TLS.PeerCertificates[0]
		}
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	// The agent pins fingerprints instead of validating a chain, so the test
	// server does the same: require a certificate, verify nothing about it.
	server.TLS = &tls.Config{ClientAuth: tls.RequireAnyClientCert}
	server.StartTLS()
	defer server.Close()

	client, err := NewMutualTLS(server.URL, certFile, keyFile, []string{thumbprintOf(server.Certificate())})
	if err != nil {
		t.Fatalf("NewMutualTLS: %v", err)
	}

	if _, err := client.EnqueueJob(context.Background(), "pvc-1", "CreateVolume", nil); err != nil {
		t.Fatalf("EnqueueJob: %v", err)
	}

	if presented == nil {
		t.Fatal("no client certificate presented; the agent would reject this connection")
	}
	if !presented.Equal(clientCert) {
		t.Errorf("presented %q, want the configured client certificate", presented.Subject.CommonName)
	}
}

// TestMutualTLSRejectsAnUnpinnedServerCertificate is the reverse of the
// agent's own ClientCertificateEnforcementTests: a certificate that is
// perfectly valid, just not the pinned one, must not reach the job API.
func TestMutualTLSRejectsAnUnpinnedServerCertificate(t *testing.T) {
	clientCertPEM, clientKeyPEM, _ := selfSigned(t, "hyperv-csi-driver")
	certFile, keyFile := writePair(t, clientCertPEM, clientKeyPEM)

	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	server.TLS = &tls.Config{ClientAuth: tls.RequireAnyClientCert}
	server.StartTLS()
	defer server.Close()

	// A well-formed thumbprint, just not the server's own.
	client, err := NewMutualTLS(server.URL, certFile, keyFile, []string{strings.Repeat("AB", 20)})
	if err != nil {
		t.Fatalf("NewMutualTLS: %v", err)
	}

	if _, err := client.EnqueueJob(context.Background(), "pvc-1", "CreateVolume", nil); err == nil {
		t.Fatal("connected to an agent certificate that was not pinned")
	}
}

// TestMutualTLSRejectsAnExpiredServerCertificate mirrors the agent's own
// PinnedButExpiredCertificate_NeverReachesTheJobApi: pinning says which key is
// trusted, not for how long.
func TestMutualTLSRejectsAnExpiredServerCertificate(t *testing.T) {
	clientCertPEM, clientKeyPEM, _ := selfSigned(t, "hyperv-csi-driver")
	certFile, keyFile := writePair(t, clientCertPEM, clientKeyPEM)

	serverCert := expiredSelfSignedTLSCertificate(t, "hyperv-csi-agent")
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	server.TLS = &tls.Config{
		Certificates: []tls.Certificate{serverCert},
		ClientAuth:   tls.RequireAnyClientCert,
	}
	server.StartTLS()
	defer server.Close()

	parsed, err := x509.ParseCertificate(serverCert.Certificate[0])
	if err != nil {
		t.Fatal(err)
	}

	client, err := NewMutualTLS(server.URL, certFile, keyFile, []string{thumbprintOf(parsed)})
	if err != nil {
		t.Fatalf("NewMutualTLS: %v", err)
	}

	if _, err := client.EnqueueJob(context.Background(), "pvc-1", "CreateVolume", nil); err == nil {
		t.Fatal("connected to an agent certificate outside its validity window")
	}
}

func TestNewMutualTLSRequiresAtLeastOneServerCertificateThumbprint(t *testing.T) {
	certPEM, keyPEM, _ := selfSigned(t, "hyperv-csi-driver")
	certFile, keyFile := writePair(t, certPEM, keyPEM)

	if _, err := NewMutualTLS("https://agent.example", certFile, keyFile, nil); err == nil {
		t.Error("accepted no server certificate thumbprints at all")
	}
}

func TestNewMutualTLSRejectsAMalformedServerCertificateThumbprint(t *testing.T) {
	certPEM, keyPEM, _ := selfSigned(t, "hyperv-csi-driver")
	certFile, keyFile := writePair(t, certPEM, keyPEM)

	if _, err := NewMutualTLS("https://agent.example", certFile, keyFile, []string{"not a thumbprint"}); err == nil {
		t.Error("accepted a malformed server certificate thumbprint")
	}
}

func TestMutualTLSRejectsAnUnreadableKeyPair(t *testing.T) {
	certPEM, keyPEM, _ := selfSigned(t, "hyperv-csi-driver")
	certFile, _ := writePair(t, certPEM, keyPEM)
	_, otherKey, _ := selfSigned(t, "someone-else")
	mismatched := filepath.Join(t.TempDir(), "mismatched.key")
	if err := os.WriteFile(mismatched, otherKey, 0o600); err != nil {
		t.Fatal(err)
	}

	// A cert and key that don't belong together would otherwise fail at the
	// first handshake, long after startup and far from the cause.
	if _, err := NewMutualTLS("https://agent.example", certFile, mismatched, []string{strings.Repeat("AB", 20)}); err == nil {
		t.Error("accepted a certificate and key that do not match")
	}
}

func selfSigned(t *testing.T, commonName string) (certPEM, keyPEM []byte, certificate *x509.Certificate) {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}

	template := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: commonName},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(24 * time.Hour),
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}

	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	parsed, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}

	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}

	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}),
		parsed
}

func writePair(t *testing.T, certPEM, keyPEM []byte) (certFile, keyFile string) {
	t.Helper()

	dir := t.TempDir()
	certFile = filepath.Join(dir, "tls.crt")
	keyFile = filepath.Join(dir, "tls.key")
	if err := os.WriteFile(certFile, certPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keyFile, keyPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	return certFile, keyFile
}

func thumbprintOf(certificate *x509.Certificate) string {
	sum := sha1.Sum(certificate.Raw) //nolint:gosec // fingerprint, not a signature
	return strings.ToUpper(hex.EncodeToString(sum[:]))
}

// expiredSelfSignedTLSCertificate is selfSigned's shape but already outside
// its validity window, for exercising the pin's expiry check without an
// injectable clock.
func expiredSelfSignedTLSCertificate(t *testing.T, commonName string) tls.Certificate {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}

	template := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: commonName},
		NotBefore:    time.Now().Add(-48 * time.Hour),
		NotAfter:     time.Now().Add(-24 * time.Hour),
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}

	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}

	certificate, err := tls.X509KeyPair(
		pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}),
	)
	if err != nil {
		t.Fatal(err)
	}
	return certificate
}
