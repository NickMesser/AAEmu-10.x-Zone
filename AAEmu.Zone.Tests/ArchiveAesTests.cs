using AAEmu.Commons.Utils.AAPak;

namespace AAEmu.Zone.Tests;

public sealed class ArchiveAesTests
{
    [Test]
    public async Task ZeroIvCbcMatchesIndependentVectorAndPreservesFailureContract()
    {
        // NIST SP 800-38A, F.2.1: absorb its nonzero IV into the first plaintext
        // block so this zero-IV archive API must produce the same four ciphertext blocks.
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var referenceIv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var plaintext = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51" +
            "30c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        for (var index = 0; index < referenceIv.Length; index++)
            plaintext[index] ^= referenceIv[index];
        var ciphertext = Convert.FromHexString(
            "7649abac8119b246cee98e9b12e9197d5086cb9b507219ee95db113a917678b2" +
            "73bed6b8e3c1743b7116e69e222295163ff1caa1681fac09120eca307586e1a7");

        await Assert.That(AAPakFileHeader.EncryptAES(plaintext, key, true).SequenceEqual(ciphertext)).IsTrue();
        await Assert.That(AAPakFileHeader.EncryptAES(ciphertext, key, false).SequenceEqual(plaintext)).IsTrue();

        foreach (var keyBytes in new[] { 16, 24, 32 })
        {
            var supportedKey = new byte[keyBytes];
            var encrypted = AAPakFileHeader.EncryptAES(plaintext, supportedKey, true);
            await Assert.That(AAPakFileHeader.EncryptAES(encrypted, supportedKey, false).SequenceEqual(plaintext)).IsTrue();
            await Assert.That(AAPakFileHeader.EncryptAES([], supportedKey, true).Length).IsEqualTo(0);
        }

        foreach (var encrypt in new[] { true, false })
        {
            AAPakFileHeader.LastAESError = string.Empty;
            await Assert.That(AAPakFileHeader.EncryptAES(new byte[15], key, encrypt) is null).IsTrue();
            await Assert.That(string.IsNullOrEmpty(AAPakFileHeader.LastAESError)).IsFalse();

            AAPakFileHeader.LastAESError = string.Empty;
            await Assert.That(AAPakFileHeader.EncryptAES(plaintext, new byte[15], encrypt) is null).IsTrue();
            await Assert.That(string.IsNullOrEmpty(AAPakFileHeader.LastAESError)).IsFalse();
        }

        // Successful calls historically leave the previous error string untouched.
        var previousError = AAPakFileHeader.LastAESError;
        await Assert.That(AAPakFileHeader.EncryptAES(ciphertext, key, false).SequenceEqual(plaintext)).IsTrue();
        await Assert.That(AAPakFileHeader.LastAESError).IsEqualTo(previousError);
    }
}
