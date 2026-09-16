using System.Text;

namespace KeelMatrix.FixtureVault;

// This file owns the single decision that determines whether a counted fixture was actually
// decoded and content-inspected. Every supported combination of declared encoding, decodability,
// and decoded NUL content is decided here and nowhere else, so the path a fixture takes is
// readable from this file and provable by tests.
//
// Declared encoding             | Decoded content                   | Classification
// ------------------------------|-----------------------------------|-----------------------------
// none (no byte-order mark)     | valid UTF-8, no U+0000            | Inspected
// none (no byte-order mark)     | valid UTF-8 containing U+0000     | Uninspectable
// UTF-8 byte-order mark         | valid UTF-8, no U+0000            | Inspected
// UTF-8 byte-order mark         | contains U+0000                   | Uninspectable
// UTF-16/UTF-32 byte-order mark | valid text, no U+0000             | Inspected
// UTF-16/UTF-32 byte-order mark | contains U+0000                   | Uninspectable
// any declared encoding         | bytes no supported encoding reads | Uninspectable
// none (no byte-order mark)     | undecodable bytes with NUL bytes  | Uninspectable on Verify paths, binary asset elsewhere
// known binary extension        | never decoded by design           | Binary asset
//
// A byte-order mark proves which encoding a fixture declares; it never proves that the decoded
// text is trustworthy for content-dependent checks. U+0000 is a legal Unicode code point, so
// decoded text that contains it is treated as uninspectable content rather than as clean text.
internal enum ContentKind
{
    Inspected,

    BinaryAsset,

    Uninspectable
}

internal enum ContentDecodeOutcome
{
    Decoded,

    DecodedWithNul,

    Undecodable
}

internal sealed record ContentClassification(
    ContentDecodeOutcome Outcome,
    string? DeclaredEncodingName,
    string Text,
    bool HasNulBytes)
{
    internal const string Utf8EncodingName = "UTF-8";
    internal const string Utf16LittleEndianEncodingName = "UTF-16LE";
    internal const string Utf16BigEndianEncodingName = "UTF-16BE";
    internal const string Utf32LittleEndianEncodingName = "UTF-32LE";
    internal const string Utf32BigEndianEncodingName = "UTF-32BE";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

    internal bool IsInspectable => Outcome == ContentDecodeOutcome.Decoded;

    internal bool IsNonUtf8Declaration =>
        DeclaredEncodingName is not null &&
        !DeclaredEncodingName.Equals(Utf8EncodingName, StringComparison.Ordinal);

    // Bytes that no supported encoding decodes and that carry NUL characters prove no text
    // encoding at all. Only paths outside the Verify convention are classified as binary assets;
    // an accepted Verify baseline is reported as uninspected content instead.
    internal bool ProvesUndeclaredBinaryBlob =>
        Outcome == ContentDecodeOutcome.Undecodable &&
        DeclaredEncodingName is null &&
        HasNulBytes;

    // The per-file skip reason always describes the condition that was actually observed: a NUL
    // character in decoded content versus no encoding that could be established, naming the
    // declared encoding whenever a byte-order mark declared one.
    internal string SkippedReason => Outcome switch
    {
        ContentDecodeOutcome.DecodedWithNul when DeclaredEncodingName is null =>
            FixtureVaultContract.UndeclaredNulContentSkippedReason,
        ContentDecodeOutcome.DecodedWithNul =>
            FixtureVaultContract.DeclaredNulContentSkippedReason(DeclaredEncodingName!),
        ContentDecodeOutcome.Undecodable when DeclaredEncodingName is null =>
            FixtureVaultContract.UninspectableContentSkippedReason,
        _ =>
            FixtureVaultContract.UndecodableDeclaredContentSkippedReason(DeclaredEncodingName!)
    };

    internal string DiagnosticMessage => Outcome switch
    {
        ContentDecodeOutcome.DecodedWithNul when DeclaredEncodingName is null =>
            FixtureVaultContract.UndeclaredNulContentDiagnosticMessage,
        ContentDecodeOutcome.DecodedWithNul =>
            FixtureVaultContract.DeclaredNulContentDiagnosticMessage(DeclaredEncodingName!),
        ContentDecodeOutcome.Undecodable when DeclaredEncodingName is null =>
            FixtureVaultContract.UndecodableContentDiagnosticMessage,
        _ =>
            FixtureVaultContract.UndecodableDeclaredContentDiagnosticMessage(DeclaredEncodingName!)
    };

    internal string Remediation => Outcome switch
    {
        ContentDecodeOutcome.DecodedWithNul => FixtureVaultContract.NulContentRemediation,
        ContentDecodeOutcome.Undecodable when DeclaredEncodingName is null =>
            FixtureVaultContract.UndeclaredEncodingRemediation,
        _ => FixtureVaultContract.DeclaredEncodingRemediation(DeclaredEncodingName!)
    };

    internal static ContentClassification Classify(byte[] bytes)
    {
        DeclaredEncoding declaration = DetectDeclaredEncoding(bytes);
        bool hasNulBytes = bytes.AsSpan(declaration.BomLength).Contains((byte)0);
        if (!TryDecode(bytes, declaration, out string text))
        {
            return new ContentClassification(
                ContentDecodeOutcome.Undecodable,
                declaration.Name,
                string.Empty,
                hasNulBytes);
        }

        return new ContentClassification(
            text.Contains('\0') ? ContentDecodeOutcome.DecodedWithNul : ContentDecodeOutcome.Decoded,
            declaration.Name,
            text,
            hasNulBytes);
    }

    internal static ContentKind Resolve(
        ContentClassification classification,
        bool isKnownBinaryExtension,
        bool isVerifyFixture)
    {
        if (isKnownBinaryExtension)
        {
            return ContentKind.BinaryAsset;
        }

        if (classification.IsInspectable)
        {
            return ContentKind.Inspected;
        }

        return classification.ProvesUndeclaredBinaryBlob && !isVerifyFixture
            ? ContentKind.BinaryAsset
            : ContentKind.Uninspectable;
    }

    private static DeclaredEncoding DetectDeclaredEncoding(byte[] bytes)
    {
        // UTF-32 is checked first because FF FE 00 00 is also a UTF-16 little-endian byte-order mark.
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new DeclaredEncoding(Utf32BigEndianEncodingName, 4, StrictUtf32BigEndian);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new DeclaredEncoding(Utf32LittleEndianEncodingName, 4, StrictUtf32LittleEndian);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new DeclaredEncoding(Utf16BigEndianEncodingName, 2, StrictUtf16BigEndian);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new DeclaredEncoding(Utf16LittleEndianEncodingName, 2, StrictUtf16LittleEndian);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new DeclaredEncoding(Utf8EncodingName, 3, StrictUtf8);
        }

        // No byte-order mark: the only encoding that may be assumed is UTF-8, and the bytes still
        // have to prove it by decoding.
        return new DeclaredEncoding(null, 0, StrictUtf8);
    }

    private static bool TryDecode(byte[] bytes, DeclaredEncoding declaration, out string text)
    {
        try
        {
            text = declaration.Encoding.GetString(bytes, declaration.BomLength, bytes.Length - declaration.BomLength);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private readonly record struct DeclaredEncoding(string? Name, int BomLength, Encoding Encoding);
}
