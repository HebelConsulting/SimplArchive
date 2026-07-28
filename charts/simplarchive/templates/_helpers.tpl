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
  value: {{ .Values.config.objectStorage.serviceUrl | quote }}
- name: ObjectStorage__PublicServiceUrl
  value: {{ .Values.config.objectStorage.publicServiceUrl | quote }}
- name: ObjectStorage__Region
  value: {{ .Values.config.objectStorage.region | quote }}
- name: ObjectStorage__BucketName
  value: {{ .Values.config.objectStorage.bucketName | quote }}
- name: Gotenberg__Url
  value: {{ .Values.config.gotenbergUrl | quote }}
- name: OpenSearch__Url
  value: {{ .Values.config.openSearchUrl | quote }}
- name: Tika__Url
  value: {{ .Values.config.tikaUrl | quote }}
- name: Tika__OcrLanguages
  value: {{ .Values.config.tikaOcrLanguages | quote }}
- name: Ocr__Url
  value: {{ .Values.config.ocrUrl | quote }}
- name: ConnectionStrings__Valkey
  value: {{ .Values.config.valkeyConnectionString | quote }}
- name: Smtp__Host
  value: {{ .Values.config.smtp.host | quote }}
- name: Smtp__Port
  value: {{ .Values.config.smtp.port | quote }}
- name: Smtp__FromAddress
  value: {{ .Values.config.smtp.fromAddress | quote }}
- name: Smtp__FromName
  value: {{ .Values.config.smtp.fromName | quote }}
- name: OpenBao__Address
  value: {{ .Values.config.openBao.address | quote }}
- name: OpenBao__RoleId
  value: {{ .Values.config.openBao.roleId | quote }}
- name: OpenBao__DatabaseConnectionTemplate
  value: {{ .Values.config.openBao.databaseConnectionTemplate | quote }}
- name: Bootstrap__PlatformAdministrator__Name
  value: {{ .Values.config.bootstrap.name | quote }}
- name: Bootstrap__PlatformAdministrator__ClientId
  value: {{ .Values.config.bootstrap.clientId | quote }}
{{- end -}}
