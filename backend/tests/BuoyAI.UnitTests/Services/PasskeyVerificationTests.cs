using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Passkey sign-in used to accept any signature of 32 bytes or more, and registration stored a hash
/// of the attestation object instead of the public key, so anyone holding a passkey's credential id
/// could get a login token. A software authenticator (a real P-256 key) drives the real
/// registration and sign-in code here, the way a phone or browser would.
/// </summary>
public class PasskeyVerificationTests
{
  private const string RpId = "localhost"; // WebAuthnSettings default
  private const string Origin = "http://localhost:3000"; // WebAuthnSettings default
  private const int UserId = 41;

  private static string B64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private sealed class SoftAuthenticator : IDisposable
  {
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    public byte[] CoseKey()
    {
      var q = Key.ExportParameters(false).Q;
      var w = new CborWriter();
      w.WriteStartMap(5);
      w.WriteInt32(1); w.WriteInt32(2);    // kty: EC2
      w.WriteInt32(3); w.WriteInt32(-7);   // alg: ES256
      w.WriteInt32(-1); w.WriteInt32(1);   // crv: P-256
      w.WriteInt32(-2); w.WriteByteString(q.X!);
      w.WriteInt32(-3); w.WriteByteString(q.Y!);
      w.WriteEndMap();
      return w.Encode();
    }

    public static byte[] AuthData(string rpId, byte flags, uint signCount, byte[]? attested = null)
    {
      var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))) { flags };
      data.AddRange(new[] { (byte)(signCount >> 24), (byte)(signCount >> 16), (byte)(signCount >> 8), (byte)signCount });
      if (attested != null)
        data.AddRange(attested);
      return data.ToArray();
    }

    public string RegistrationResponse(string challenge, string rpId = RpId)
    {
      var attested = new List<byte>(new byte[16]) { (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length };
      attested.AddRange(CredentialId);
      attested.AddRange(CoseKey());
      var w = new CborWriter();
      w.WriteStartMap(3);
      w.WriteTextString("fmt"); w.WriteTextString("none");
      w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
      w.WriteTextString("authData"); w.WriteByteString(AuthData(rpId, 0x45, 0, attested.ToArray())); // UP | UV | AT
      w.WriteEndMap();
      return JsonSerializer.Serialize(new
      {
        id = B64Url(CredentialId),
        type = "public-key",
        response = new { attestationObject = B64Url(w.Encode()), clientDataJSON = ClientData("webauthn.create", challenge) },
      });
    }

    public static string ClientData(string type, string challenge, string origin = Origin) =>
      B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin })));

    /// <summary>A sign-in assertion; signer defaults to this authenticator's key.</summary>
    public string AssertionResponse(string challenge, string rpId = RpId, ECDsa? signer = null, byte[]? signatureOverride = null,
      string origin = Origin, byte flags = 0x05, string? clientDataOverride = null)
    {
      var authData = AuthData(rpId, flags, 1); // default UP | UV
      var clientData = ClientData("webauthn.get", challenge, origin);
      var signed = authData.Concat(SHA256.HashData(Convert.FromBase64String(Pad(clientData)))).ToArray();
      var signature = signatureOverride ??
        (signer ?? Key).SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
      return JsonSerializer.Serialize(new
      {
        id = B64Url(CredentialId),
        type = "public-key",
        response = new { authenticatorData = B64Url(authData), clientDataJSON = clientDataOverride ?? clientData, signature = B64Url(signature) },
      });
    }

    private static string Pad(string b64Url)
    {
      var s = b64Url.Replace('-', '+').Replace('_', '/');
      return s + new string('=', (4 - s.Length % 4) % 4);
    }

    public void Dispose() => Key.Dispose();
  }

  private static (TwoFactorService Service, BuoyAIDbContext Db) CreateService(WebAuthnSettings? settings = null)
  {
    var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase($"passkeys-{Guid.NewGuid()}").Options);
    db.Users.Add(new User
    {
      Id = UserId, Username = "wren", PasswordHash = "hash", Email = "wren@example.com",
      FirstName = "Wren", LastName = "Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
      MfaType = MfaType.None,
    });
    db.SaveChanges();
    var users = new Mock<IUserRepository>();
    users.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => db.Users.SingleOrDefault(u => u.Id == id));
    users.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => { db.SaveChanges(); return u; });
    var service = new TwoFactorService(db, Mock.Of<IEmailService>(), users.Object,
      Mock.Of<ILogger<TwoFactorService>>(), Options.Create(settings ?? new WebAuthnSettings()), Mock.Of<BuoyAI.Core.Interfaces.IEncryptionService>());
    return (service, db);
  }

  private static async Task Register(TwoFactorService service, SoftAuthenticator authenticator)
  {
    var options = await service.InitiatePasskeyRegistrationAsync(UserId, new PasskeyRegistrationInitDto { DisplayName = "Wren" });
    await service.CompletePasskeyRegistrationAsync(UserId, new PasskeyRegistrationCompleteDto
    {
      CredentialResponse = authenticator.RegistrationResponse(options.Challenge),
      DeviceName = "Pixel",
    });
  }

  private static async Task<string> SignInChallenge(TwoFactorService service) =>
    (await service.InitiatePasskeyAuthenticationAsync(new PasskeyAuthenticationInitDto())).Challenge;

  private static Task<User> SignIn(TwoFactorService service, string assertion) =>
    service.CompletePasskeyAuthenticationAsync(new PasskeyAuthenticationCompleteDto { AssertionResponse = assertion });

  [Fact]
  public async Task RegistrationOptions_AdvertiseOnlyTheAlgorithmsTheVerifierChecks()
  {
    var (service, _) = CreateService();

    var options = await service.InitiatePasskeyRegistrationAsync(UserId, new PasskeyRegistrationInitDto { DisplayName = "Wren" });

    using var json = System.Text.Json.JsonDocument.Parse(options.CredentialCreationOptions);
    var algs = json.RootElement.GetProperty("pubKeyCredParams").EnumerateArray()
      .Select(p => p.GetProperty("alg").GetInt32()).ToArray();
    algs.Should().Equal(-7, -257); // ES256, RS256
  }

  [Fact]
  public async Task Registration_StoresTheRealPublicKey()
  {
    var (service, db) = CreateService();
    using var authenticator = new SoftAuthenticator();

    await Register(service, authenticator);

    var stored = Convert.FromBase64String(db.UserPasskeys.Single().PublicKey);
    stored.Should().Equal(authenticator.CoseKey());
    WebAuthnVerifier.IsSupportedKey(stored).Should().BeTrue();
  }

  [Fact]
  public async Task SignIn_WithAValidSignature_ReturnsTheUser()
  {
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);

    var user = await SignIn(service, authenticator.AssertionResponse(await SignInChallenge(service)));

    user.Id.Should().Be(UserId);
  }

  [Fact]
  public async Task SignIn_WithAnUnsignedAssertion_IsRefused()
  {
    // The old check passed any 32+ byte signature: this is that attack.
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);

    var forged = authenticator.AssertionResponse(await SignInChallenge(service), signatureOverride: RandomNumberGenerator.GetBytes(72));
    var act = () => SignIn(service, forged);

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid signature*");
  }

  [Fact]
  public async Task SignIn_SignedByAnotherKey_IsRefused()
  {
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    await Register(service, authenticator);

    var act = () => SignIn(service, authenticator.AssertionResponse(SignInChallengeSync(service), signer: attacker));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid signature*");
  }

  [Fact]
  public async Task SignIn_WithAChallengeTheServerNeverIssued_IsRefused()
  {
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);
    await SignInChallenge(service); // an open challenge exists, but not this one

    var act = () => SignIn(service, authenticator.AssertionResponse(B64Url(RandomNumberGenerator.GetBytes(32))));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*challenge*");
  }

  [Fact]
  public async Task SignIn_MadeForAnotherSite_IsRefused()
  {
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);

    var act = () => SignIn(service, authenticator.AssertionResponse(SignInChallengeSync(service), rpId: "evil.example"));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid signature*");
  }

  [Fact]
  public async Task Registration_ForAnotherSite_IsRefused()
  {
    var (service, db) = CreateService();
    using var authenticator = new SoftAuthenticator();
    var options = await service.InitiatePasskeyRegistrationAsync(UserId, new PasskeyRegistrationInitDto { DisplayName = "Wren" });

    var act = () => service.CompletePasskeyRegistrationAsync(UserId, new PasskeyRegistrationCompleteDto
    {
      CredentialResponse = authenticator.RegistrationResponse(options.Challenge, rpId: "evil.example"),
    });

    await act.Should().ThrowAsync<InvalidOperationException>();
    db.UserPasskeys.Should().BeEmpty();
  }

  [Fact]
  public async Task SignIn_WithAPasskeySavedTheOldWay_AsksToSetItUpAgain()
  {
    var (service, db) = CreateService();
    using var authenticator = new SoftAuthenticator();
    db.UserPasskeys.Add(new UserPasskey
    {
      UserId = UserId, CredentialId = B64Url(authenticator.CredentialId), Name = "Old",
      PublicKey = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("attestation"))), // what registration used to store
      IsActive = true, CreatedAt = DateTime.UtcNow,
    });
    await db.SaveChangesAsync();

    var act = () => SignIn(service, authenticator.AssertionResponse(SignInChallengeSync(service)));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*set up again*");
  }

  [Fact]
  public void Verifier_AcceptsAnRs256Key()
  {
    using var rsa = RSA.Create(2048);
    var p = rsa.ExportParameters(false);
    var w = new CborWriter();
    w.WriteStartMap(4);
    w.WriteInt32(1); w.WriteInt32(3);      // kty: RSA
    w.WriteInt32(3); w.WriteInt32(-257);   // alg: RS256
    w.WriteInt32(-1); w.WriteByteString(p.Modulus!);
    w.WriteInt32(-2); w.WriteByteString(p.Exponent!);
    w.WriteEndMap();
    var authData = SoftAuthenticator.AuthData(RpId, 0x01, 1);
    var clientData = Encoding.UTF8.GetBytes("{\"type\":\"webauthn.get\"}");
    var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
    var signature = rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    WebAuthnVerifier.VerifyAssertion(authData, clientData, signature, w.Encode(), RpId).Should().BeTrue();
    signature[0] ^= 0xFF;
    WebAuthnVerifier.VerifyAssertion(authData, clientData, signature, w.Encode(), RpId).Should().BeFalse();
  }

  [Fact]
  public void Verifier_RefusesAnAssertionWithoutUserPresence()
  {
    using var authenticator = new SoftAuthenticator();
    var authData = SoftAuthenticator.AuthData(RpId, 0x00, 1); // no UP flag
    var clientData = Encoding.UTF8.GetBytes("{}");
    var signature = authenticator.Key.SignData(authData.Concat(SHA256.HashData(clientData)).ToArray(),
      HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    WebAuthnVerifier.VerifyAssertion(authData, clientData, signature, authenticator.CoseKey(), RpId).Should().BeFalse();
  }

  [Fact]
  public async Task SignIn_FromAnotherOrigin_IsRefused()
  {
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);

    var act = () => SignIn(service, authenticator.AssertionResponse(SignInChallengeSync(service), origin: "https://evil.localhost"));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*didn't come from Buoy Money*");
  }

  [Fact]
  public async Task SignIn_FromAnAllowedAppOrigin_Works()
  {
    var settings = new WebAuthnSettings { AllowedOrigins = { "android:apk-key-hash:abc" } };
    var (service, _) = CreateService(settings);
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);

    var user = await SignIn(service, authenticator.AssertionResponse(SignInChallengeSync(service), origin: "android:apk-key-hash:abc"));

    user.Id.Should().Be(UserId);
  }

  [Fact]
  public async Task SignIn_WithoutUserVerification_IsRefusedWhenRequired()
  {
    var (service, _) = CreateService(new WebAuthnSettings { UserVerification = "required" });
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);

    var act = () => SignIn(service, authenticator.AssertionResponse(SignInChallengeSync(service), flags: 0x01)); // UP only

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid signature*");
  }

  [Fact]
  public async Task SignIn_ReplayingAUsedChallenge_IsRefused()
  {
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);
    var assertion = authenticator.AssertionResponse(SignInChallengeSync(service));
    await SignIn(service, assertion);

    var act = () => SignIn(service, assertion);

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*challenge*");
  }

  [Fact]
  public async Task SignIn_WithAnExpiredChallenge_IsRefused()
  {
    var (service, db) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);
    var challenge = SignInChallengeSync(service);
    db.PasskeyChallenges.Single(c => c.Challenge == challenge).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
    await db.SaveChangesAsync();

    var act = () => SignIn(service, authenticator.AssertionResponse(challenge));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*challenge*");
  }

  [Fact]
  public async Task SignIn_WithAStepUpChallengeForAnotherUser_IsRefused()
  {
    var (service, db) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);
    var challenge = SignInChallengeSync(service);
    db.PasskeyChallenges.Single(c => c.Challenge == challenge).UserId = UserId + 1; // issued to someone else's step-up
    await db.SaveChangesAsync();

    var act = () => SignIn(service, authenticator.AssertionResponse(challenge));

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different account*");
  }

  [Fact]
  public async Task SignIn_WithClientDataChangedAfterSigning_IsRefused()
  {
    // Signed for one issued challenge, then clientDataJSON swapped to another issued challenge.
    var (service, _) = CreateService();
    using var authenticator = new SoftAuthenticator();
    await Register(service, authenticator);
    var signedFor = SignInChallengeSync(service);
    var other = SignInChallengeSync(service);

    var tampered = authenticator.AssertionResponse(signedFor,
      clientDataOverride: SoftAuthenticator.ClientData("webauthn.get", other));
    var act = () => SignIn(service, tampered);

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid signature*");
  }

  [Fact]
  public void Settings_OriginMatchIsExact()
  {
    var settings = new WebAuthnSettings { Origin = "https://buoy-ai.com" };

    settings.IsAllowedOrigin("https://buoy-ai.com").Should().BeTrue();
    settings.IsAllowedOrigin("https://staging.buoy-ai.com").Should().BeFalse();
    settings.IsAllowedOrigin("https://buoy-ai.com.evil.example").Should().BeFalse();
    settings.IsAllowedOrigin(null).Should().BeFalse();
  }

  private static string SignInChallengeSync(TwoFactorService service) => SignInChallenge(service).GetAwaiter().GetResult();
}
