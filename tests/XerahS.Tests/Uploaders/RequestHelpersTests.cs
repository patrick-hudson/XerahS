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
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using XerahS.Uploaders;
using UploadHttpMethod = XerahS.Uploaders.HttpMethod;

namespace XerahS.Tests.Uploaders;

[TestFixture]
public class RequestHelpersTests
{
    [TestCase("Original PDF #1.pdf")]
    [TestCase("café 100%.pdf")]
    public void MakeFileInputContentOpen_PreservesOrdinaryAsciiAndUtf8Headers(string fileName)
    {
        const string boundary = "test-boundary";

        string content = Encoding.UTF8.GetString(RequestHelpers.MakeFileInputContentOpen(boundary, "file", fileName));

        Assert.That(content, Is.EqualTo(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: application/pdf\r\n\r\n"));
    }

    [TestCase("quoted\"name.pdf", "quoted%22name.pdf")]
    [TestCase("back\\slash.pdf", "back%5Cslash.pdf")]
    [TestCase("line\r\nbreak.pdf", "line%0D%0Abreak.pdf")]
    [TestCase("control\u0001\u007F.pdf", "control%01%7F.pdf")]
    public void MakeFileInputContentOpen_EncodesUnsafeFilenameCharacters(string fileName, string expectedEncodedName)
    {
        const string boundary = "test-boundary";
        string content = Encoding.UTF8.GetString(RequestHelpers.MakeFileInputContentOpen(boundary, "file", fileName));
        string[] lines = content.Split("\r\n", StringSplitOptions.None);

        Assert.That(lines, Has.Length.EqualTo(5));
        var disposition = ContentDispositionHeaderValue.Parse(lines[1]["Content-Disposition: ".Length..]);
        Assert.Multiple(() =>
        {
            Assert.That(disposition.DispositionType, Is.EqualTo("form-data"));
            Assert.That(disposition.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "name", "filename" }));
            Assert.That(disposition.Name, Is.EqualTo("file"));
            Assert.That(disposition.FileName, Is.EqualTo(expectedEncodedName));
            Assert.That(Uri.UnescapeDataString(disposition.FileName!.Trim('"')), Is.EqualTo(fileName));
            Assert.That(lines[2], Is.EqualTo("Content-Type: application/pdf"));
        });
    }

    [Test]
    public void MakeFileInputContentOpen_CannotInjectHeadersOrAnotherPartThroughParameters()
    {
        const string boundary = "test-boundary";
        string hostileParameter = $"quoted\"\\\r\nX-Injected: yes\r\n\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"injected\"";
        string fileName = hostileParameter + ".pdf";
        string open = Encoding.UTF8.GetString(RequestHelpers.MakeFileInputContentOpen(boundary, hostileParameter, fileName));
        string body = open + "synthetic file bytes" + Encoding.UTF8.GetString(RequestHelpers.MakeFileInputContentClose(boundary));
        string[] lines = body.Split("\r\n", StringSplitOptions.None);

        Assert.That(open.Split("\r\n", StringSplitOptions.None), Has.Length.EqualTo(5));
        var disposition = ContentDispositionHeaderValue.Parse(lines[1]["Content-Disposition: ".Length..]);
        Assert.Multiple(() =>
        {
            Assert.That(disposition.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "name", "filename" }));
            Assert.That(Uri.UnescapeDataString(disposition.Name!.Trim('"')), Is.EqualTo(hostileParameter));
            Assert.That(Uri.UnescapeDataString(disposition.FileName!.Trim('"')), Is.EqualTo(fileName));
            Assert.That(lines.Count(line => line == "--" + boundary || line == "--" + boundary + "--"), Is.EqualTo(2));
            Assert.That(lines.Count(line => line.StartsWith("X-Injected:", StringComparison.Ordinal)), Is.Zero);
            Assert.That(lines.Count(line => line.StartsWith("Content-Disposition:", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(lines[2], Is.EqualTo("Content-Type: application/pdf"));
            Assert.That(lines[3], Is.Empty);
            Assert.That(lines[4], Is.EqualTo("synthetic file bytes"));
        });
    }

    [Test]
    public void MakeInputContent_CannotInjectHeadersOrAnotherPartThroughFieldName()
    {
        const string boundary = "test-boundary";
        string fieldName = $"quoted\"\\\r\nX-Injected: yes\r\n\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"injected\"";
        string body = Encoding.UTF8.GetString(RequestHelpers.MakeInputContent(boundary, fieldName, "field value")) + $"--{boundary}--\r\n";
        string[] lines = body.Split("\r\n", StringSplitOptions.None);

        Assert.That(lines, Has.Length.EqualTo(6));
        var disposition = ContentDispositionHeaderValue.Parse(lines[1]["Content-Disposition: ".Length..]);
        Assert.Multiple(() =>
        {
            Assert.That(disposition.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "name" }));
            Assert.That(Uri.UnescapeDataString(disposition.Name!.Trim('"')), Is.EqualTo(fieldName));
            Assert.That(lines.Count(line => line == "--" + boundary || line == "--" + boundary + "--"), Is.EqualTo(2));
            Assert.That(lines.Count(line => line.StartsWith("X-Injected:", StringComparison.Ordinal)), Is.Zero);
            Assert.That(lines[2], Is.Empty);
            Assert.That(lines[3], Is.EqualTo("field value"));
        });
    }

    [Test]
    public void CreateWebRequest_ParsesCookieHeaderWithoutWhitespaceSeparators()
    {
        NameValueCollection headers = new()
        {
            ["Cookie"] = "session=abc123;theme=dark"
        };

        HttpWebRequest request = RequestHelpers.CreateWebRequest(UploadHttpMethod.GET, "https://example.com/upload", headers);

        Assert.That(request.CookieContainer, Is.Not.Null);
        CookieCollection cookies = request.CookieContainer!.GetCookies(new Uri("https://example.com/upload"));

        Assert.That(cookies["session"]?.Value, Is.EqualTo("abc123"));
        Assert.That(cookies["theme"]?.Value, Is.EqualTo("dark"));
    }

    [Test]
    public void CreateWebRequest_PreservesCookieValuesContainingEquals()
    {
        NameValueCollection headers = new()
        {
            ["Cookie"] = "token=abc=def=="
        };

        HttpWebRequest request = RequestHelpers.CreateWebRequest(UploadHttpMethod.GET, "https://example.com/upload", headers);

        Assert.That(request.CookieContainer, Is.Not.Null);
        CookieCollection cookies = request.CookieContainer!.GetCookies(new Uri("https://example.com/upload"));

        Assert.That(cookies["token"]?.Value, Is.EqualTo("abc=def=="));
    }

    [Test]
    public void CreateWebRequest_PreservesCookiesWithEmptyValues()
    {
        NameValueCollection headers = new()
        {
            ["Cookie"] = "session=abc123; theme="
        };

        HttpWebRequest request = RequestHelpers.CreateWebRequest(UploadHttpMethod.GET, "https://example.com/upload", headers);

        Assert.That(request.CookieContainer, Is.Not.Null);
        CookieCollection cookies = request.CookieContainer!.GetCookies(new Uri("https://example.com/upload"));

        Assert.That(cookies["session"]?.Value, Is.EqualTo("abc123"));
        Assert.That(cookies["theme"], Is.Not.Null);
        Assert.That(cookies["theme"]?.Value, Is.EqualTo(string.Empty));
    }
}
