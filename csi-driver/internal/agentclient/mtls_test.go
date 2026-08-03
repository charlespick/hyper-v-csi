package agentclient

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"io"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
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

	client, err := NewMutualTLS(server.URL, certFile, keyFile)
	if err != nil {
		t.Fatalf("NewMutualTLS: %v", err)
	}
	// Trust the httptest server's own certificate; in production the agent's
	// is a publicly-trusted Let's Encrypt certificate and the system roots
	// cover it, which is why the client configures no CA of its own.
	client.HTTPClient.Transport.(*http.Transport).TLSClientConfig.RootCAs = serverRoots(t, server)

	if _, err := client.EnqueueJob(context.Background(), "pvc-1", "CreateVolume", "volume:pvc-1", nil); err != nil {
		t.Fatalf("EnqueueJob: %v", err)
	}

	if presented == nil {
		t.Fatal("no client certificate presented; the agent would reject this connection")
	}
	if !presented.Equal(clientCert) {
		t.Errorf("presented %q, want the configured client certificate", presented.Subject.CommonName)
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
	if _, err := NewMutualTLS("https://agent.example", certFile, mismatched); err == nil {
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

func serverRoots(t *testing.T, server *httptest.Server) *x509.CertPool {
	t.Helper()

	pool := x509.NewCertPool()
	pool.AddCert(server.Certificate())
	return pool
}
