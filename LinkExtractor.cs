using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace LinkDownloader
{
    public class LinkItem
    {
        public string Url { get; set; }
        public string FileName { get; set; }
        public string Host { get; set; }
        public string Extension { get; set; }
        public string Category { get; set; }
    }

    public static class LinkExtractor
    {
        private static readonly Regex AttributeRegex = new Regex(
            @"(?<name>href|src|poster|action|data-src|data-href)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UrlRegex = new Regex(
            @"(?<value>(?:https?:)?//[^\s""'<>()\[\]{}]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BaseRegex = new Regex(
            @"<base\b[^>]*\bhref\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BaseTagRegex = new Regex(
            @"<base\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly char[] TrailingPunctuation = new char[] { '.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'' };

        public static List<LinkItem> Extract(string content, string baseUrl, out int skipped)
        {
            skipped = 0;
            List<LinkItem> result = new List<LinkItem>();
            if (string.IsNullOrEmpty(content))
            {
                return result;
            }

            Uri baseUri = null;
            string effectiveBase = baseUrl;
            Match baseMatch = BaseRegex.Match(content);
            if (baseMatch.Success)
            {
                string baseHref = Decode(baseMatch.Groups["value"].Value);
                Uri candidate;
                if (Uri.TryCreate(baseHref, UriKind.Absolute, out candidate))
                {
                    effectiveBase = candidate.AbsoluteUri;
                }
                else if (TryCreateBase(baseUrl, out candidate))
                {
                    effectiveBase = new Uri(candidate, baseHref).AbsoluteUri;
                }
            }
            TryCreateBase(effectiveBase, out baseUri);

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string scanContent = BaseTagRegex.Replace(content, " ");

            foreach (Match m in AttributeRegex.Matches(scanContent))
            {
                AddCandidate(result, seen, m.Groups["value"].Value, baseUri, ref skipped);
            }
            foreach (Match m in UrlRegex.Matches(scanContent))
            {
                AddCandidate(result, seen, m.Groups["value"].Value, baseUri, ref skipped);
            }

            return result;
        }

        public static bool TryCreateBase(string value, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            value = value.Trim();
            if (!value.Contains("://"))
            {
                value = "https://" + value.TrimStart('/');
            }
            return Uri.TryCreate(value, UriKind.Absolute, out uri);
        }

        private static void AddCandidate(List<LinkItem> result, HashSet<string> seen, string raw,
            Uri baseUri, ref int skipped)
        {
            raw = Decode(raw).Trim();
            if (raw.Length == 0)
            {
                return;
            }

            string lower = raw.ToLowerInvariant();
            if (lower.StartsWith("#") ||
                lower.StartsWith("javascript:") ||
                lower.StartsWith("mailto:") ||
                lower.StartsWith("tel:") ||
                lower.StartsWith("data:") ||
                lower.StartsWith("about:") ||
                lower.StartsWith("file:") ||
                lower.StartsWith("blob:"))
            {
                skipped++;
                return;
            }

            while (raw.Length > 0 && Array.IndexOf(TrailingPunctuation, raw[raw.Length - 1]) >= 0)
            {
                raw = raw.Substring(0, raw.Length - 1);
            }
            if (raw.Length == 0)
            {
                return;
            }

            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri))
            {
                if (baseUri == null)
                {
                    skipped++;
                    return;
                }
                if (!Uri.TryCreate(baseUri, raw, out uri))
                {
                    skipped++;
                    return;
                }
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                skipped++;
                return;
            }

            UriBuilder builder = new UriBuilder(uri);
            builder.Fragment = "";
            string normalized = builder.Uri.AbsoluteUri;
            if (!seen.Add(normalized))
            {
                return;
            }

            LinkItem item = new LinkItem();
            item.Url = normalized;
            item.FileName = HttpUtil.InferFileName(normalized);
            item.Host = builder.Host;
            item.Extension = System.IO.Path.GetExtension(builder.Path).TrimStart('.').ToLowerInvariant();
            item.Category = HttpUtil.CategoryForExtension(item.Extension);
            result.Add(item);
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                return WebUtility.HtmlDecode(value);
            }
            catch
            {
                return value;
            }
        }
    }
}
