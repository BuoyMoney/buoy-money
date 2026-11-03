using System.Security.Cryptography;
using BuoyAI.Infrastructure.Services.Bridge;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>
/// The bridge's HPKE (the app copy) against the published vector in RFC 9180 Appendix A.3.1.
/// The Cloud copy runs the same vector in its own tests, so neither side only agrees with itself.
/// </summary>
public class BridgeHpkeRfcVectorTests
{
  // RFC 9180 Appendix A.3.1 (DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, mode 0), sequence number 0.
  // Single-shot seal uses sequence 0, so this is the published vector for exactly what the bridge does.
  private const string InfoHex = "4f6465206f6e2061204772656369616e2055726e";
  private const string SkEm = "4995788ef4b9d6132b249ce59a77281493eb39af373d236a1fe415cb0c2d7beb";
  private const string PkEm = "04a92719c6195d5085104f469a8b9814d5838ff72b60501e2c4466e5e67b325ac98536d7b61a1af4b78e5b7f951c0900be863c403ce65c9bfcb9382657222d18c4";
  private const string SkRm = "f3ce7fdae57e1a310d87f1ebbde6f328be0a99cdbcadf4d6589cf29de4b8ffd2";
  private const string PkRm = "04fe8c19ce0905191ebc298a9245792531f26f0cece2460639e8bc39cb7f706a826a779b4cf969b8a0e539c7f62fb3d30ad6aa8f80e30f1d128aafd68a2ce72ea0";
  private const string PtHex = "4265617574792069732074727574682c20747275746820626561757479";
  private const string AadHex = "436f756e742d30";
  private const string CtHex = "5ad590bb8baa577f8619db35a36311226a896e7342a6d836d8b7bcd2f20b6c7f9076ac232e3ab2523f39513434";

  private static ECDiffieHellman Key(string dHex, string pointHex)
  {
    var q = Convert.FromHexString(pointHex);
    return ECDiffieHellman.Create(new ECParameters
    {
      Curve = ECCurve.NamedCurves.nistP256,
      D = Convert.FromHexString(dHex),
      Q = new ECPoint { X = q[1..33], Y = q[33..65] }
    });
  }

  [Fact]
  public void Open_decrypts_the_rfc_9180_ciphertext()
  {
    using var recipient = Key(SkRm, PkRm);
    var sealedBytes = Convert.FromHexString(PkEm + CtHex);

    var plain = BridgeHpke.Open(recipient, sealedBytes, Convert.FromHexString(InfoHex), Convert.FromHexString(AadHex));

    Assert.Equal(PtHex, Convert.ToHexString(plain).ToLowerInvariant());
  }

  [Fact]
  public void Seal_with_the_rfc_ephemeral_key_reproduces_the_rfc_9180_bytes()
  {
    using var ephemeral = Key(SkEm, PkEm);

    var sealedBytes = BridgeHpke.SealWith(ephemeral, Convert.FromHexString(PkRm), Convert.FromHexString(InfoHex),
      Convert.FromHexString(AadHex), Convert.FromHexString(PtHex));

    Assert.Equal(PkEm + CtHex, Convert.ToHexString(sealedBytes).ToLowerInvariant());
  }

  [Fact]
  public void The_rfc_ciphertext_does_not_open_with_the_wrong_info_or_aad()
  {
    using var recipient = Key(SkRm, PkRm);
    var sealedBytes = Convert.FromHexString(PkEm + CtHex);

    Assert.ThrowsAny<CryptographicException>(() => BridgeHpke.Open(recipient, sealedBytes, [1], Convert.FromHexString(AadHex)));
    Assert.ThrowsAny<CryptographicException>(() => BridgeHpke.Open(recipient, sealedBytes, Convert.FromHexString(InfoHex), [1]));
  }
}
