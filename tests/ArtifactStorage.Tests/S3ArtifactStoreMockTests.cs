using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FakeItEasy;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.S3;

namespace ArtifactStorage.Tests;

/// <summary>
///     Request-shape tests for the S3 provider with a faked client; the behaviour against a real S3 API is
///     covered by <see cref="S3ArtifactStoreMinioTests" />.
/// </summary>
public sealed class S3ArtifactStoreMockTests
{
    private const int PartSize = ArtifactStorageOptionsValidator.MinS3PartSize;
    private readonly IAmazonS3 _client = A.Fake<IAmazonS3>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private S3ArtifactStore CreateStore(string? sse = null)
    {
        return new S3ArtifactStore(_client, new S3ArtifactStorageOptions
        {
            Bucket = "bucket",
            Region = "eu-central-1",
            MultipartPartSizeBytes = PartSize,
            ServerSideEncryption = sse
        });
    }

    [Fact]
    public async Task SmallContent_UsesSinglePutWithMetadataAndEncryption()
    {
        PutObjectRequest? captured = null;
        long capturedLength = -1;
        A.CallTo(() => _client.PutObjectAsync(A<PutObjectRequest>._, A<CancellationToken>._))
            .Invokes((PutObjectRequest r, CancellationToken _) =>
            {
                captured = r;
                capturedLength = r.InputStream.Length;
            })
            .Returns(new PutObjectResponse());

        await CreateStore("AES256").PutAsync("a/b.bin", new MemoryStream(new byte[PartSize - 1]),
            new ArtifactMetadata("application/gzip", new Dictionary<string, string> { ["kid"] = "k1" }), Ct);

        Assert.NotNull(captured);
        Assert.Equal("bucket", captured.BucketName);
        Assert.Equal("a/b.bin", captured.Key);
        Assert.Equal("application/gzip", captured.ContentType);
        Assert.Equal(ServerSideEncryptionMethod.AES256, captured.ServerSideEncryptionMethod);
        Assert.Equal("k1", captured.Metadata["kid"]);
        Assert.Equal(PartSize - 1, capturedLength);
        A.CallTo(() => _client.InitiateMultipartUploadAsync(A<InitiateMultipartUploadRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task LargeContent_UsesMultipartUploadWithAllParts()
    {
        A.CallTo(() => _client.InitiateMultipartUploadAsync(A<InitiateMultipartUploadRequest>._,
                A<CancellationToken>._))
            .Returns(new InitiateMultipartUploadResponse { UploadId = "upload-1" });
        var partSizes = new List<long>();
        A.CallTo(() => _client.UploadPartAsync(A<UploadPartRequest>._, A<CancellationToken>._))
            .ReturnsLazily((UploadPartRequest r, CancellationToken _) =>
            {
                partSizes.Add(r.InputStream.Length);
                return new UploadPartResponse { ETag = $"\"etag-{r.PartNumber}\"" };
            });
        CompleteMultipartUploadRequest? completed = null;
        A.CallTo(() => _client.CompleteMultipartUploadAsync(A<CompleteMultipartUploadRequest>._,
                A<CancellationToken>._))
            .Invokes((CompleteMultipartUploadRequest r, CancellationToken _) => completed = r)
            .Returns(new CompleteMultipartUploadResponse());

        await CreateStore().PutAsync("a/big.bin",
            new NonSeekableStream(new MemoryStream(new byte[2 * PartSize + 10])), ArtifactMetadata.None, Ct);

        Assert.Equal([PartSize, PartSize, 10L], partSizes);
        Assert.NotNull(completed);
        Assert.Equal("upload-1", completed.UploadId);
        Assert.Equal([1, 2, 3], completed.PartETags.Select(p => p.PartNumber ?? 0));
        A.CallTo(() => _client.PutObjectAsync(A<PutObjectRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ContentOfExactlyOnePart_UsesMultipartWithOnePart()
    {
        A.CallTo(() => _client.InitiateMultipartUploadAsync(A<InitiateMultipartUploadRequest>._,
                A<CancellationToken>._))
            .Returns(new InitiateMultipartUploadResponse { UploadId = "u" });
        A.CallTo(() => _client.UploadPartAsync(A<UploadPartRequest>._, A<CancellationToken>._))
            .Returns(new UploadPartResponse { ETag = "\"e\"" });

        await CreateStore().PutAsync("a/exact.bin", new MemoryStream(new byte[PartSize]), ArtifactMetadata.None, Ct);

        A.CallTo(() => _client.UploadPartAsync(A<UploadPartRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _client.CompleteMultipartUploadAsync(A<CompleteMultipartUploadRequest>._,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FailedPart_AbortsTheMultipartUpload()
    {
        A.CallTo(() => _client.InitiateMultipartUploadAsync(A<InitiateMultipartUploadRequest>._,
                A<CancellationToken>._))
            .Returns(new InitiateMultipartUploadResponse { UploadId = "upload-2" });
        A.CallTo(() => _client.UploadPartAsync(A<UploadPartRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new AmazonS3Exception("boom"));

        await Assert.ThrowsAsync<AmazonS3Exception>(() => CreateStore().PutAsync("a/big.bin",
            new MemoryStream(new byte[PartSize + 1]), ArtifactMetadata.None, Ct));

        A.CallTo(() => _client.AbortMultipartUploadAsync(
                A<AbortMultipartUploadRequest>.That.Matches(r => r.UploadId == "upload-2" && r.Key == "a/big.bin"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _client.CompleteMultipartUploadAsync(A<CompleteMultipartUploadRequest>._,
            A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MissingBucket_OnRead_IsAnErrorNotNotFound()
    {
        A.CallTo(() => _client.GetObjectAsync(A<GetObjectRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new AmazonS3Exception("no bucket") { ErrorCode = "NoSuchBucket", StatusCode = HttpStatusCode.NotFound });

        await Assert.ThrowsAsync<AmazonS3Exception>(() => CreateStore().OpenReadAsync("a/b.bin", Ct));
    }

    [Fact]
    public async Task MissingKey_OnRead_ReturnsNull()
    {
        A.CallTo(() => _client.GetObjectAsync(A<GetObjectRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new AmazonS3Exception("no key") { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound });

        Assert.Null(await CreateStore().OpenReadAsync("a/b.bin", Ct));
    }

    [Fact]
    public void Constructor_RejectsUnsupportedOptions()
    {
        Assert.Throws<ArgumentException>(() => new S3ArtifactStore(_client,
            new S3ArtifactStorageOptions { Bucket = "b", ServerSideEncryption = "aws:kms" }));
        Assert.Throws<ArgumentException>(() => new S3ArtifactStore(_client,
            new S3ArtifactStorageOptions { Bucket = "b", MultipartPartSizeBytes = 1 }));
        Assert.ThrowsAny<ArgumentException>(() => new S3ArtifactStore(_client, new S3ArtifactStorageOptions()));
    }

    [Fact]
    public async Task Put_ClearsThePooledPartBuffer_BeforeReturningIt()
    {
        // The part buffer is rented from the shared pool; it held artifact content and must be zeroed on return.
        var bufferField = typeof(MemoryStream).GetField("_buffer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(bufferField);
        byte[]? rented = null;
        A.CallTo(() => _client.PutObjectAsync(A<PutObjectRequest>._, A<CancellationToken>._))
            .Invokes((PutObjectRequest r, CancellationToken _) => rented = (byte[]?)bufferField.GetValue(r.InputStream))
            .Returns(new PutObjectResponse());
        var content = new byte[1000];
        Array.Fill(content, (byte)0xAB);

        await CreateStore().PutAsync("a/b.bin", new MemoryStream(content), new ArtifactMetadata(), Ct);

        Assert.NotNull(rented);
        Assert.True(rented.All(b => b == 0));
    }

    [Fact]
    public async Task DeleteOlderThan_NeverExpiresObjectsWithoutLastModified()
    {
        A.CallTo(() => _client.ListObjectsV2Async(A<ListObjectsV2Request>._, A<CancellationToken>._))
            .Returns(new ListObjectsV2Response
            {
                S3Objects =
                [
                    new S3Object { Key = "p/old.bin", Size = 1, LastModified = DateTime.UtcNow.AddDays(-10) },
                    new S3Object { Key = "p/no-time.bin", Size = 1, LastModified = null }
                ],
                IsTruncated = false
            });
        A.CallTo(() => _client.GetObjectMetadataAsync(A<GetObjectMetadataRequest>._, A<CancellationToken>._))
            .Returns(new GetObjectMetadataResponse());

        var deleted = await CreateStore().DeleteOlderThanAsync("p/", TimeSpan.FromDays(1), Ct);

        Assert.Equal(["p/old.bin"], deleted);
        A.CallTo(() => _client.DeleteObjectAsync(A<DeleteObjectRequest>.That.Matches(r => r.Key == "p/no-time.bin"),
            A<CancellationToken>._)).MustNotHaveHappened();
    }
}
