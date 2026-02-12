using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload;

/// <summary>
/// Securely stores cloud provider credentials using Windows DPAPI.
/// </summary>
public sealed class CredentialStore
{
    private readonly ILogger<CredentialStore> _logger;
    private readonly string _storePath;

    public CredentialStore(ILogger<CredentialStore> logger, string? storePath = null)
    {
        _logger = logger;
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Screener",
            "credentials");

        Directory.CreateDirectory(_storePath);
    }

    /// <summary>
    /// Store credentials for a provider.
    /// </summary>
    public async Task StoreCredentialsAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(credentials.Values);

            // Encrypt using DPAPI (Windows only, current user scope)
            var encrypted = ProtectedData.Protect(
                json,
                GetEntropy(credentials.ProviderId),
                DataProtectionScope.CurrentUser);

            var filePath = GetCredentialFilePath(credentials.ProviderId);
            await File.WriteAllBytesAsync(filePath, encrypted, ct);

            _logger.LogInformation("Stored credentials for provider {ProviderId}", credentials.ProviderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store credentials for {ProviderId}", credentials.ProviderId);
            throw;
        }
    }

    /// <summary>
    /// Retrieve credentials for a provider.
    /// </summary>
    public async Task<ProviderCredentials?> GetCredentialsAsync(string providerId, CancellationToken ct = default)
    {
        var filePath = GetCredentialFilePath(providerId);

        if (!File.Exists(filePath))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(filePath, ct);

            var decrypted = ProtectedData.Unprotect(
                encrypted,
                GetEntropy(providerId),
                DataProtectionScope.CurrentUser);

            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(decrypted);

            if (values == null)
                return null;

            return new ProviderCredentials(providerId, values);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt credentials for {ProviderId} - may have been encrypted by different user", providerId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve credentials for {ProviderId}", providerId);
            return null;
        }
    }

    /// <summary>
    /// Delete credentials for a provider.
    /// </summary>
    public Task DeleteCredentialsAsync(string providerId, CancellationToken ct = default)
    {
        var filePath = GetCredentialFilePath(providerId);

        if (File.Exists(filePath))
        {
            // Secure delete: overwrite with zeros before deleting
            var fileInfo = new FileInfo(filePath);
            var zeros = new byte[fileInfo.Length];
            File.WriteAllBytes(filePath, zeros);
            File.Delete(filePath);

            _logger.LogInformation("Deleted credentials for provider {ProviderId}", providerId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Check if credentials exist for a provider.
    /// </summary>
    public bool HasCredentials(string providerId)
    {
        return File.Exists(GetCredentialFilePath(providerId));
    }

    /// <summary>
    /// List all providers with stored credentials.
    /// </summary>
    public IReadOnlyList<string> GetStoredProviders()
    {
        if (!Directory.Exists(_storePath))
            return Array.Empty<string>();

        return Directory.GetFiles(_storePath, "*.cred")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
    }

    private string GetCredentialFilePath(string providerId)
        => Path.Combine(_storePath, $"{providerId}.cred");

    private static byte[] GetEntropy(string providerId)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"Screener.{providerId}.Entropy.v1"));
}
