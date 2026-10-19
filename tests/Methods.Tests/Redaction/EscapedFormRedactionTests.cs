using System.Text.Encodings.Web;
using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Redaction;

/// <summary>AB#7015: a sensitive value is masked in its decoded form and in the escaped forms a serializer or URL writes.</summary>
public class EscapedFormRedactionTests
{
    private static readonly JsonSerializerOptions Default = new() { Encoder = JavaScriptEncoder.Default };

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static TheoryData<string> Secrets =>
    [
        "p@ss+word&1<x>", // Ariadne case 1: HTML-sensitive characters
        "pässwörd-ÄÖ", // case 2: non-ASCII
        "pa\"ss\\word", // case 3: quote and backslash
        "tab\there\u0007bell\nnl", // control characters
        "a/b'c`d", // slash, apostrophe, backtick
        "emoji-\U0001F600-end", // surrogate pair
        "all\"\\+&<>'\u00e4\t\u0007 mixed/"
    ];

    private static CkMethodRedactor RedactorFor(string secret) =>
        CkMethodRedactor.For(new HashSet<string> { "newPassword" },
            TestData.Request(DateTime.UtcNow.AddSeconds(5),
                parametersJson: JsonSerializer.Serialize(new Dictionary<string, string> { ["newPassword"] = secret })));

    [Theory]
    [MemberData(nameof(Secrets))]
    public void TheSecret_IsMasked_InEverySerializedForm(string secret)
    {
        var redactor = RedactorFor(secret);
        var forms = new List<string>
        {
            secret,
            JsonSerializer.Serialize(secret, Default)[1..^1],
            JsonSerializer.Serialize(secret, Relaxed)[1..^1],
            JsonSerializer.Serialize(secret, Default)[1..^1].ToLowerInvariant(),
            Uri.EscapeDataString(secret),
            JsonSerializer.Serialize(JsonSerializer.Serialize(secret, Default)[1..^1], Default)[1..^1] // JSON in JSON
        };
        // the whole serialized object, as a bus or serialization exception quotes a payload
        var payload = JsonSerializer.Serialize(new { pw = secret });
        var payloadRelaxed = JsonSerializer.Serialize(new { pw = secret }, Relaxed);

        foreach (var form in forms.Where(f => f.Length > 0))
        {
            var redacted = redactor.Redact($"failed with value {form} here");
            Assert.DoesNotContain(form, redacted);
            Assert.Contains(CkMethodRedactor.Mask, redacted);
        }

        foreach (var text in new[] { payload, payloadRelaxed })
        {
            var redacted = redactor.Redact("failed with " + text);
            Assert.DoesNotContain(secret, redacted);
            // nothing of the value survives: the decoded text is gone and so is its escaped form
            Assert.DoesNotContain(JsonSerializer.Serialize(secret, Default)[1..^1], redacted);
            Assert.DoesNotContain(JsonSerializer.Serialize(secret, Relaxed)[1..^1], redacted);
        }
    }

    [Fact]
    public void EscapedForm_IsMasked_InUpperAndLowerHexCase()
    {
        var redactor = RedactorFor("ä+");

        Assert.Equal("v=***", redactor.Redact("v=\\u00E4\\u002B"));
        Assert.Equal("v=***", redactor.Redact("v=\\u00e4\\u002b"));
        Assert.Equal("v=***", redactor.Redact("v=\u00e4+"));
    }

    [Fact]
    public void TheCallerToken_IsMasked_InItsEscapedForm_Too()
    {
        var token = "tok\"en\\+x";
        var request = TestData.Request(DateTime.UtcNow.AddSeconds(5)) with { CallerAccessToken = token };
        var redactor = CkMethodRedactor.ForToken(request);

        Assert.DoesNotContain("tok", redactor.Redact(JsonSerializer.Serialize(new { t = token })));
    }

    [Fact]
    public void UnrelatedText_AndNumbers_AreLeftAlone()
    {
        var redactor = CkMethodRedactor.For(new HashSet<string> { "pin" },
            TestData.Request(DateTime.UtcNow.AddSeconds(5), parametersJson: "{\"pin\":424242,\"other\":\"visible\"}"));

        Assert.Equal("pin *** other visible", redactor.Redact("pin 424242 other visible"));
    }

    [Theory]
    [InlineData("pass\"word\\1+&")]
    [InlineData("pässwörd")]
    public async Task ThrowingFilter_QuotingTheSerializedParameters_IsLoggedMasked(string secret)
    {
        var broken = new ThrowingFilter(() => new InvalidOperationException(
            "serializer failed on " + JsonSerializer.Serialize(new { newPassword = secret }) + " and " +
            JsonSerializer.Serialize(new { newPassword = secret }, Relaxed)));
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, broken);
        var request = TestData.Request(host.InSeconds(15),
            parametersJson: JsonSerializer.Serialize(new { currentPassword = "old", newPassword = secret }));

        var response = await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var logs = host.AllLogText();
        Assert.Contains("serializer failed", logs);
        Assert.DoesNotContain(secret, logs);
        Assert.DoesNotContain(JsonSerializer.Serialize(secret, Default)[1..^1], logs);
        Assert.DoesNotContain(JsonSerializer.Serialize(secret, Relaxed)[1..^1], logs);
    }

    [Fact]
    public async Task FailingHandler_QuotingTheSerializedParameters_IsLoggedMasked()
    {
        const string secret = "pa\"ss\\w+rd&<ä>\t";
        var handler = new ChangePasswordHandler((context, _) => throw new InvalidOperationException(
            "store rejected " + JsonSerializer.Serialize(new { newPassword = context.Parameters.NewPassword })));
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId);
        var request = TestData.Request(host.InSeconds(15),
            parametersJson: JsonSerializer.Serialize(new { currentPassword = "old", newPassword = secret }));

        var response = await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var logs = host.AllLogText();
        Assert.Contains("store rejected", logs);
        Assert.DoesNotContain(secret, logs);
        Assert.DoesNotContain(JsonSerializer.Serialize(secret, Default)[1..^1], logs);
    }

    private sealed class ThrowingFilter(Func<Exception> exception) : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Audit;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            throw exception();
    }
}
