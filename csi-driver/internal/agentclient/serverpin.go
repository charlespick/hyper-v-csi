package agentclient

import (
	"crypto/sha1" //nolint:gosec // fingerprint algorithm, not a security-strength hash choice; matches the agent's own pin format
	"crypto/x509"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"
	"time"
)

// thumbprintLength is a SHA-1 fingerprint's length in hex characters, matching
// the agent's own AllowedClientCertificateThumbprints format.
const thumbprintLength = 40

// normalizeServerCertificateThumbprints validates and hex-normalizes the
// configured pins once at client construction, so a malformed value fails
// startup instead of every handshake.
func normalizeServerCertificateThumbprints(raw []string) (map[string]struct{}, error) {
	if len(raw) == 0 {
		return nil, errors.New("at least one server certificate thumbprint is required when a client certificate is configured")
	}

	allowed := make(map[string]struct{}, len(raw))
	for _, thumbprint := range raw {
		normalized := normalizeThumbprint(thumbprint)
		if len(normalized) != thumbprintLength {
			return nil, fmt.Errorf(
				"server certificate thumbprint %q is not a SHA-1 thumbprint: expected %d hex characters, got %d; paste only the fingerprint, without any label",
				thumbprint, thumbprintLength, len(normalized))
		}
		allowed[normalized] = struct{}{}
	}
	return allowed, nil
}

// normalizeThumbprint strips everything but hex digits and upper-cases what's
// left, the same tolerance the agent's own thumbprint parsing applies -
// operators paste these out of openssl, certutil, and the Windows certificate
// dialog, which disagree on separators and case.
func normalizeThumbprint(thumbprint string) string {
	var b strings.Builder
	for _, r := range thumbprint {
		if (r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') || (r >= 'A' && r <= 'F') {
			b.WriteRune(r)
		}
	}
	return strings.ToUpper(b.String())
}

// verifyServerCertificateThumbprint builds a tls.Config.VerifyPeerCertificate
// callback that authorizes the agent's server certificate by fingerprint
// instead of a trust chain - the mirror image of how the agent itself
// authorizes this client's certificate. The agent's certificate is
// self-signed, so there is no chain to validate; possession of the pinned
// certificate is the entire claim being checked. Assigning this callback
// requires tls.Config.InsecureSkipVerify, which is what disables Go's own
// chain validation - deliberately, since that validation would reject every
// self-signed certificate outright.
func verifyServerCertificateThumbprint(allowed map[string]struct{}) func([][]byte, [][]*x509.Certificate) error {
	return func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
		if len(rawCerts) == 0 {
			return errors.New("agent presented no certificate")
		}

		certificate, err := x509.ParseCertificate(rawCerts[0])
		if err != nil {
			return fmt.Errorf("parsing agent certificate: %w", err)
		}

		// The pin says which key is trusted; the validity window says for how
		// long. Ignoring it would make an expired certificate valid forever,
		// which is the opposite of what rotating one is for.
		now := time.Now()
		if now.Before(certificate.NotBefore) || now.After(certificate.NotAfter) {
			return fmt.Errorf("agent certificate is outside its validity window (%s to %s)",
				certificate.NotBefore, certificate.NotAfter)
		}

		sum := sha1.Sum(certificate.Raw) //nolint:gosec // fingerprint, not a signature
		fingerprint := strings.ToUpper(hex.EncodeToString(sum[:]))
		if _, ok := allowed[fingerprint]; ok {
			return nil
		}
		return fmt.Errorf("agent certificate fingerprint %s is not a pinned server certificate", fingerprint)
	}
}
