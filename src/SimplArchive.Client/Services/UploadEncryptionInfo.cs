namespace SimplArchive.Client.Services;

/// <summary>
/// The client-side at-rest encryption instruction riding on a create-version response (ADR 0818/B2):
/// present only on encryption-gated tenants — its presence tells the uploader to encrypt before the
/// presigned PUT, wrapping a fresh DEK against this KEK. Shared by the page's upload targets and the
/// name-conflict resolver so the two cannot parse the same field differently.
/// </summary>
public sealed record UploadEncryptionInfo(string KekGeneration, string PublicKeyPem, string OaepHash);
