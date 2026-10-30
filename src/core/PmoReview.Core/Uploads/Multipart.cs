using System.Text;
using System.Text.RegularExpressions;

namespace PmoReview.Core.Uploads;

/// <summary>The one file of a multipart/form-data body (RFC 7578): its file name and bytes. Other parts are ignored.</summary>
public sealed record UploadedFile(string FileName, byte[] Content);

public static partial class Multipart
{
    /// <summary>
    /// The part named <c>file</c> (or the first part with a file name). Null when the body is not multipart. Throws
    /// <see cref="UploadFileException"/> for a multipart body without a file.
    /// </summary>
    public static UploadedFile? ReadFile(byte[] body, string? contentType)
    {
        if (contentType is null || !contentType.TrimStart().StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Boundary().Match(contentType);
        if (!m.Success) throw new UploadFileException("InvalidFile", "The multipart body has no boundary.");
        var boundary = Encoding.ASCII.GetBytes("--" + m.Groups["b"].Value.Trim('"'));
        UploadedFile? first = null;
        var pos = IndexOf(body, boundary, 0);
        while (pos >= 0)
        {
            var start = pos + boundary.Length;
            if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-') break; // closing boundary
            var headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            if (headersEnd < 0) break;
            var headers = Encoding.UTF8.GetString(body, start, headersEnd - start);
            var next = IndexOf(body, boundary, headersEnd + 4);
            if (next < 0) break;
            var contentEnd = next - 2; // the CRLF before the boundary
            var disposition = Disposition().Match(headers);
            if (disposition.Success && contentEnd >= headersEnd + 4)
            {
                var name = Param(disposition.Value, "name");
                var fileName = Param(disposition.Value, "filename");
                if (fileName is not null)
                {
                    var file = new UploadedFile(Path.GetFileName(fileName.Replace('\\', '/')), body[(headersEnd + 4)..contentEnd]);
                    if (string.Equals(name, "file", StringComparison.Ordinal)) return file;
                    first ??= file;
                }
            }
            pos = next;
        }
        return first ?? throw new UploadFileException("EmptyFile", "The multipart body has no file part (field \"file\").");
    }

    private static string? Param(string disposition, string name)
    {
        var m = Regex.Match(disposition, $@"(?:^|;)\s*{name}=(?:""(?<v>[^""]*)""|(?<v>[^;\s]+))", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        if (from > haystack.Length) return -1;
        var found = haystack.AsSpan(from).IndexOf(needle);
        return found < 0 ? -1 : from + found;
    }

    [GeneratedRegex(@"boundary=(?<b>""[^""]+""|[^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Boundary();

    [GeneratedRegex(@"(?im)^content-disposition:\s*form-data[^\r\n]*")]
    private static partial Regex Disposition();
}
