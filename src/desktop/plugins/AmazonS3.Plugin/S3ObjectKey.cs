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

using Amazon.S3;
using System.Net;

namespace ShareX.AmazonS3.Plugin;

internal static class S3ObjectKey
{
    public const int MaximumUploadAttempts = 16;

    public static bool IsConditionalConflict(AmazonS3Exception exception)
        => exception.StatusCode == HttpStatusCode.PreconditionFailed ||
            (exception.StatusCode == HttpStatusCode.Conflict && exception.ErrorCode == "ConditionalRequestConflict");

    public static string WithRandomSuffix(string key)
    {
        int leafStart = key.LastIndexOf('/') + 1;
        int extensionStart = key.LastIndexOf('.');
        if (extensionStart <= leafStart || extensionStart == key.Length - 1)
        {
            extensionStart = key.Length;
        }
        return key.Insert(extensionStart, "-" + Guid.NewGuid().ToString("N")[..12]);
    }
}
