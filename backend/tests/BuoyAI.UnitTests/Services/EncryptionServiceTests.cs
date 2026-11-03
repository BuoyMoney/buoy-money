using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class EncryptionServiceTests
{
  private readonly Mock<IConfiguration> _mockConfiguration;
  private readonly EncryptionService _encryptionService;
  private const string TestEncryptionKey = "test-encryption-key-32-characters";

  public EncryptionServiceTests()
  {
    _mockConfiguration = new Mock<IConfiguration>();
    _mockConfiguration.Setup(x => x["Encryption:Key"]).Returns(TestEncryptionKey);
    _mockConfiguration.Setup(x => x["Encryption:WriteV2"]).Returns("true");
    
    _encryptionService = new EncryptionService(_mockConfiguration.Object);
  }

  [Fact]
  public void Encrypt_WithValidPlainText_ShouldReturnEncryptedText()
  {
    // Arrange
    var plainText = "sensitive-data-to-encrypt";

    // Act
    var encryptedText = _encryptionService.Encrypt(plainText);

    // Assert
    encryptedText.Should().NotBeNullOrEmpty();
    encryptedText.Should().NotBe(plainText);
  }

  [Fact]
  public void Encrypt_WithEmptyString_ShouldReturnEmptyString()
  {
    // Arrange
    var plainText = string.Empty;

    // Act
    var encryptedText = _encryptionService.Encrypt(plainText);

    // Assert
    encryptedText.Should().Be(string.Empty);
  }

  [Fact]
  public void Decrypt_WithValidEncryptedText_ShouldReturnOriginalText()
  {
    // Arrange
    var originalText = "sensitive-data-to-encrypt";
    var encryptedText = _encryptionService.Encrypt(originalText);

    // Act
    var decryptedText = _encryptionService.Decrypt(encryptedText);

    // Assert
    decryptedText.Should().Be(originalText);
  }

  [Fact]
  public void Decrypt_WithEmptyString_ShouldReturnEmptyString()
  {
    // Arrange
    var encryptedText = string.Empty;

    // Act
    var decryptedText = _encryptionService.Decrypt(encryptedText);

    // Assert
    decryptedText.Should().Be(string.Empty);
  }

  [Fact]
  public void EncryptDecrypt_RoundTrip_ShouldPreserveOriginalData()
  {
    // Arrange
    var testData = new[]
    {
      "simple text",
      "text with spaces and punctuation!",
      "numbers123456789",
      "special characters: !@#$%^&*()",
      "multi-line\ntext\nwith\nbreaks"
    };

    foreach (var originalText in testData)
    {
      // Act
      var encrypted = _encryptionService.Encrypt(originalText);
      var decrypted = _encryptionService.Decrypt(encrypted);

      // Assert
      decrypted.Should().Be(originalText, $"Round trip should preserve '{originalText}'");
    }
  }

  [Fact]
  public void Encrypt_SameTextTwice_ShouldReturnDifferentCipherText()
  {
    // Arrange
    var plainText = "same-text-to-encrypt";

    // Act
    var encrypted1 = _encryptionService.Encrypt(plainText);
    var encrypted2 = _encryptionService.Encrypt(plainText);

    // Assert
    encrypted1.Should().NotBe(encrypted2, "Same plaintext should produce different ciphertext due to random IV");
    
    // Both should decrypt to the same original text
    _encryptionService.Decrypt(encrypted1).Should().Be(plainText);
    _encryptionService.Decrypt(encrypted2).Should().Be(plainText);
  }

  [Fact]
  public void Constructor_WithoutEncryptionKey_ShouldThrowException()
  {
    // Arrange
    var mockConfigWithoutKey = new Mock<IConfiguration>();
    mockConfigWithoutKey.Setup(x => x["Encryption:Key"]).Returns((string?)null);

    // Act & Assert
    var exception = Assert.Throws<InvalidOperationException>(() => 
      new EncryptionService(mockConfigWithoutKey.Object));
    
    exception.Message.Should().Contain("Encryption key not configured");
  }

  [Fact]
  public void Decrypt_WithInvalidCipherText_ShouldThrowException()
  {
    // Arrange
    var invalidCipherText = "invalid-base64-cipher-text";

    // Act & Assert
    var exception = Assert.Throws<InvalidOperationException>(() => 
      _encryptionService.Decrypt(invalidCipherText));
    
    exception.Message.Should().Contain("Failed to decrypt data");
  }

  // Byte-for-byte the pre-v2 algorithm, to produce ciphertext like the rows already in the database.
  private static string LegacyEncrypt(string key, string plainText)
  {
    using var aes = System.Security.Cryptography.Aes.Create();
    aes.Key = System.Text.Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));
    aes.GenerateIV();
    using var ms = new MemoryStream();
    ms.Write(aes.IV, 0, aes.IV.Length);
    using (var cs = new System.Security.Cryptography.CryptoStream(ms, aes.CreateEncryptor(), System.Security.Cryptography.CryptoStreamMode.Write))
    using (var sw = new StreamWriter(cs))
    {
      sw.Write(plainText);
    }
    return Convert.ToBase64String(ms.ToArray());
  }

  [Fact]
  public void Encrypt_WritesVersionedFormat()
  {
    var encrypted = _encryptionService.Encrypt("access-sandbox-abc123");

    encrypted.Should().StartWith("v2:");
    _encryptionService.IsCurrentFormat(encrypted).Should().BeTrue();
  }

  [Fact]
  public void Decrypt_LegacyCiphertext_StillDecrypts()
  {
    var legacy = LegacyEncrypt(TestEncryptionKey, "access-sandbox-legacy-token");

    _encryptionService.IsCurrentFormat(legacy).Should().BeFalse();
    _encryptionService.Decrypt(legacy).Should().Be("access-sandbox-legacy-token");
  }

  [Fact]
  public void Decrypt_LegacyCiphertext_WithShortKey_StillDecrypts()
  {
    var cfg = new Mock<IConfiguration>();
    cfg.Setup(x => x["Encryption:Key"]).Returns("short-key");
    var svc = new EncryptionService(cfg.Object);

    svc.Decrypt(LegacyEncrypt("short-key", "token")).Should().Be("token");
  }

  [Fact]
  public void Decrypt_TamperedV2Ciphertext_IsDetected()
  {
    var encrypted = _encryptionService.Encrypt("do-not-tamper");
    var blob = Convert.FromBase64String(encrypted.Substring(3));
    blob[^1] ^= 0x01;
    var tampered = "v2:" + Convert.ToBase64String(blob);

    var act = () => _encryptionService.Decrypt(tampered);

    act.Should().Throw<InvalidOperationException>().WithMessage("*Failed to decrypt data*");
  }

  [Fact]
  public void Decrypt_V2WithWrongKey_Fails()
  {
    var encrypted = _encryptionService.Encrypt("secret");
    var cfg = new Mock<IConfiguration>();
    cfg.Setup(x => x["Encryption:Key"]).Returns("a-completely-different-key-of-32-chars!");
    var other = new EncryptionService(cfg.Object);

    var act = () => other.Decrypt(encrypted);

    act.Should().Throw<InvalidOperationException>();
  }

  [Fact]
  public void EncryptDecrypt_Unicode_RoundTrips()
  {
    var text = "tökén-日本語";
    _encryptionService.Decrypt(_encryptionService.Encrypt(text)).Should().Be(text);
  }

  [Fact]
  public void WriteV2Unset_Encrypt_WritesLegacyFormat_ReadableByTheOldAlgorithm()
  {
    var cfg = new Mock<IConfiguration>();
    cfg.Setup(x => x["Encryption:Key"]).Returns(TestEncryptionKey);
    var svc = new EncryptionService(cfg.Object);

    var encrypted = svc.Encrypt("access-sandbox-legacy-write");

    svc.WriteV2.Should().BeFalse();
    encrypted.Should().NotStartWith("v2:");
    svc.IsCurrentFormat(encrypted).Should().BeFalse();
    // Old algorithm, written out independently of the service.
    var full = Convert.FromBase64String(encrypted);
    using var aes = System.Security.Cryptography.Aes.Create();
    aes.Key = System.Text.Encoding.UTF8.GetBytes(TestEncryptionKey.PadRight(32).Substring(0, 32));
    aes.IV = full[..16];
    using var dec = aes.CreateDecryptor();
    dec.TransformFinalBlock(full, 16, full.Length - 16).Should().Equal(System.Text.Encoding.UTF8.GetBytes("access-sandbox-legacy-write"));
    svc.Decrypt(encrypted).Should().Be("access-sandbox-legacy-write");
  }

  [Fact]
  public void WriteV2True_Encrypt_WritesV2()
  {
    _encryptionService.WriteV2.Should().BeTrue();
    _encryptionService.Encrypt("x").Should().StartWith("v2:");
  }
}
