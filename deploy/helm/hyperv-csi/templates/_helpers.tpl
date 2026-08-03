{{/*
The CSI driver name. Not configurable on purpose: it has to match the
DriverName constant compiled into the binary, the CSIDriver object's
metadata.name, and every StorageClass's provisioner. Changing it once volumes
exist orphans their PersistentVolumes.
*/}}
{{- define "hyperv-csi.driverName" -}}
csi.hyper-v.makerland.xyz
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
{{- end -}}
{{- end -}}
