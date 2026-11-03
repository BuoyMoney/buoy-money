using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>
/// Encrypts what pairing leaves on disk. AES-256-GCM, the key derived with HKDF from <c>Encryption:Key</c>, always in
/// the authenticated format (this table is new, so nothing older has to read it). Format: <c>c1:</c> + base64(nonce|tag|ciphertext).
/// </summary>
public class CloudSecretProtector(IConfiguration configuration)
{
  private const string Prefix = "c1:";
  private readonly byte[] _key = HKDF.DeriveKey(HashAlgorithmName.SHA256,
    Encoding.UTF8.GetBytes(configuration["Encryption:Key"] ?? throw new InvalidOperationException("Encryption key not configured")),
    32, Encoding.UTF8.GetBytes("Buoy.SelfHostPairingSecret.v1"), Encoding.UTF8.GetBytes("aes-256-gcm cloud pairing key"));

  public string Protect(string plain, string purpose)
  {
    var nonce = RandomNumberGenerator.GetBytes(12);
    var data = Encoding.UTF8.GetBytes(plain);
    var cipher = new byte[data.Length];
    var tag = new byte[16];
    using var gcm = new AesGcm(_key, 16);
    gcm.Encrypt(nonce, data, cipher, tag, Encoding.ASCII.GetBytes(purpose));
    return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
  }

  public string Reveal(string stored, string purpose)
  {
    if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unknown format.");
    var blob = Convert.FromBase64String(stored[Prefix.Length..]);
    var plain = new byte[blob.Length - 28];
    using var gcm = new AesGcm(_key, 16);
    gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.ASCII.GetBytes(purpose));
    return Encoding.UTF8.GetString(plain);
  }
}

/// <summary>The unlocked pairing: the credential and the two private keys. Dispose when done so the keys are released.</summary>
public sealed class CloudIdentity : IDisposable
{
  public required Uri CloudUrl { get; init; }
  public required Guid ServerId { get; init; }
  public required string Credential { get; init; }
  public required ECDiffieHellman EncryptionKey { get; init; }
  public required ECDsa SigningKey { get; init; }

  public void Dispose()
  {
    EncryptionKey.Dispose();
    SigningKey.Dispose();
  }
}

public class CloudPairingService : ICloudPairingService
{
  public const string HttpClientName = "cloud-bridge";
  private const string CredentialPurpose = "cloud-credential";
  private const string EncryptionPurpose = "cloud-encryption-key";
  private const string SigningPurpose = "cloud-signing-key";

  private readonly BuoyAIDbContext _db;
  private readonly CloudSecretProtector _protector;
  private readonly IHttpClientFactory _http;
  private readonly CloudBridgeOptions _options;
  private readonly ILogger<CloudPairingService> _logger;

  public CloudPairingService(BuoyAIDbContext db, CloudSecretProtector protector, IHttpClientFactory http,
    IOptions<CloudBridgeOptions> options, ILogger<CloudPairingService> logger)
  {
    _db = db;
    _protector = protector;
    _http = http;
    _options = options.Value;
    _logger = logger;
  }

  public static Uri? ParseCloudUrl(string? url)
  {
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)) return null;
    // Plain http is for a Cloud running on the same machine while developing.
    return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
  }

  public async Task<CloudPairingStatus> GetStatusAsync(CancellationToken ct = default)
  {
    var configured = ParseCloudUrl(_options.Url) != null;
    var row = await _db.CloudPairings.AsNoTracking().FirstOrDefaultAsync(ct);
    return row == null
      ? new CloudPairingStatus(configured, false, configured ? _options.Url : null, null, null, null, null)
      : new CloudPairingStatus(configured, true, row.CloudUrl, row.ServerId, row.Name, row.PairedAt, null);
  }

  public async Task<CloudPairingStatus> PairAsync(string code, string? name, CancellationToken ct = default)
  {
    var cloud = ParseCloudUrl(_options.Url) ?? throw new DomainValidationException("The Cloud bank feed is not set up on this server (CloudBridge:Url).");
    if (string.IsNullOrWhiteSpace(code)) throw new DomainValidationException("Enter the pairing code from your Cloud account.");

    using var encryption = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var displayName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();

    var client = _http.CreateClient(HttpClientName);
    JsonNode? answer;
    try
    {
      using var response = await client.PostAsJsonAsync(new Uri(cloud, "v1/pairing/pair"), new
      {
        code = code.Trim(),
        name = displayName.Length > 80 ? displayName[..80] : displayName,
        encryptionPublicKey = ExportPoint(encryption),
        signingPublicKey = ExportPoint(signing),
        serverVersion = ServerVersion(_options)
      }, ct);
      var text = await response.Content.ReadAsStringAsync(ct);
      if (!response.IsSuccessStatusCode)
      {
        var message = TryMessage(text) ?? "The Cloud did not accept that pairing code.";
        throw new DomainValidationException(message);
      }
      answer = JsonNode.Parse(text);
    }
    catch (HttpRequestException ex)
    {
      _logger.LogWarning("Cloud pairing request failed: {Kind}", ex.GetType().Name);
      throw new DomainValidationException("Could not reach the BuoyAI Cloud. Check this server's internet connection.");
    }

    var serverId = answer?["serverId"]?.GetValue<Guid>() ?? throw new DomainValidationException("The Cloud answered in a way this server does not understand.");
    var credential = answer["credential"]?.GetValue<string>() ?? throw new DomainValidationException("The Cloud answered in a way this server does not understand.");

    var row = await _db.CloudPairings.FirstOrDefaultAsync(ct);
    if (row == null) _db.CloudPairings.Add(row = new CloudPairing { Id = 1 });
    row.CloudUrl = cloud.ToString().TrimEnd('/');
    row.ServerId = serverId;
    row.Name = displayName;
    row.CredentialCipher = _protector.Protect(credential, CredentialPurpose);
    row.EncryptionKeyCipher = _protector.Protect(Convert.ToBase64String(encryption.ExportPkcs8PrivateKey()), EncryptionPurpose);
    row.SigningKeyCipher = _protector.Protect(Convert.ToBase64String(signing.ExportPkcs8PrivateKey()), SigningPurpose);
    row.PairedAt = DateTime.UtcNow;
    try
    {
      await _db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException)
    {
      // Two pairings raced to create the one row; the other won. Nothing here is half written.
      throw new DomainValidationException("This server was just paired by another request. Reload and check its status.");
    }

    return new CloudPairingStatus(true, true, row.CloudUrl, serverId, displayName, row.PairedAt, answer["needsConfirmation"]?.GetValue<bool>());
  }

  public async Task UnpairAsync(CancellationToken ct = default)
  {
    var row = await _db.CloudPairings.FirstOrDefaultAsync(ct);
    if (row == null) return;
    _db.CloudPairings.Remove(row);
    await _db.SaveChangesAsync(ct);
  }

  /// <summary>Unlocks the stored pairing, or null when there is none or it cannot be read.</summary>
  public async Task<CloudIdentity?> GetIdentityAsync(CancellationToken ct = default)
  {
    var row = await _db.CloudPairings.AsNoTracking().FirstOrDefaultAsync(ct);
    if (row == null || ParseCloudUrl(row.CloudUrl) is not { } url) return null;
    try
    {
      var encryption = ECDiffieHellman.Create();
      encryption.ImportPkcs8PrivateKey(Convert.FromBase64String(_protector.Reveal(row.EncryptionKeyCipher, EncryptionPurpose)), out _);
      var signing = ECDsa.Create();
      signing.ImportPkcs8PrivateKey(Convert.FromBase64String(_protector.Reveal(row.SigningKeyCipher, SigningPurpose)), out _);
      return new CloudIdentity
      {
        CloudUrl = url,
        ServerId = row.ServerId,
        Credential = _protector.Reveal(row.CredentialCipher, CredentialPurpose),
        EncryptionKey = encryption,
        SigningKey = signing
      };
    }
    catch (Exception ex) when (ex is CryptographicException or FormatException)
    {
      _logger.LogError("The stored Cloud pairing could not be unlocked (was Encryption:Key changed?)");
      return null;
    }
  }

  public static string ServerVersion(CloudBridgeOptions options)
  {
    if (!string.IsNullOrWhiteSpace(options.ServerVersion)) return options.ServerVersion.Trim();
    var v = typeof(CloudPairingService).Assembly.GetName().Version;
    return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
  }

  public static string ExportPoint(ECDiffieHellman key) => Point(key.ExportParameters(false));

  public static string ExportPoint(ECDsa key) => Point(key.ExportParameters(false));

  private static string Point(ECParameters p)
  {
    var raw = new byte[65];
    raw[0] = 0x04;
    p.Q.X!.CopyTo(raw, 1 + 32 - p.Q.X.Length);
    p.Q.Y!.CopyTo(raw, 33 + 32 - p.Q.Y.Length);
    return Convert.ToBase64String(raw);
  }

  private static string? TryMessage(string body)
  {
    try { return JsonNode.Parse(body)?["message"]?.GetValue<string>(); }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
  }
}
