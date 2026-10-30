using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>Reasons and field name of the refusals of the idempotency key (<c>METHOD_VALIDATION_FAILED</c>).</summary>
public static class CkMethodIdempotencyRefusal
{
    /// <summary>The <c>field</c> of the refusal: the idempotency key of the request.</summary>
    public const string Field = "idempotencyKey";

    /// <summary>
    ///     The <c>reason</c> when a call carries a key, the method has sensitive parameters and no fingerprint key is
    ///     configured (OD-4 G1, fail closed): without a key the fingerprint of a password could be guessed offline.
    /// </summary>
    public const string FingerprintKeyMissing = "fingerprint-key-missing";
}

/// <summary>The fingerprints of one request, or the fact that none can be made.</summary>
/// <param name="Primary">The fingerprint to store and compare, <c>null</c> when <paramref name="KeyMissing" />.</param>
/// <param name="Alternates">
///     Fingerprints of the same request under other keys that a stored fingerprint may come from (the previous key, or the
///     unkeyed SHA-256 of a method without sensitive parameters made before a key was configured).
/// </param>
/// <param name="KeyMissing">The method has sensitive parameters and no key is configured: the call must be refused.</param>
public sealed record CkMethodFingerprintResult(
    CkMethodParametersFingerprint? Primary,
    IReadOnlyList<CkMethodParametersFingerprint> Alternates,
    bool KeyMissing);

/// <summary>
///     Computes the fingerprint of the <b>raw</b> request before redaction (OD-4 row 2, G1). Never logs.
/// </summary>
public interface ICkMethodParametersFingerprinter
{
    /// <summary>
    ///     Fingerprint over <c>ckTypeId</c>, <c>targetRtId</c> and the canonical parameters.
    /// </summary>
    /// <param name="rawRequest">The raw request (with the real parameter values).</param>
    /// <param name="hasSensitiveParameters">The method has sensitive parameters, or it is unknown (fail closed).</param>
    CkMethodFingerprintResult Compute(CkMethodRequest rawRequest, bool hasSensitiveParameters);
}

/// <summary>
///     HMAC-SHA-256 over the canonical form of type, target and parameters with the current key; a method without
///     sensitive parameters uses plain SHA-256 when no key is configured. The canonical form sorts object members, drops
///     duplicate members (the last one counts, like the handler's deserializer), normalizes numbers and keeps array order.
/// </summary>
internal sealed class CkMethodParametersFingerprinter : ICkMethodParametersFingerprinter
{
    /// <summary>Key id of the unkeyed SHA-256 fingerprint.</summary>
    internal const string UnkeyedKeyId = "sha256";

    private const string Domain = "octo-ck-method-fingerprint-v1";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly (string Id, byte[] Secret)? _current;
    private readonly (string Id, byte[] Secret)? _previous;

    public CkMethodParametersFingerprinter(IOptions<CkMethodFingerprintOptions> options)
    {
        var value = options.Value;
        _current = Resolve(value.Current);
        _previous = _current == null ? null : Resolve(value.Previous);
    }

    public CkMethodFingerprintResult Compute(CkMethodRequest rawRequest, bool hasSensitiveParameters)
    {
        ArgumentNullException.ThrowIfNull(rawRequest);

        var message = BuildMessage(rawRequest);
        if (_current is { } current)
        {
            var alternates = new List<CkMethodParametersFingerprint>(2);
            if (_previous is { } previous)
            {
                alternates.Add(Hmac(previous, message));
            }

            if (!hasSensitiveParameters)
            {
                // made before a key was configured
                alternates.Add(Sha256(message));
            }

            return new CkMethodFingerprintResult(Hmac(current, message), alternates, false);
        }

        return hasSensitiveParameters
            ? new CkMethodFingerprintResult(null, [], true)
            : new CkMethodFingerprintResult(Sha256(message), [], false);
    }

    private static (string Id, byte[] Secret)? Resolve(CkMethodFingerprintKeyOptions key) =>
        key.IsEmpty || string.IsNullOrWhiteSpace(key.Id) || string.IsNullOrWhiteSpace(key.Secret) ||
        !CkMethodFingerprintOptionsValidator.TryDecode(key.Secret, out var bytes)
            ? null
            : (key.Id, bytes);

    private static CkMethodParametersFingerprint Hmac((string Id, byte[] Secret) key, byte[] message) =>
        new(HMACSHA256.HashData(key.Secret, message), key.Id);

    private static CkMethodParametersFingerprint Sha256(byte[] message) =>
        new(SHA256.HashData(message), UnkeyedKeyId);

    private static byte[] BuildMessage(CkMethodRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Append(buffer, Domain);
        Append(buffer, request.CkTypeId ?? string.Empty);
        Append(buffer, request.TargetRtId ?? string.Empty);
        Append(buffer, Canonicalize(request.ParametersJson));
        return buffer.WrittenSpan.ToArray();
    }

    private static void Append(ArrayBufferWriter<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(4), bytes.Length);
        buffer.Advance(4);
        buffer.Write(bytes);
    }

    internal static string Canonicalize(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(parametersJson, new JsonDocumentOptions { MaxDepth = 64 });
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                Write(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            // not JSON: the handler refuses it anyway; hash the text as it is (a distinct form, never mixed with JSON)
            return "raw:" + parametersJson;
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // last member of a duplicated name wins (as in the handler's deserializer), members sorted by ordinal name
                var members = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    members[property.Name] = property.Value;
                }

                foreach (var (name, value) in members)
                {
                    writer.WritePropertyName(name);
                    Write(writer, value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>1, 1.0 and 1e0 are the same number.</summary>
    private static string NormalizeNumber(JsonElement number)
    {
        if (number.TryGetDecimal(out var d))
        {
            return (d / 1.000000000000000000000000000000000m).ToString("G29", CultureInfo.InvariantCulture);
        }

        return number.TryGetDouble(out var f) && double.IsFinite(f)
            ? f.ToString("R", CultureInfo.InvariantCulture)
            : number.GetRawText();
    }
}
