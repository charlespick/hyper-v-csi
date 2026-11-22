{{/*
The CSI driver name: the CSIDriver object's metadata.name, every StorageClass's
provisioner, the VolumeSnapshotClass's and VolumeAttributesClasses' driver,
the kubelet plugin directory, and what the binary registers as. The default
matches the binary's own default, so an install that leaves driverName unset
passes no --driver-name at all. Changing it once volumes exist orphans their
PersistentVolumes.
*/}}
{{- define "hyperv-csi.defaultDriverName" -}}
csi.hyper-v.makerland.xyz
{{- end -}}

{{- define "hyperv-csi.driverName" -}}
{{- .Values.driverName | default (include "hyperv-csi.defaultDriverName" .) -}}
{{- end -}}

{{/*
"true" when this release registers under a name of its own - an additional
install, one per failover cluster. It is the one switch that also gives the
release's other cluster-wide and per-namespace names a per-release form, so a
release that leaves driverName unset renders exactly what it always has.
*/}}
{{- define "hyperv-csi.customDriverName" -}}
{{- if ne (include "hyperv-csi.driverName" .) (include "hyperv-csi.defaultDriverName" .) -}}true{{- end -}}
{{- end -}}

{{/*
"true" when zone topology is on. Read through this rather than
.Values.zoneTopology.enabled directly: `helm upgrade --reuse-values` from a
release made before the value existed carries no zoneTopology map at all, and
the direct path then fails the upgrade on a nil pointer.
*/}}
{{- define "hyperv-csi.zoneTopology" -}}
{{- if and .Values.zoneTopology .Values.zoneTopology.enabled -}}true{{- end -}}
{{- end -}}

{{/*
The always-rendered no-QoS VolumeAttributesClass. Cluster-scoped, so a second
release under its own driver name would collide on the fixed name; it gets
one after the release instead.
*/}}
{{- define "hyperv-csi.noneVolumeAttributesClassName" -}}
{{- if include "hyperv-csi.customDriverName" . -}}
{{- printf "%s-none" (include "hyperv-csi.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- else -}}
hyperv-none
{{- end -}}
{{- end -}}

{{- define "hyperv-csi.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "hyperv-csi.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "hyperv-csi.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "hyperv-csi.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "hyperv-csi.selectorLabels" -}}
app.kubernetes.io/name: {{ include "hyperv-csi.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "hyperv-csi.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "hyperv-csi.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "hyperv-csi.image" -}}
{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}
{{- end -}}

{{/*
Name of the Secret holding the client certificate, whichever way it was
provided.
*/}}
{{- define "hyperv-csi.clientCertificateSecret" -}}
{{- if .Values.clientCertificate.existingSecret -}}
{{- .Values.clientCertificate.existingSecret -}}
{{- else -}}
{{- printf "%s-client-cert" (include "hyperv-csi.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
Rejects configurations the driver itself would reject at startup, so the
mistake surfaces at `helm install` rather than as a CrashLoopBackOff.
*/}}
{{- define "hyperv-csi.validate" -}}
{{- $driverName := include "hyperv-csi.driverName" . -}}
{{- if not (regexMatch "^[a-zA-Z][A-Za-z0-9-._]{0,61}[a-zA-Z0-9]$" $driverName) -}}
{{- fail (printf "driverName %q is not a valid CSI driver name: at most 63 characters of letters, digits, '-', '.' and '_', starting with a letter and ending with a letter or digit" $driverName) -}}
{{- end -}}
{{- /* Stricter than CSI: the chart also uses the name as the CSIDriver's
metadata.name, which the API server only accepts as a lowercase DNS
subdomain, so a name CSI allows (uppercase, '_') would otherwise fail only
at apply time. */ -}}
{{- if not (regexMatch "^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$" $driverName) -}}
{{- fail (printf "driverName %q must be lowercase letters, digits, '-' and '.': it is also the CSIDriver object's name, which Kubernetes requires to be a DNS subdomain" $driverName) -}}
{{- end -}}
{{- if not .Values.agent.address -}}
{{- fail "agent.address is required: the https:// URL of hyperv-csi-agent on the failover cluster" -}}
{{- end -}}
{{- if .Values.agent.allowInsecure -}}
  {{- if hasPrefix "https://" .Values.agent.address -}}
{{- fail "agent.allowInsecure is set but agent.address is https://; clear one of them" -}}
  {{- end -}}
{{- else -}}
  {{- if not (hasPrefix "https://" .Values.agent.address) -}}
{{- fail "agent.address must start with https:// unless agent.allowInsecure is set; over plaintext the client certificate proves nothing" -}}
  {{- end -}}
  {{- if and (not .Values.clientCertificate.existingSecret) (not (and .Values.clientCertificate.cert .Values.clientCertificate.key)) -}}
{{- fail "a client certificate is required: set clientCertificate.existingSecret, or supply clientCertificate.cert and .key with --set-file" -}}
  {{- end -}}
  {{- if not .Values.agent.serverCertificateThumbprints -}}
{{- fail "agent.serverCertificateThumbprints is required: the agent's server certificate is self-signed, so the driver has no other way to trust it" -}}
  {{- end -}}
{{- end -}}
{{- end -}}
