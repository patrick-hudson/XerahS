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

using NUnit.Framework;
using System.Reflection;
using SkiaSharp;
using XerahS.Common;
using XerahS.Core;
using XerahS.Core.Tasks;
using XerahS.Core.Tasks.Pipeline;
using XerahS.Platform.Abstractions;
using XerahS.Platform.Mobile;
using XerahS.Uploaders;
using XerahS.Uploaders.PluginSystem;

namespace XerahS.Tests.Tasks;

[TestFixture]
[NonParallelizable]
public sealed class WorkerTaskClipboardUploadTests
{
    private static readonly FieldInfo PersonalFolderField = typeof(PathsManager)
        .GetField("_personalFolder", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PersonalFolderOverrideField = typeof(PathsManager)
        .GetField("_personalFolderOverrideSet", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PluginsLoadedField = typeof(ProviderCatalog)
        .GetField("_pluginsLoaded", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ProvidersField = typeof(ProviderCatalog)
        .GetField("_providers", BindingFlags.Static | BindingFlags.NonPublic)!;
    private string _tempDirectory = null!;
    private string? _originalPersonalFolder;
    private bool _originalPersonalFolderOverrideSet;
    private bool _originalPluginsLoaded;
    private string? _batchProviderId;
    private string? _originalInstancesJson;

    [SetUp]
    public void SetUp()
    {
        PlatformServices.Reset();
        _tempDirectory = Path.Combine(Path.GetTempPath(), "xerahs-clipboard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        PlatformServices.Reset();
        if (_originalInstancesJson != null)
        {
            InstanceManager.Instance.ImportConfigurationJson(_originalInstancesJson);
            _originalInstancesJson = null;
        }
        if (_batchProviderId != null)
        {
            var providers = (Dictionary<string, IUploaderProvider>)ProvidersField.GetValue(null)!;
            providers.Remove(_batchProviderId);
            PluginsLoadedField.SetValue(null, _originalPluginsLoaded);
            PersonalFolderField.SetValue(null, _originalPersonalFolder);
            PersonalFolderOverrideField.SetValue(null, _originalPersonalFolderOverrideSet);
            _batchProviderId = null;
        }
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [Test]
    public async Task CaptureStage_ClipboardUpload_UsesAsyncReadsAndUploadsCopiedFileInsteadOfUriText()
    {
        string filePath = CreateFile("Copied PDF #1.pdf");
        var clipboard = new AsyncOnlyClipboard
        {
            Files = new[] { filePath },
            Text = new Uri(filePath).AbsoluteUri
        };
        MobilePlatform.Initialize();
        PlatformServices.Clipboard = clipboard;
        using var worker = WorkerTask.Create(new TaskSettings { Job = WorkflowType.ClipboardUpload });
        var context = new PipelineContext { Info = worker.Info };

        var result = await new CaptureStage(worker).ExecuteAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(PipelineStageResult.Continue));
            Assert.That(worker.Info.DataType, Is.EqualTo(EDataType.File));
            Assert.That(worker.Info.Job, Is.EqualTo(TaskJob.FileUpload));
            Assert.That(worker.Info.FilePath, Is.EqualTo(filePath));
            Assert.That(worker.Info.FileName, Is.EqualTo("Copied PDF #1.pdf"));
            Assert.That(worker.Info.TextContent, Is.Null);
            Assert.That(clipboard.TextReads, Is.Zero);
        });
    }

    [Test]
    public async Task ParseClipboardAsync_FiltersMissingFilesBeforeChoosingFilesOverText()
    {
        string firstFile = CreateFile("first.pdf");
        string secondFile = CreateFile("second.pdf");
        var clipboard = new AsyncOnlyClipboard
        {
            Files = new[] { " ", Path.Combine(_tempDirectory, "missing.pdf"), firstFile, secondFile },
            Text = "file URI fallback"
        };

        var content = await ClipboardContentHelper.ParseClipboardAsync(clipboard);

        Assert.That(content, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(content!.DataType, Is.EqualTo(EDataType.File));
            Assert.That(content.Files, Is.EqualTo(new[] { firstFile, secondFile }));
            Assert.That(clipboard.TextReads, Is.Zero);
        });
    }

    [Test]
    public async Task CaptureStage_ClipboardUpload_FallsBackToTextWhenCopiedFilesNoLongerExist()
    {
        var clipboard = new AsyncOnlyClipboard
        {
            Files = new[] { Path.Combine(_tempDirectory, "missing.pdf") },
            Text = "ordinary clipboard text"
        };
        MobilePlatform.Initialize();
        PlatformServices.Clipboard = clipboard;
        var settings = new TaskSettings { Job = WorkflowType.ClipboardUpload };
        settings.UploadSettings.NameFormatPattern = "clipboard-text";
        using var worker = WorkerTask.Create(settings);

        var result = await new CaptureStage(worker).ExecuteAsync(new PipelineContext { Info = worker.Info }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(PipelineStageResult.Continue));
            Assert.That(worker.Info.DataType, Is.EqualTo(EDataType.Text));
            Assert.That(worker.Info.Job, Is.EqualTo(TaskJob.TextUpload));
            Assert.That(worker.Info.TextContent, Is.EqualTo(clipboard.Text));
            Assert.That(worker.Info.FileName, Is.EqualTo("clipboard-text." + settings.AdvancedSettings.TextFileExtension));
        });
    }

    [Test]
    public async Task CaptureStage_ClipboardUpload_KeepsImagePriorityAndImageNaming()
    {
        using var bitmap = new SKBitmap(2, 2);
        var clipboard = new AsyncOnlyClipboard
        {
            Image = bitmap,
            Files = new[] { CreateFile("copied.pdf") },
            Text = "ordinary clipboard text"
        };
        MobilePlatform.Initialize();
        PlatformServices.Clipboard = clipboard;
        var settings = new TaskSettings { Job = WorkflowType.ClipboardUpload };
        settings.UploadSettings.NameFormatPattern = "Screenshot-test";
        using var worker = WorkerTask.Create(settings);

        var result = await new CaptureStage(worker).ExecuteAsync(new PipelineContext { Info = worker.Info }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(PipelineStageResult.Continue));
            Assert.That(worker.Info.DataType, Is.EqualTo(EDataType.Image));
            Assert.That(worker.Info.Job, Is.EqualTo(TaskJob.DataUpload));
            Assert.That(worker.Info.Metadata.Image, Is.SameAs(clipboard.Image));
            Assert.That(worker.Info.FileName, Is.EqualTo("Screenshot-test.png"));
            Assert.That(clipboard.FileReads, Is.Zero);
            Assert.That(clipboard.TextReads, Is.Zero);
        });
    }

    [Test]
    public async Task CaptureStage_ClipboardUpload_RejectsEmptyClipboard()
    {
        MobilePlatform.Initialize();
        PlatformServices.Clipboard = new AsyncOnlyClipboard();
        using var worker = WorkerTask.Create(new TaskSettings { Job = WorkflowType.ClipboardUpload });
        var context = new PipelineContext { Info = worker.Info };

        var result = await new CaptureStage(worker).ExecuteAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(PipelineStageResult.Failed));
            Assert.That(context.Error!.Message, Is.EqualTo("Clipboard is empty or contains unsupported data."));
        });
    }

    [Test]
    public void CaptureStage_ClipboardUpload_CancellationPreventsReadingAndAssigningContent()
    {
        var clipboard = new AsyncOnlyClipboard { Files = new[] { CreateFile("copied.pdf") } };
        MobilePlatform.Initialize();
        PlatformServices.Clipboard = clipboard;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var worker = WorkerTask.Create(new TaskSettings { Job = WorkflowType.ClipboardUpload });

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new CaptureStage(worker).ExecuteAsync(new PipelineContext { Info = worker.Info }, cancellation.Token));
        Assert.That(clipboard.ImageReads, Is.Zero);
        Assert.That(worker.Info.FilePath, Is.Empty);
    }

    [Test]
    public void CaptureStage_ClipboardUpload_CancellationDuringImageReadDisposesImageAndDoesNotAssignContent()
    {
        using var cancellation = new CancellationTokenSource();
        using var bitmap = new SKBitmap(2, 2);
        var clipboard = new AsyncOnlyClipboard
        {
            Image = bitmap,
            AfterImageRead = cancellation.Cancel
        };
        MobilePlatform.Initialize();
        PlatformServices.Clipboard = clipboard;
        using var worker = WorkerTask.Create(new TaskSettings { Job = WorkflowType.ClipboardUpload });

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CaptureStage(worker).ExecuteAsync(new PipelineContext { Info = worker.Info }, cancellation.Token));

        Assert.Multiple(() =>
        {
            Assert.That(bitmap.Handle, Is.EqualTo(IntPtr.Zero));
            Assert.That(worker.Info.Metadata.Image, Is.Null);
            Assert.That(clipboard.FileReads, Is.Zero);
            Assert.That(clipboard.TextReads, Is.Zero);
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CaptureStage_ClipboardUpload_BatchReportsAnyFailedChild(bool firstSucceeds, bool secondSucceeds)
    {
        string firstFile = CreateFile("first copied.pdf");
        string secondFile = CreateFile("second copied.pdf");
        var uploadedNames = new List<string>();
        var settings = ConfigureBatchUploader(uploadedNames, name =>
        {
            bool succeeds = name == Path.GetFileName(firstFile) ? firstSucceeds : secondSucceeds;
            return new UploadResult
            {
                IsSuccess = succeeds,
                URL = succeeds ? "https://example.test/" + Uri.EscapeDataString(name) : null,
                Response = succeeds ? null : "Synthetic upload failure"
            };
        });
        PlatformServices.Clipboard = new AsyncOnlyClipboard
        {
            Files = new[] { firstFile, secondFile },
            Text = new Uri(firstFile).AbsoluteUri + "\n" + new Uri(secondFile).AbsoluteUri
        };
        using var worker = WorkerTask.Create(settings);
        var context = new PipelineContext { Info = worker.Info };

        var result = await new CaptureStage(worker).ExecuteAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(uploadedNames, Is.EqualTo(new[] { Path.GetFileName(firstFile), Path.GetFileName(secondFile) }));
            Assert.That(result, Is.EqualTo(firstSucceeds && secondSucceeds ? PipelineStageResult.Stop : PipelineStageResult.Failed));
            if (firstSucceeds && secondSucceeds)
            {
                Assert.That(context.Error, Is.Null);
            }
            else
            {
                Assert.That(context.Status, Is.EqualTo(XerahS.Core.TaskStatus.Failed));
                Assert.That(context.Error, Is.Not.Null);
            }
            if (firstSucceeds || secondSucceeds)
            {
                string lastSuccessfulName = Path.GetFileName(secondSucceeds ? secondFile : firstFile);
                Assert.That(worker.Info.FileName, Is.EqualTo(lastSuccessfulName));
                Assert.That(worker.Info.Result!.URL, Is.EqualTo("https://example.test/" + Uri.EscapeDataString(lastSuccessfulName)));
            }
        });
    }

    [Test]
    public async Task CaptureStage_ClipboardUpload_BatchRejectsNullUploaderResults()
    {
        string firstFile = CreateFile("first.pdf");
        string secondFile = CreateFile("second.pdf");
        var uploadedNames = new List<string>();
        var settings = ConfigureBatchUploader(uploadedNames, _ => null!);
        PlatformServices.Clipboard = new AsyncOnlyClipboard { Files = new[] { firstFile, secondFile } };
        using var worker = WorkerTask.Create(settings);
        var context = new PipelineContext { Info = worker.Info };

        var result = await new CaptureStage(worker).ExecuteAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(uploadedNames, Has.Count.EqualTo(2));
            Assert.That(result, Is.EqualTo(PipelineStageResult.Failed));
            Assert.That(context.Status, Is.EqualTo(XerahS.Core.TaskStatus.Failed));
            Assert.That(context.Error, Is.Not.Null);
        });
    }

    [Test]
    public async Task CaptureStage_ClipboardUpload_BatchReportsFileRemovedAfterClipboardWasParsed()
    {
        string firstFile = CreateFile("first.pdf");
        string secondFile = CreateFile("second.pdf");
        var uploadedNames = new List<string>();
        var settings = ConfigureBatchUploader(uploadedNames, name =>
        {
            File.Delete(secondFile);
            return new UploadResult { IsSuccess = true, URL = "https://example.test/" + name };
        });
        PlatformServices.Clipboard = new AsyncOnlyClipboard { Files = new[] { firstFile, secondFile } };
        using var worker = WorkerTask.Create(settings);
        var context = new PipelineContext { Info = worker.Info };

        var result = await new CaptureStage(worker).ExecuteAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(uploadedNames, Is.EqualTo(new[] { Path.GetFileName(firstFile) }));
            Assert.That(result, Is.EqualTo(PipelineStageResult.Failed));
            Assert.That(context.Status, Is.EqualTo(XerahS.Core.TaskStatus.Failed));
            Assert.That(context.Error, Is.Not.Null);
            Assert.That(worker.Info.Result!.URL, Is.EqualTo("https://example.test/first.pdf"));
        });
    }

    [Test]
    public void CaptureStage_ClipboardUpload_BatchCancellationStopsBeforeNextFile()
    {
        string firstFile = CreateFile("first.pdf");
        string secondFile = CreateFile("second.pdf");
        using var cancellation = new CancellationTokenSource();
        var uploadedNames = new List<string>();
        var settings = ConfigureBatchUploader(uploadedNames, name =>
        {
            cancellation.Cancel();
            return new UploadResult { IsSuccess = true, URL = "https://example.test/" + name };
        });
        PlatformServices.Clipboard = new AsyncOnlyClipboard { Files = new[] { firstFile, secondFile } };
        using var worker = WorkerTask.Create(settings);

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CaptureStage(worker).ExecuteAsync(new PipelineContext { Info = worker.Info }, cancellation.Token));
        Assert.That(uploadedNames, Is.EqualTo(new[] { Path.GetFileName(firstFile) }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartAsync_ClipboardUpload_BatchFailureShowsErrorToastAndKeepsLastSuccessfulUrl(bool removeSecondFile)
    {
        string firstFile = CreateFile("first.pdf");
        string secondFile = CreateFile("second.pdf");
        var uploadedNames = new List<string>();
        var settings = ConfigureBatchUploader(uploadedNames, name =>
        {
            if (name == Path.GetFileName(firstFile))
            {
                if (removeSecondFile)
                {
                    File.Delete(secondFile);
                }
                return new UploadResult { IsSuccess = true, URL = "https://example.test/first.pdf" };
            }
            return new UploadResult { IsSuccess = false, Response = "Synthetic upload failure" };
        });
        var toastService = new RecordingToastService();
        PlatformServices.RegisterToastService(toastService);
        PlatformServices.Clipboard = new AsyncOnlyClipboard
        {
            Files = new[] { firstFile, secondFile },
            Text = new Uri(firstFile).AbsoluteUri + "\n" + new Uri(secondFile).AbsoluteUri
        };
        using var worker = WorkerTask.Create(settings);

        await worker.StartAsync();

        Assert.That(toastService.Notifications, Is.Not.Empty);
        var taskToast = toastService.Notifications[^1];
        Assert.Multiple(() =>
        {
            Assert.That(worker.Status, Is.EqualTo(XerahS.Core.TaskStatus.Failed));
            Assert.That(worker.Error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(worker.Error!.InnerException, Is.InstanceOf<AggregateException>());
            Assert.That(worker.Info.Result!.URL, Is.EqualTo("https://example.test/first.pdf"));
            Assert.That(worker.Info.FileName, Is.EqualTo("first.pdf"));
            Assert.That(taskToast.Title, Is.EqualTo("ClipboardUpload Failed"));
            Assert.That(taskToast.Text, Does.Contain("clipboard file uploads failed"));
            Assert.That(taskToast.ErrorDetails, Does.Contain("second.pdf"));
        });
    }

    private sealed class RecordingToastService : IToastService
    {
        public List<ToastConfig> Notifications { get; } = new();
        public void ShowToast(ToastConfig config) => Notifications.Add(config);
        public void CloseActiveToast() { }
    }

    private TaskSettings ConfigureBatchUploader(List<string> uploadedNames, Func<string, UploadResult> upload)
    {
        _originalPersonalFolder = (string?)PersonalFolderField.GetValue(null);
        _originalPersonalFolderOverrideSet = (bool)PersonalFolderOverrideField.GetValue(null)!;
        _originalPluginsLoaded = ProviderCatalog.ArePluginsLoaded();
        _batchProviderId = "clipboard-batch-tests-" + Guid.NewGuid().ToString("N");
        PathsManager.PersonalFolder = Path.Combine(_tempDirectory, "settings");
        PathsManager.EnsureDirectoriesExist();
        _originalInstancesJson = InstanceManager.Instance.ExportConfigurationJson();
        foreach (var instance in InstanceManager.Instance.GetInstances())
        {
            InstanceManager.Instance.RemoveInstance(instance.InstanceId);
        }
        // Mark the fake-only catalog loaded without creating a provider context or
        // scanning installed plugins; both catalog and path state are restored above.
        ProviderCatalog.LoadPlugins(Array.Empty<string>());
        ProviderCatalog.RegisterProvider(new BatchTestProvider(_batchProviderId, uploadedNames, upload));
        var destination = new UploaderInstance
        {
            ProviderId = _batchProviderId,
            Category = UploaderCategory.File,
            DisplayName = "Synthetic clipboard batch uploader",
            IsAvailable = true
        };
        InstanceManager.Instance.AddInstance(destination);
        MobilePlatform.Initialize();
        return new TaskSettings
        {
            Job = WorkflowType.ClipboardUpload,
            DestinationInstanceId = destination.InstanceId,
            AfterCaptureJob = AfterCaptureTasks.None,
            AfterUploadJob = AfterUploadTasks.None
        };
    }

    private sealed class BatchTestProvider(string providerId, List<string> uploadedNames, Func<string, UploadResult> upload)
        : UploaderProviderBase
    {
        public override string ProviderId => providerId;
        public override string Name => "Clipboard Batch Test Provider";
        public override string Description => "Test-only uploader for clipboard batch results.";
        public override Version Version { get; } = new(1, 0, 0);
        public override UploaderCategory[] SupportedCategories { get; } = [UploaderCategory.File];
        public override Type ConfigModelType => typeof(object);
        public override Dictionary<UploaderCategory, string[]> GetSupportedFileTypes() => new()
        {
            [UploaderCategory.File] = ["*"]
        };
        public override Uploader CreateInstance(string settingsJson) => new BatchTestUploader(uploadedNames, upload);
    }

    private sealed class BatchTestUploader(List<string> uploadedNames, Func<string, UploadResult> upload) : FileUploader
    {
        public override UploadResult Upload(Stream stream, string fileName)
        {
            uploadedNames.Add(fileName);
            return upload(fileName);
        }
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, "synthetic clipboard test");
        return path;
    }

    private sealed class AsyncOnlyClipboard : IClipboardService
    {
        public SKBitmap? Image { get; init; }
        public string[]? Files { get; init; }
        public string? Text { get; init; }
        public Action? AfterImageRead { get; init; }
        public int ImageReads { get; private set; }
        public int FileReads { get; private set; }
        public int TextReads { get; private set; }

        public async Task<SKBitmap?> GetImageAsync()
        {
            ImageReads++;
            await Task.Yield();
            AfterImageRead?.Invoke();
            return Image;
        }

        public async Task<string[]?> GetFileDropListAsync()
        {
            FileReads++;
            await Task.Yield();
            return Files;
        }

        public async Task<string?> GetTextAsync()
        {
            TextReads++;
            await Task.Yield();
            return Text;
        }

        public void Clear() => throw new NotSupportedException();
        public bool ContainsText() => throw new NotSupportedException("Synchronous clipboard reads must not be used.");
        public bool ContainsImage() => throw new NotSupportedException("Synchronous clipboard reads must not be used.");
        public bool ContainsFileDropList() => throw new NotSupportedException("Synchronous clipboard reads must not be used.");
        public string? GetText() => throw new NotSupportedException("Synchronous clipboard reads must not be used.");
        public SKBitmap? GetImage() => throw new NotSupportedException("Synchronous clipboard reads must not be used.");
        public string[]? GetFileDropList() => throw new NotSupportedException("Synchronous clipboard reads must not be used.");
        public void SetText(string text) => throw new NotSupportedException();
        public void SetImage(SKBitmap image) => throw new NotSupportedException();
        public void SetFileDropList(string[] files) => throw new NotSupportedException();
        public object? GetData(string format) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public bool ContainsData(string format) => throw new NotSupportedException();
        public Task SetTextAsync(string text) => throw new NotSupportedException();
    }

    [Test]
    public void ApplyLastClipboardUploadInfo_PreservesResolvedUploaderHost()
    {
        var destination = new TaskInfo();

        var lastInfo = new TaskInfo()
        {
            DataType = EDataType.File,
            Job = TaskJob.FileUpload,
            Result = new UploadResult
            {
                IsSuccess = true,
                URL = "https://example.test/last.txt"
            },
            ResolvedUploaderHost = "Actual Clipboard Uploader"
        };
        lastInfo.FilePath = "/tmp/last.txt";
        lastInfo.Metadata.UploadURL = "https://example.test/last.txt";

        WorkerTask.ApplyLastClipboardUploadInfo(destination, lastInfo);

        Assert.Multiple(() =>
        {
            Assert.That(destination.DataType, Is.EqualTo(EDataType.File));
            Assert.That(destination.FilePath, Is.EqualTo("/tmp/last.txt"));
            Assert.That(destination.Job, Is.EqualTo(TaskJob.FileUpload));
            Assert.That(destination.Result, Is.SameAs(lastInfo.Result));
            Assert.That(destination.Metadata.UploadURL, Is.EqualTo("https://example.test/last.txt"));
            Assert.That(destination.UploaderHost, Is.EqualTo("Actual Clipboard Uploader"));
        });
    }
}
