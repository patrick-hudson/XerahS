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

using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using NUnit.Framework;
using XerahS.Packaging;

namespace XerahS.Tests.Build;

[TestFixture]
public class PortableTarballTests
{
    [TestCase("XerahS")]
    [TestCase("xerahs-watchfolder-daemon")]
    [TestCase("omaxerahs")]
    public void CreateTarball_RootExecutableHasExecutablePermissions(string filename)
    {
        WithArchive(source => File.WriteAllText(Path.Combine(source, filename), "fake-binary"), archive =>
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            TarEntry entry = reader.GetNextEntry()!;
            Assert.That(entry.Name, Is.EqualTo(filename));
            Assert.That(entry.EntryType, Is.EqualTo(TarEntryType.RegularFile));
            Assert.That((int)entry.Mode, Is.EqualTo(Convert.ToInt32("755", 8)), filename);
        });
    }

    [Test]
    public void CreateTarball_PreservesNestedExecutableDataAndSymlinkModes()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Creating symbolic links requires additional privileges on Windows.");

        WithArchive(source =>
        {
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "nested", "XerahS"), "fake-binary");
            File.WriteAllText(Path.Combine(source, "XerahS.dll"), "data");
            File.CreateSymbolicLink(Path.Combine(source, "launcher"), "nested/XerahS");
        }, archive =>
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var entries = new Dictionary<string, (TarEntryType Type, int Mode, string Link)>();
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) != null)
                entries[entry.Name] = (entry.EntryType, (int)entry.Mode, entry.LinkName);

            Assert.Multiple(() =>
            {
                Assert.That(entries["nested/XerahS"].Mode, Is.EqualTo(Convert.ToInt32("755", 8)));
                Assert.That(entries["XerahS.dll"].Mode, Is.EqualTo(Convert.ToInt32("644", 8)));
                Assert.That(entries["launcher"].Type, Is.EqualTo(TarEntryType.SymbolicLink));
                Assert.That(entries["launcher"].Link, Is.EqualTo("nested/XerahS"));
            });
        });
    }

    private static void WithArchive(Action<string> prepare, Action<string> verify)
    {
        string root = Path.Combine(Path.GetTempPath(), "xerahs-portable-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            prepare(source);
            string archive = Path.Combine(root, "portable.tar.gz");
            Type program = typeof(AppImagePackager).Assembly.GetType("Program", throwOnError: true)!;
            MethodInfo create = program.GetMethod("CreateTarball", BindingFlags.Static | BindingFlags.NonPublic)!;
            create.Invoke(null, [source, archive]);
            verify(archive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
