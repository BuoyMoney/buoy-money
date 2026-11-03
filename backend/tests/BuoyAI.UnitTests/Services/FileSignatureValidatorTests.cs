using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class FileSignatureValidatorTests
{
  private static readonly byte[] FakeExe = { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }; // "MZ.." DOS/PE header
  private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
  private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
  private static byte[] Webp()
  {
    var b = new byte[16];
    "RIFF"u8.CopyTo(b);
    "WEBP"u8.CopyTo(b.AsSpan(8));
    return b;
  }

  [Fact]
  public void RealImages_MatchTheirType()
  {
    FileSignatureValidator.IsContentConsistent(Jpeg, "image/jpeg").Should().BeTrue();
    FileSignatureValidator.IsContentConsistent(Png, "image/png").Should().BeTrue();
    FileSignatureValidator.IsContentConsistent(Webp(), "image/webp").Should().BeTrue();
  }

  [Fact]
  public void RenamedExecutable_IsRejectedAsAnyImageType()
  {
    // The exact attack from the QE ticket: a .exe with a spoofed image Content-Type.
    FileSignatureValidator.IsContentConsistent(FakeExe, "image/jpeg").Should().BeFalse();
    FileSignatureValidator.IsContentConsistent(FakeExe, "image/png").Should().BeFalse();
    FileSignatureValidator.IsContentConsistent(FakeExe, "image/webp").Should().BeFalse();
  }

  [Fact]
  public void CrossedImageSignatures_AreRejected()
  {
    // PNG bytes declared as JPEG, etc. — must not pass.
    FileSignatureValidator.IsContentConsistent(Png, "image/jpeg").Should().BeFalse();
    FileSignatureValidator.IsContentConsistent(Jpeg, "image/png").Should().BeFalse();
    FileSignatureValidator.IsContentConsistent(Jpeg, "image/webp").Should().BeFalse();
  }

  [Fact]
  public void ExistingDocTypes_StillWork()
  {
    FileSignatureValidator.IsContentConsistent("%PDF-1.7"u8, "application/pdf").Should().BeTrue();
    FileSignatureValidator.IsContentConsistent(FakeExe, "application/pdf").Should().BeFalse();
  }
}
