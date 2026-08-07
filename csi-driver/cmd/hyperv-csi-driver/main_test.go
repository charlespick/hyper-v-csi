package main

import (
	"strings"
	"testing"
)

// The credentials are the only thing between the agent's job API and anything
// that can route to it, so losing them must never be something that happens by
// omission.
func TestBuildAgentClientCredentialRules(t *testing.T) {
	certFile, keyFile := "testdata/tls.crt", "testdata/tls.key"

	tests := []struct {
		name              string
		cert, key         string
		serverThumbprints []string
		allowInsecure     bool
		address           string
		wantErr           string
	}{
		{
			name:    "no credentials and no opt-out is refused",
			address: "https://agent.example",
			wantErr: "required in controller mode",
		},
		{
			name:    "certificate without key is refused",
			address: "https://agent.example",
			cert:    certFile,
			wantErr: "must be given together",
		},
		{
			name:    "key without certificate is refused",
			address: "https://agent.example",
			key:     keyFile,
			wantErr: "must be given together",
		},
		{
			// Over plaintext the certificate is never requested, so it proves
			// nothing — the connection is unauthenticated in both directions.
			name:    "a certificate against a plaintext address is refused",
			address: "http://agent.example",
			cert:    certFile,
			key:     keyFile,
			wantErr: "must be https://",
		},
		{
			// The agent's server certificate is self-signed, so there is no
			// chain to fall back to trusting.
			name:    "a client certificate without a server thumbprint is refused",
			address: "https://agent.example",
			cert:    certFile,
			key:     keyFile,
			wantErr: "agent-server-cert-thumbprint is required",
		},
		{
			name:          "opting out explicitly is allowed",
			address:       "http://localhost:5012",
			allowInsecure: true,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := buildAgentClient(test.address, test.cert, test.key, test.serverThumbprints, test.allowInsecure)

			if test.wantErr == "" {
				if err != nil {
					t.Fatalf("buildAgentClient: %v", err)
				}
				return
			}

			if err == nil {
				t.Fatalf("accepted %q, want an error containing %q", test.name, test.wantErr)
			}
			if !strings.Contains(err.Error(), test.wantErr) {
				t.Errorf("err = %v, want it to mention %q", err, test.wantErr)
			}
		})
	}
}

func TestBuildAgentClientAcceptsAValidKeyPair(t *testing.T) {
	serverThumbprint := "370DD6340D9E6FDE640FBAF4C32E2809EC315310"
	if _, err := buildAgentClient(
		"https://agent.example", "testdata/tls.crt", "testdata/tls.key", []string{serverThumbprint}, false,
	); err != nil {
		t.Fatalf("buildAgentClient: %v", err)
	}
}
