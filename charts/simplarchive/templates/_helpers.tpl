{{/* Expand the name of the chart. */}}
{{- define "simplarchive.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Fully qualified app name. */}}
{{- define "simplarchive.fullname" -}}
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

{{- define "simplarchive.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "simplarchive.labels" -}}
helm.sh/chart: {{ include "simplarchive.chart" . }}
{{ include "simplarchive.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "simplarchive.selectorLabels" -}}
app.kubernetes.io/name: {{ include "simplarchive.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "simplarchive.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "simplarchive.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "simplarchive.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}

{{/*
Dependency URLs: when an in-cluster dep (ADR 0485) is enabled, point the app at its Service; otherwise use the
config.* value (external/managed). This is what lets `deps.<x>.enabled: true` auto-wire the app with no other change.
*/}}
{{- define "simplarchive.gotenbergUrl" -}}
{{- if .Values.deps.gotenberg.enabled -}}http://{{ include "simplarchive.fullname" . }}-gotenberg:3000{{- else -}}{{ .Values.config.gotenbergUrl }}{{- end -}}
{{- end -}}
{{- define "simplarchive.tikaUrl" -}}
{{- if .Values.deps.tika.enabled -}}http://{{ include "simplarchive.fullname" . }}-tika:9998{{- else -}}{{ .Values.config.tikaUrl }}{{- end -}}
{{- end -}}
{{- define "simplarchive.ocrUrl" -}}
{{- if .Values.deps.ocr.enabled -}}http://{{ include "simplarchive.fullname" . }}-ocr:8080{{- else -}}{{ .Values.config.ocrUrl }}{{- end -}}
{{- end -}}
{{- define "simplarchive.valkeyConn" -}}
{{- if .Values.deps.valkey.enabled -}}{{ include "simplarchive.fullname" . }}-valkey:6379{{- else -}}{{ .Values.config.valkeyConnectionString }}{{- end -}}
{{- end -}}
{{- define "simplarchive.openSearchUrl" -}}
{{- if .Values.deps.opensearch.enabled -}}http://{{ include "simplarchive.fullname" . }}-opensearch:9200{{- else -}}{{ .Values.config.openSearchUrl }}{{- end -}}
{{- end -}}
{{- define "simplarchive.objectStorageServiceUrl" -}}
{{- if .Values.deps.seaweedfs.enabled -}}http://{{ include "simplarchive.fullname" . }}-seaweedfs:8333{{- else -}}{{ .Values.config.objectStorage.serviceUrl }}{{- end -}}
{{- end -}}

{{/* Chart-generated Secret holding the in-cluster stateful deps' credentials (kiosk; phase 3 → OpenBao). */}}
{{- define "simplarchive.statefulSecretName" -}}
{{- printf "%s-stateful" (include "simplarchive.fullname" .) -}}
{{- end -}}
{{- define "simplarchive.openBaoAddress" -}}
{{- if .Values.deps.openbao.enabled -}}http://{{ include "simplarchive.fullname" . }}-openbao:8200{{- else -}}{{ .Values.config.openBao.address }}{{- end -}}
{{- end -}}
{{- define "simplarchive.openBaoRoleId" -}}
{{- if .Values.deps.openbao.enabled -}}{{ .Values.deps.openbao.roleId }}{{- else -}}{{ .Values.config.openBao.roleId }}{{- end -}}
{{- end -}}
{{- define "simplarchive.openBaoDbTemplate" -}}
{{- if .Values.deps.openbao.enabled -}}Host={{ include "simplarchive.fullname" . }}-postgres;Port=5432;Database={{ .Values.deps.postgres.database }}{{- else -}}{{ .Values.config.openBao.databaseConnectionTemplate }}{{- end -}}
{{- end -}}

{{/* Component name for a dep workload/Service, e.g. "<fullname>-tika". */}}
{{- define "simplarchive.depName" -}}
{{- printf "%s-%s" (include "simplarchive.fullname" .root) .component -}}
{{- end -}}

{{/*
The non-secret application config as inline env. Used by both the Deployment and the migration Job (a pre-*
hook, which can't rely on a separately-created ConfigMap existing yet). Inline env also means a config change
rolls the Deployment automatically (the pod template changes). Secrets come from `existingSecret` via envFrom.
*/}}
{{- define "simplarchive.appConfigEnv" -}}
- name: ASPNETCORE_ENVIRONMENT
  value: {{ .Values.config.aspnetCoreEnvironment | quote }}
- name: ASPNETCORE_URLS
  value: "http://+:8080"
- name: App__BaseUrl
  value: {{ .Values.config.app.baseUrl | quote }}
- name: App__ApplyMigrationsAtStartup
  value: {{ .Values.config.app.applyMigrationsAtStartup | quote }}
- name: ObjectStorage__ServiceUrl
  value: {{ include "simplarchive.objectStorageServiceUrl" . | quote }}
- name: ObjectStorage__PublicServiceUrl
  value: {{ .Values.config.objectStorage.publicServiceUrl | quote }}
- name: ObjectStorage__Region
  value: {{ .Values.config.objectStorage.region | quote }}
- name: ObjectStorage__BucketName
  value: {{ .Values.config.objectStorage.bucketName | quote }}
- name: Gotenberg__Url
  value: {{ include "simplarchive.gotenbergUrl" . | quote }}
- name: OpenSearch__Url
  value: {{ include "simplarchive.openSearchUrl" . | quote }}
- name: Tika__Url
  value: {{ include "simplarchive.tikaUrl" . | quote }}
- name: Tika__OcrLanguages
  value: {{ .Values.config.tikaOcrLanguages | quote }}
- name: Ocr__Url
  value: {{ include "simplarchive.ocrUrl" . | quote }}
- name: ConnectionStrings__Valkey
  value: {{ include "simplarchive.valkeyConn" . | quote }}
- name: Smtp__Host
  value: {{ .Values.config.smtp.host | quote }}
- name: Smtp__Port
  value: {{ .Values.config.smtp.port | quote }}
- name: Smtp__FromAddress
  value: {{ .Values.config.smtp.fromAddress | quote }}
- name: Smtp__FromName
  value: {{ .Values.config.smtp.fromName | quote }}
- name: OpenBao__Address
  value: {{ include "simplarchive.openBaoAddress" . | quote }}
- name: OpenBao__RoleId
  value: {{ include "simplarchive.openBaoRoleId" . | quote }}
- name: OpenBao__DatabaseConnectionTemplate
  value: {{ include "simplarchive.openBaoDbTemplate" . | quote }}
{{- if .Values.deps.openbao.enabled }}
- name: OpenBao__SecretId
  valueFrom:
    secretKeyRef:
      name: {{ include "simplarchive.fullname" . }}-openbao-approle
      key: OpenBao__SecretId
- name: OpenBao__DatabaseOwnerStaticRole
  value: "simplarchive-owner"
{{- end }}
- name: Bootstrap__PlatformAdministrator__Name
  value: {{ .Values.config.bootstrap.name | quote }}
- name: Bootstrap__PlatformAdministrator__ClientId
  value: {{ .Values.config.bootstrap.clientId | quote }}
{{- end -}}
