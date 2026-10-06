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

using XerahS.Common;
using XerahS.Core.Helpers;
using XerahS.Core.Tasks.Processors;
using XerahS.Platform.Abstractions;

namespace XerahS.Core.Tasks
{
    /// <summary>
    /// WorkerTask partial class for upload and clipboard operations.
    /// </summary>
    public partial class WorkerTask
    {
        internal async Task<(bool Loaded, string[]? Files)> TryLoadClipboardContentAsync(
            TaskSettings taskSettings, TaskMetadata metadata, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var clipboard = PlatformServices.Clipboard;
            if (clipboard == null)
            {
                return (false, null);
            }

            var content = await ClipboardContentHelper.ParseClipboardAsync(clipboard, token);
            if (token.IsCancellationRequested)
            {
                content?.Image?.Dispose();
                token.ThrowIfCancellationRequested();
            }

            if (content == null)
            {
                return (false, null);
            }

            switch (content.DataType)
            {
                case EDataType.Image:
                    metadata.Image = content.Image;
                    Info.DataType = EDataType.Image;
                    Info.Job = TaskJob.DataUpload;
                    string imageExtension = EnumExtensions.GetDescription(taskSettings.ImageSettings.ImageFormat);
                    Info.SetFileName(TaskHelpers.GetFileName(taskSettings, imageExtension, metadata));
                    return (true, null);

                case EDataType.Text:
                    Info.TextContent = content.Text;
                    Info.DataType = EDataType.Text;
                    Info.Job = TaskJob.TextUpload;
                    string textExtension = taskSettings.AdvancedSettings.TextFileExtension;
                    Info.SetFileName(TaskHelpers.GetFileName(taskSettings, textExtension, metadata));
                    return (true, null);

                case EDataType.File:
                    var clipboardFiles = content.Files;
                    if (clipboardFiles == null || clipboardFiles.Length == 0)
                    {
                        return (false, null);
                    }
                    Info.FilePath = clipboardFiles[0];
                    Info.DataType = EDataType.File;
                    Info.Job = TaskJob.FileUpload;
                    return (true, clipboardFiles);
            }

            return (false, null);
        }

        internal async Task<Exception?> UploadClipboardFilesAsync(TaskSettings taskSettings, string[] files, CancellationToken token)
        {
            var uploadProcessor = new UploadJobProcessor();
            var failures = new List<Exception>();
            TaskInfo? lastInfo = null;
            TaskInfo? lastSuccessfulInfo = null;

            foreach (var filePath in files)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    failures.Add(new FileNotFoundException("The file to upload no longer exists.", filePath));
                    continue;
                }

                var fileInfo = new TaskInfo(taskSettings)
                {
                    DataType = EDataType.File,
                    Job = TaskJob.FileUpload,
                    FilePath = filePath
                };

                await uploadProcessor.ProcessAsync(fileInfo, token);
                token.ThrowIfCancellationRequested();
                lastInfo = fileInfo;
                if (UploadJobProcessor.IsSuccessfulUploadResult(fileInfo.Result))
                {
                    lastSuccessfulInfo = fileInfo;
                }
                else
                {
                    string message = fileInfo.Result?.Response ?? "Uploader returned no result.";
                    failures.Add(new InvalidOperationException($"Upload failed for {Path.GetFileName(filePath)}: {message}"));
                }
            }

            token.ThrowIfCancellationRequested();
            var completedInfo = lastSuccessfulInfo ?? lastInfo;
            if (completedInfo != null)
            {
                ApplyLastClipboardUploadInfo(Info, completedInfo);
            }

            return failures.Count > 0
                ? new AggregateException("One or more clipboard file uploads failed.", failures)
                : null;
        }

        internal static void ApplyLastClipboardUploadInfo(TaskInfo destination, TaskInfo lastInfo)
        {
            destination.DataType = lastInfo.DataType;
            destination.FilePath = lastInfo.FilePath;
            destination.Job = lastInfo.Job;
            destination.Result = lastInfo.Result;
            destination.Metadata.UploadURL = lastInfo.Metadata.UploadURL;
            destination.ResolvedUploaderHost = lastInfo.ResolvedUploaderHost;
        }

        internal bool TryIndexFolder(TaskSettings taskSettings, out string? outputPath)
        {
            outputPath = null;

            if (taskSettings?.ToolsSettings == null)
            {
                DebugHelper.WriteLine("IndexFolder: ToolsSettings missing.");
                return false;
            }

            string folderPath = taskSettings.ToolsSettings.IndexerFolderPath;
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                DebugHelper.WriteLine($"IndexFolder: Folder path invalid: '{folderPath}'");
                return false;
            }

            try
            {
                var indexerSettings = taskSettings.ToolsSettings.IndexerSettings ?? new XerahS.Indexer.IndexerSettings();

                string output = XerahS.Indexer.Indexer.Index(folderPath, indexerSettings);
                outputPath = WriteIndexOutput(taskSettings, folderPath, output, indexerSettings.Output);
                return !string.IsNullOrEmpty(outputPath);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "IndexFolder: indexing failed");
                return false;
            }
        }

        private static string WriteIndexOutput(TaskSettings taskSettings, string folderPath, string output, XerahS.Indexer.IndexerOutput outputType)
        {
            string extension = GetIndexFolderExtension(outputType);
            string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettings);
            Directory.CreateDirectory(screenshotsFolder);

            string fileName = TaskHelpers.GetFileName(taskSettings, extension);
            string resolvedPath = TaskHelpers.HandleExistsFile(screenshotsFolder, fileName, taskSettings);
            if (string.IsNullOrWhiteSpace(resolvedPath)) return string.Empty;
            File.WriteAllText(resolvedPath, output);
            return resolvedPath;
        }

        private static string GetIndexFolderExtension(XerahS.Indexer.IndexerOutput outputType)
        {
            return outputType switch
            {
                XerahS.Indexer.IndexerOutput.Html => "html",
                XerahS.Indexer.IndexerOutput.Txt => "txt",
                XerahS.Indexer.IndexerOutput.Xml => "xml",
                XerahS.Indexer.IndexerOutput.Json => "json",
                _ => "txt"
            };
        }
    }
}
