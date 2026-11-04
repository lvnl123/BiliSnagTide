using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliLiveManager;

/// <summary>Pure HLS playlist URI mapping for both media & master playlists.</summary>
public static class HlsPlaylistTools
{
    private static readonly Regex TagUrl = new("URI=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static string Rewrite(string body, Uri playlistUri, Func<Uri, string> makeLocalUrl)
    {
        var result = new StringBuilder();
        using var reader = new StringReader(body);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                result.AppendLine(Adapt(trimmed, playlistUri, makeLocalUrl));
            else if (trimmed.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                result.AppendLine(TagUrl.Replace(line, m => "URI=\"" + Adapt(m.Groups[1].Value, playlistUri, makeLocalUrl) + "\""));
            else result.AppendLine(line);
        }
        return result.ToString();
    }
    private static string Adapt(string uri, Uri playlist, Func<Uri, string> map)
    {
        try { return map(new Uri(playlist, uri)); }
        catch (UriFormatException) { return uri; }
    }
    public static IReadOnlyList<Uri> MediaUris(string body, Uri playlistUri)
    {
        var list = new List<Uri>();
        using var reader = new StringReader(body);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#')) Add(trimmed);
            else if (trimmed.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                foreach (Match match in TagUrl.Matches(line)) Add(match.Groups[1].Value);
        }
        return list;
        void Add(string value) { try { list.Add(new Uri(playlistUri, value)); } catch (UriFormatException) { } }
    }
}
