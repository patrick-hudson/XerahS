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
using XerahS.Core;
using XerahS.Core.Managers;
using XerahS.Core.Tasks;
using XerahS.Platform.Abstractions;

namespace XerahS.Tests.Tasks;

[TestFixture]
[NonParallelizable]
public sealed class TaskManagerFileNamingTests
{
    [SetUp]
    public void SetUp() => PlatformServices.Reset();

    [TearDown]
    public void TearDown() => PlatformServices.Reset();

    [TestCase(false, "Original PDF #1.pdf")]
    [TestCase(true, "requested-upload-name.pdf")]
    public async Task StartFileTask_UsesOriginalNameUnlessNamePatternWasRequested(bool usePattern, string expectedName)
    {
        string filePath = Path.Combine(Path.GetTempPath(), "Original PDF #1.pdf");
        var settings = new TaskSettings
        {
            Job = WorkflowType.FileUpload,
            AfterCaptureJob = AfterCaptureTasks.None,
            AfterUploadJob = AfterUploadTasks.None
        };
        settings.UploadSettings.FileUploadUseNamePattern = usePattern;
        settings.UploadSettings.NameFormatPattern = "requested-upload-name";
        WorkerTask? startedTask = null;
        var manager = TaskManager.Instance;
        void OnStarted(object? sender, WorkerTask task) => startedTask = task;
        manager.TaskStarted += OnStarted;

        try
        {
            // The capture stage stops when platform services are uninitialized;
            // the test observes the real task setup without reading or uploading files.
            await manager.StartFileTask(settings, filePath);

            Assert.That(startedTask, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(startedTask!.Info.FileName, Is.EqualTo(expectedName));
                Assert.That(startedTask.Info.FilePath, Is.EqualTo(filePath));
                Assert.That(startedTask.Info.DataType, Is.EqualTo(XerahS.Common.EDataType.File));
            });
        }
        finally
        {
            manager.TaskStarted -= OnStarted;
            startedTask?.Dispose();
        }
    }
}
