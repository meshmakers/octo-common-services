using Meshmakers.Octo.Services.ArtifactStorage.S3;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.ArtifactStorage.Configuration;

/// <summary>
///     Validates <see cref="ArtifactStorageOptions" /> for the selected provider. Messages name settings, never
///     their values.
/// </summary>
public sealed class ArtifactStorageOptionsValidator : IValidateOptions<ArtifactStorageOptions>
{
    /// <summary>Minimum S3 multipart part size.</summary>
    public const int MinS3PartSize = 5 * 1024 * 1024;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ArtifactStorageOptions options)
    {
        var failures = new List<string>();
        if (!ArtifactKey.IsValid(options.InstancePrefix))
        {
            failures.Add($"{ArtifactStorageOptions.SectionName}:InstancePrefix must be one or more key segments (letters, digits, '-', '_', '.').");
        }

        switch (options.Provider)
        {
            case ArtifactStorageProvider.FileSystem:
                break;
            case ArtifactStorageProvider.S3:
                ValidateS3(options.S3, failures);
                break;
            case ArtifactStorageProvider.AzureBlob:
                ValidateAzure(options.AzureBlob, failures);
                break;
            default:
                failures.Add($"{ArtifactStorageOptions.SectionName}:Provider must be FileSystem, S3 or AzureBlob.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateS3(S3ArtifactStorageOptions s3, List<string> failures)
    {
        const string p = ArtifactStorageOptions.SectionName + ":S3:";
        if (string.IsNullOrWhiteSpace(s3.Bucket))
        {
            failures.Add($"{p}Bucket is required.");
        }

        if (string.IsNullOrWhiteSpace(s3.ServiceUrl) && string.IsNullOrWhiteSpace(s3.Region))
        {
            failures.Add($"{p}ServiceUrl or {p}Region is required.");
        }

        if (!string.IsNullOrWhiteSpace(s3.ServiceUrl) &&
            !Uri.TryCreate(s3.ServiceUrl, UriKind.Absolute, out _))
        {
            failures.Add($"{p}ServiceUrl must be an absolute URL.");
        }

        if (string.IsNullOrEmpty(s3.AccessKeyId) != string.IsNullOrEmpty(s3.SecretAccessKey))
        {
            failures.Add($"{p}AccessKeyId and {p}SecretAccessKey must be set together.");
        }

        if (!S3ArtifactStore.IsSupportedServerSideEncryption(s3.ServerSideEncryption))
        {
            failures.Add($"{p}ServerSideEncryption must be 'None' or 'AES256'.");
        }

        if (s3.MultipartPartSizeBytes < MinS3PartSize)
        {
            failures.Add($"{p}MultipartPartSizeBytes must be at least {MinS3PartSize}.");
        }
    }

    private static void ValidateAzure(AzureBlobArtifactStorageOptions azure, List<string> failures)
    {
        const string p = ArtifactStorageOptions.SectionName + ":AzureBlob:";
        if (string.IsNullOrWhiteSpace(azure.Container))
        {
            failures.Add($"{p}Container is required.");
        }

        if (!string.IsNullOrWhiteSpace(azure.ConnectionString))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(azure.AccountUrl))
        {
            failures.Add($"{p}ConnectionString or {p}AccountUrl is required.");
            return;
        }

        if (!Uri.TryCreate(azure.AccountUrl, UriKind.Absolute, out _))
        {
            failures.Add($"{p}AccountUrl must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(azure.AccountKey) && !azure.UseManagedIdentity)
        {
            failures.Add($"{p}AccountUrl needs {p}AccountKey or {p}UseManagedIdentity=true.");
        }
    }
}
