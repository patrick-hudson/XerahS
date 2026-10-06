#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using NUnit.Framework;
using Newtonsoft.Json;
using ShareX.AmazonS3.Plugin.ViewModels;
using ShareX.AmazonS3.Plugin;
using System.Net;
using System.Collections.Concurrent;
using XerahS.Uploaders;

namespace XerahS.Tests.Uploaders;

[TestFixture]
public class AmazonS3CollisionTests
{
    [TestCase(false, "Copied PDF #1.pdf", @"^Copied PDF #1-[a-f0-9]{12}\.pdf$")]
    [TestCase(false, "Screen-Capture.png", @"^Screen-Capture-[a-f0-9]{12}\.png$")]
    [TestCase(false, "archive.tar.gz", @"^archive\.tar-[a-f0-9]{12}\.gz$")]
    [TestCase(false, "clip.mp4", @"^clip-[a-f0-9]{12}\.mp4$")]
    [TestCase(false, "payload.bin", @"^payload-[a-f0-9]{12}\.bin$")]
    [TestCase(false, "README", @"^README-[a-f0-9]{12}$")]
    [TestCase(false, ".env", @"^\.env-[a-f0-9]{12}$")]
    [TestCase(false, "réport #1.zip", @"^réport #1-[a-f0-9]{12}\.zip$")]
    [TestCase(true, "Copied PDF #1.pdf", @"^Copied PDF #1-[a-f0-9]{12}\.pdf$")]
    [TestCase(true, "Screen-Capture.png", @"^Screen-Capture-[a-f0-9]{12}\.png$")]
    [TestCase(true, "archive.tar.gz", @"^archive\.tar-[a-f0-9]{12}\.gz$")]
    [TestCase(true, "clip.mp4", @"^clip-[a-f0-9]{12}\.mp4$")]
    [TestCase(true, "payload.bin", @"^payload-[a-f0-9]{12}\.bin$")]
    [TestCase(true, "README", @"^README-[a-f0-9]{12}$")]
    [TestCase(true, ".env", @"^\.env-[a-f0-9]{12}$")]
    [TestCase(true, "réport #1.zip", @"^réport #1-[a-f0-9]{12}\.zip$")]
    public void Upload_WhenNameExists_PreservesOldObjectAndReturnsRenamedUpload(bool multipart, string fileName, string pattern)
    {
        byte[] original = [9, 8, 7];
        byte[] uploaded = [1, 2, 3, 4];
        RecordingS3 client = new();
        client.Objects[fileName] = original;
        AmazonS3Uploader uploader = CreateUploader(client, multipart);
        List<string> copiedUrls = [];
        uploader.EarlyURLCopyRequested += copiedUrls.Add;
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            UploadResult result;
            if (multipart)
            {
                string file = Path.Combine(temp, fileName);
                File.WriteAllBytes(file, uploaded);
                result = uploader.UploadFile(file)!;
            }
            else
            {
                using MemoryStream stream = new(uploaded);
                result = uploader.Upload(stream, fileName);
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True, result.Response);
                Assert.That(client.Objects[fileName], Is.EqualTo(original));
                Assert.That(client.Objects.Count, Is.EqualTo(2));
                string key = client.Objects.Keys.Single(k => k != fileName);
                Assert.That(key, Does.Match(pattern));
                Assert.That(client.Objects[key], Is.EqualTo(uploaded));
                Assert.That(result.URL, Is.EqualTo("https://put.example/" + Uri.EscapeDataString(key)));
                Assert.That(result.Metadata["S3Key"], Is.EqualTo(key));
                Assert.That(copiedUrls, Is.EqualTo(new[] { result.URL }));
                if (multipart)
                {
                    Assert.That(client.AbortedUploads, Is.EqualTo(1));
                }
            });
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Upload_WhenNameIsAvailable_KeepsExactName(bool multipart)
    {
        RecordingS3 client = new();
        UploadResult result = UploadBytes(CreateUploader(client, multipart), multipart, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, result.Response);
            Assert.That(client.Objects.Keys, Is.EqualTo(new[] { "report.bin" }));
            Assert.That(result.URL, Is.EqualTo("https://put.example/report.bin"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Upload_WhenProtectionIsDisabled_ExplicitlyOverwrites(bool multipart)
    {
        RecordingS3 client = new();
        client.Objects["report.bin"] = [9, 8, 7];
        UploadResult result = UploadBytes(CreateUploader(client, multipart, false), multipart, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, result.Response);
            Assert.That(client.Objects.Count, Is.EqualTo(1));
            Assert.That(client.Objects["report.bin"], Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Upload_OnConditionalRace_ReplaysTheFileInAFreshAttempt(bool multipart)
    {
        RecordingS3 client = new() { RaceOnce = true };
        UploadResult result = UploadBytes(CreateUploader(client, multipart), multipart, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, result.Response);
            Assert.That(client.StoreCalls, Is.EqualTo(2));
            Assert.That(client.Objects.Single().Value, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            if (multipart)
            {
                Assert.That(client.StartedUploads, Is.EqualTo(2));
                Assert.That(client.AbortedUploads, Is.EqualTo(1));
                Assert.That(client.Events, Is.EqualTo(new[] { "initiate", "abort", "initiate" }));
            }
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Upload_OnAccessDenied_DoesNotTryRenaming(bool multipart)
    {
        RecordingS3 client = new() { FailureStatus = HttpStatusCode.Forbidden };
        AmazonS3Uploader uploader = CreateUploader(client, multipart);
        List<string> copiedUrls = [];
        uploader.EarlyURLCopyRequested += copiedUrls.Add;
        UploadResult result = UploadBytes(uploader, multipart, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(client.StoreCalls, Is.EqualTo(1));
            Assert.That(client.Objects, Is.Empty);
            Assert.That(copiedUrls, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Upload_WhenEveryCandidateCollides_StopsWithoutOverwriting(bool multipart)
    {
        RecordingS3 client = new() { AlwaysConflict = true };
        UploadResult result = UploadBytes(CreateUploader(client, multipart), multipart, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(client.StoreCalls, Is.EqualTo(16));
            Assert.That(client.AttemptedKeys.Distinct().Count(), Is.EqualTo(client.StoreCalls));
            Assert.That(client.AttemptedKeys.Skip(1), Has.All.Matches<string>(k =>
                System.Text.RegularExpressions.Regex.IsMatch(k, @"^report-[a-f0-9]{12}\.bin$")));
            Assert.That(client.Objects, Is.Empty);
            if (multipart) Assert.That(client.AbortedUploads, Is.EqualTo(client.StartedUploads));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Upload_WhenCancelledDuringCollision_DoesNotStartAnotherAttempt(bool multipart)
    {
        RecordingS3 client = new() { AlwaysConflict = true };
        AmazonS3Uploader uploader = CreateUploader(client, multipart);
        client.OnConflict = uploader.StopUpload;
        UploadResult result = UploadBytes(uploader, multipart, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(client.StoreCalls, Is.EqualTo(1));
            Assert.That(client.Objects, Is.Empty);
        });
    }

    [Test]
    public void Multipart_PartFailureIsNotTreatedAsAFilenameCollision()
    {
        RecordingS3 client = new() { FailPart = true };
        UploadResult result = UploadBytes(CreateUploader(client, true), true, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(client.StartedUploads, Is.EqualTo(1));
            Assert.That(client.AbortedUploads, Is.EqualTo(1));
            Assert.That(client.Objects, Is.Empty);
        });
    }

    [Test]
    public void Collision_PreservesPrefixAndInitialStreamPosition()
    {
        RecordingS3 client = new();
        client.Objects["folder/report.bin"] = [9];
        AmazonS3Uploader uploader = CreateUploader(client, false, prefix: "folder");
        using MemoryStream stream = new(new byte[] { 0, 0, 1, 2, 3, 4 });
        stream.Position = 2;
        UploadResult result = uploader.Upload(stream, "report.bin");
        string key = client.Objects.Keys.Single(k => k != "folder/report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, result.Response);
            Assert.That(key, Does.Match(@"^folder/report-[a-f0-9]{12}\.bin$"));
            Assert.That(client.Objects[key], Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(client.Objects["folder/report.bin"], Is.EqualTo(new byte[] { 9 }));
        });
    }

    [Test]
    public void Configuration_OlderSettingsDefaultToProtection_AndExplicitFalseRoundTrips()
    {
        AmazonS3ConfigViewModel model = new();
        model.LoadFromJson("{}");
        Assert.That(model.AvoidOverwritingExistingFiles, Is.True);
        model.AvoidOverwritingExistingFiles = false;
        string saved = model.ToJson();
        Assert.That(JsonConvert.DeserializeObject<S3ConfigModel>(saved)!.AvoidOverwritingExistingFiles, Is.False);
        AmazonS3ConfigViewModel reloaded = new();
        reloaded.LoadFromJson(saved);
        Assert.That(reloaded.AvoidOverwritingExistingFiles, Is.False);
    }

    [TestCase(false, "Copied PDF #1.pdf")]
    [TestCase(true, "payload.bin")]
    public async Task Explorer_WhenNameExists_PreservesOldObjectAndReplaysFromInitialPosition(bool multipart, string fileName)
    {
        RecordingS3 client = new();
        string originalKey = "folder/" + fileName;
        client.Objects[originalKey] = [9, 8, 7];
        byte[] bytes = new byte[multipart ? 17 * 1024 * 1024 : 6];
        bytes[0] = 99;
        bytes[1] = 88;
        Array.Fill(bytes, (byte)42, 2, bytes.Length - 2);
        using MemoryStream stream = new(bytes);
        stream.Position = 2;
        await new S3ExplorerOperations(client, new S3ConfigModel { BucketName = "collision-tests" })
            .UploadAsync(originalKey, stream, CancellationToken.None);

        string finalKey = client.Objects.Keys.Single(k => k != originalKey);
        Assert.Multiple(() =>
        {
            Assert.That(finalKey, Does.StartWith("folder/"));
            Assert.That(Path.GetExtension(finalKey), Is.EqualTo(Path.GetExtension(fileName)));
            Assert.That(client.Objects[originalKey], Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(client.Objects[finalKey], Is.EqualTo(bytes[2..]));
            Assert.That(stream.CanRead, Is.True);
            if (multipart)
            {
                Assert.That(client.StartedUploads, Is.EqualTo(2));
                Assert.That(client.AbortedUploads, Is.EqualTo(1));
                Assert.That(client.Events, Is.EqualTo(new[] { "initiate", "abort", "initiate" }));
            }
            else
            {
                Assert.That(client.PutResetFlags, Is.EqualTo(new[] { false, false }));
            }
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Explorer_WhenProtectionIsDisabled_ExplicitlyOverwrites(bool multipart)
    {
        RecordingS3 client = new();
        client.Objects["report.bin"] = [9];
        byte[] bytes = new byte[multipart ? 17 * 1024 * 1024 : 4];
        Array.Fill(bytes, (byte)42);
        using MemoryStream stream = new(bytes);
        await new S3ExplorerOperations(client, new S3ConfigModel
        {
            BucketName = "collision-tests", AvoidOverwritingExistingFiles = false
        }).UploadAsync("report.bin", stream, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(client.Objects.Keys, Is.EqualTo(new[] { "report.bin" }));
            Assert.That(client.Objects["report.bin"], Is.EqualTo(bytes));
            Assert.That(client.StoreCalls, Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Explorer_OnConditionalRace_StartsAFreshUpload(bool multipart)
    {
        RecordingS3 client = new() { RaceOnce = true };
        byte[] bytes = new byte[multipart ? 17 * 1024 * 1024 : 4];
        Array.Fill(bytes, (byte)42);
        using MemoryStream stream = new(bytes);
        await new S3ExplorerOperations(client, new S3ConfigModel { BucketName = "collision-tests" })
            .UploadAsync("report.bin", stream, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(client.StoreCalls, Is.EqualTo(2));
            Assert.That(client.Objects.Single().Key, Does.Match(@"^report-[a-f0-9]{12}\.bin$"));
            Assert.That(client.Objects.Single().Value, Is.EqualTo(bytes));
            if (multipart)
                Assert.That(client.Events, Is.EqualTo(new[] { "initiate", "abort", "initiate" }));
        });
    }

    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.Conflict)]
    public void Explorer_OnUnrelatedFailure_DoesNotTryRenaming(HttpStatusCode failure)
    {
        RecordingS3 client = new() { FailureStatus = failure };
        using MemoryStream stream = new(new byte[] { 1, 2, 3 });
        Assert.ThrowsAsync<AmazonS3Exception>(() =>
            new S3ExplorerOperations(client, new S3ConfigModel { BucketName = "collision-tests" })
                .UploadAsync("report.bin", stream, CancellationToken.None));
        Assert.That(client.StoreCalls, Is.EqualTo(1));
    }

    [Test]
    public void Explorer_WhenEveryCandidateCollides_StopsAndUsesFreshKeys()
    {
        RecordingS3 client = new() { AlwaysConflict = true };
        using MemoryStream stream = new(new byte[] { 1, 2, 3 });
        Assert.ThrowsAsync<AmazonS3Exception>(() =>
            new S3ExplorerOperations(client, new S3ConfigModel { BucketName = "collision-tests" })
                .UploadAsync("report.bin", stream, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(client.StoreCalls, Is.EqualTo(16));
            Assert.That(client.AttemptedKeys.Distinct().Count(), Is.EqualTo(client.StoreCalls));
            Assert.That(client.AttemptedKeys.Skip(1), Has.All.Matches<string>(k =>
                System.Text.RegularExpressions.Regex.IsMatch(k, @"^report-[a-f0-9]{12}\.bin$")));
            Assert.That(client.Objects, Is.Empty);
        });
    }

    [Test]
    public void Explorer_WhenCancelledDuringCollision_DoesNotStartAnotherAttempt()
    {
        using CancellationTokenSource cancellation = new();
        RecordingS3 client = new() { AlwaysConflict = true, OnConflict = cancellation.Cancel };
        using MemoryStream stream = new(new byte[] { 1, 2, 3 });
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            new S3ExplorerOperations(client, new S3ConfigModel { BucketName = "collision-tests" })
                .UploadAsync("report.bin", stream, cancellation.Token));
        Assert.That(client.StoreCalls, Is.EqualTo(1));
    }

    [Test]
    public void Multipart_WhenAbortFails_DoesNotStartAnotherCandidate()
    {
        RecordingS3 client = new() { AlwaysConflict = true, FailAbort = true };
        UploadResult result = UploadBytes(CreateUploader(client, true), true, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Response, Does.Contain("abort").IgnoreCase);
            Assert.That(client.StartedUploads, Is.EqualTo(1));
            Assert.That(client.Events, Is.EqualTo(new[] { "initiate", "abort" }));
        });
    }

    [Test]
    public void Multipart_WhenPriorUploadNoLongerExists_StartsAFreshCandidate()
    {
        RecordingS3 client = new() { RaceOnce = true, UploadGoneOnAbort = true };
        UploadResult result = UploadBytes(CreateUploader(client, true), true, "report.bin");
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, result.Response);
            Assert.That(client.StartedUploads, Is.EqualTo(2));
            Assert.That(client.Events, Is.EqualTo(new[] { "initiate", "abort", "initiate" }));
            Assert.That(client.Objects.Single().Value, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        });
    }

    private static UploadResult UploadBytes(AmazonS3Uploader uploader, bool multipart, string name)
    {
        if (!multipart)
        {
            using MemoryStream bytes = new(new byte[] { 1, 2, 3, 4 });
            return uploader.Upload(bytes, name);
        }
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, name);
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            return uploader.UploadFile(file)!;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AmazonS3Uploader CreateUploader(RecordingS3 client, bool multipart, bool avoid = true, string prefix = "")
        => new(new S3ConfigModel
        {
            BucketName = "collision-tests",
            Endpoint = "s3.amazonaws.com",
            UseCustomCNAME = true,
            CustomDomain = "https://put.example",
            ObjectPrefix = prefix,
            AvoidOverwritingExistingFiles = avoid,
            MultipartThresholdBytes = multipart ? 1 : long.MaxValue,
            MultipartPartSizeBytes = 5L * 1024 * 1024,
            MultipartMaxConcurrency = 1
        }, "test-key", "test-secret", null, () => client);

    private sealed class RecordingS3 : AmazonS3Client
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        private readonly Dictionary<string, ConcurrentDictionary<int, byte[]>> _uploads = [];
        public List<string> Events { get; } = [];
        public List<string> AttemptedKeys { get; } = [];
        public List<bool?> PutResetFlags { get; } = [];
        public int AbortedUploads { get; private set; }
        public int StartedUploads { get; private set; }
        public int StoreCalls { get; private set; }
        public bool AlwaysConflict { get; init; }
        public bool RaceOnce { get; init; }
        public bool FailPart { get; init; }
        public bool FailAbort { get; init; }
        public bool UploadGoneOnAbort { get; init; }
        public HttpStatusCode? FailureStatus { get; init; }
        public Action? OnConflict { get; set; }

        public RecordingS3() : base(new AnonymousAWSCredentials(), new AmazonS3Config
        {
            ServiceURL = "https://example.invalid",
            AuthenticationRegion = "us-east-1"
        }) { }

        private void Store(string key, byte[] bytes, string? condition)
        {
            StoreCalls++;
            AttemptedKeys.Add(key);
            if (FailureStatus is { } failure)
            {
                throw new AmazonS3Exception("Request failed.") { StatusCode = failure, ErrorCode = "AccessDenied" };
            }
            if (RaceOnce && StoreCalls == 1)
            {
                throw new AmazonS3Exception("Concurrent request.") { StatusCode = HttpStatusCode.Conflict, ErrorCode = "ConditionalRequestConflict" };
            }
            if (condition == "*" && (AlwaysConflict || Objects.ContainsKey(key)))
            {
                OnConflict?.Invoke();
                throw new AmazonS3Exception("An object already exists.")
                {
                    StatusCode = HttpStatusCode.PreconditionFailed,
                    ErrorCode = "PreconditionFailed"
                };
            }
            Objects[key] = bytes;
        }

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            PutResetFlags.Add(request.AutoResetStreamPosition);
            using MemoryStream bytes = new();
            await request.InputStream.CopyToAsync(bytes, cancellationToken);
            Store(request.Key, bytes.ToArray(), request.IfNoneMatch);
            return new() { HttpStatusCode = HttpStatusCode.OK, ETag = "uploaded" };
        }

        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            StartedUploads++;
            Events.Add("initiate");
            string id = Guid.NewGuid().ToString("N");
            _uploads[id] = new();
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = id });
        }

        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            if (FailPart)
            {
                throw new AmazonS3Exception("Part failed.") { StatusCode = HttpStatusCode.PreconditionFailed };
            }
            byte[] bytes = new byte[checked((int)(request.PartSize ?? 0))];
            if (request.InputStream != null)
            {
                await request.InputStream.ReadExactlyAsync(bytes, cancellationToken);
            }
            else
            {
                await using FileStream input = File.OpenRead(request.FilePath);
                input.Position = request.FilePosition ?? 0;
                await input.ReadExactlyAsync(bytes, cancellationToken);
            }
            _uploads[request.UploadId][request.PartNumber ?? 0] = bytes;
            return new() { ETag = "part", PartNumber = request.PartNumber };
        }

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Store(request.Key, _uploads[request.UploadId].OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray(), request.IfNoneMatch);
            _uploads.Remove(request.UploadId);
            return Task.FromResult(new CompleteMultipartUploadResponse { ETag = "uploaded" });
        }

        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("abort");
            if (UploadGoneOnAbort)
            {
                _uploads.Remove(request.UploadId);
                throw new AmazonS3Exception("Upload no longer exists.")
                {
                    StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchUpload"
                };
            }
            if (FailAbort)
                throw new AmazonS3Exception("Abort denied.") { StatusCode = HttpStatusCode.Forbidden };
            _uploads.Remove(request.UploadId);
            AbortedUploads++;
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
    }
}
