using System.Net;
using System.Xml;
using System.Xml.Linq;
using Serilog;

namespace Chronicle.Plugin.Goodreads;

/// <summary>
/// Fetches and parses Goodreads RSS feeds for public shelves.
///
/// URL pattern:
///   https://www.goodreads.com/review/list_rss/{userId}?shelf={shelf}&amp;page={page}
///
/// Each page contains up to 200 items. The client paginates automatically until
/// a page returns fewer than 200 items (indicating the last page).
///
/// Requires: the user's Goodreads profile and the target shelves must be public.
/// No authentication is needed — the user ID from the profile URL is sufficient.
/// </summary>
public sealed class GoodreadsRssClient : IDisposable
{
    private static readonly ILogger _log = Log.ForContext<GoodreadsRssClient>();

    private readonly HttpClient _http;

    private const string BaseUrl   = "https://www.goodreads.com/review/list_rss";
    private const int    PageSize  = 200;
    private const int    PageDelay = 600; // ms between paginated requests — be polite

    public GoodreadsRssClient()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Chronicle/1.0 (+https://github.com/thegoddamnbeckster/Chronicle)");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches all items from the named shelf, paginating automatically.
    /// </summary>
    /// <param name="userId">Numeric Goodreads user ID (from profile URL).</param>
    /// <param name="shelf">Shelf name, e.g. "read", "currently-reading", "to-read".</param>
    public async Task<List<RssBookItem>> GetShelfAsync(
        string userId, string shelf, CancellationToken ct = default)
    {
        var allItems = new List<RssBookItem>();
        var page = 1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{BaseUrl}/{userId}?shelf={shelf}&page={page}";
            HttpResponseMessage response;
            try
            {
                response = await GetWithRateLimitAsync(url, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to fetch Goodreads RSS for shelf '{shelf}' (page {page}): {ex.Message}", ex);
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var items   = ParseRss(content);

            if (items.Count == 0) break;

            allItems.AddRange(items);

            // If we got fewer items than the page size, we've reached the last page
            if (items.Count < PageSize) break;

            page++;
            await Task.Delay(PageDelay, ct);
        }

        return allItems;
    }

    // ── Rate-limit handling ───────────────────────────────────────────────────

    /// <summary>
    /// Goodreads' RSS feed has no published rate limit of any kind (no real API here at all —
    /// Goodreads deprecated its public API in 2020). This previously had zero 429 handling;
    /// a rate-limited response threw immediately. Same shape as Chronicle.Plugin.Letterboxd's
    /// GetWithRateLimitAsync, the closest sibling (also an unofficial RSS-feed consumer with
    /// no documented limits) — a single retry honoring Retry-After, not a SIMKL-style
    /// multi-hour cutoff, since there's no evidence of any daily-quota concept to justify one.
    /// </summary>
    private async Task<HttpResponseMessage> GetWithRateLimitAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfterSec = 30;
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                retryAfterSec = (int)response.Headers.RetryAfter.Delta!.Value.TotalSeconds + 1;

            _log.Warning("Goodreads: rate-limited (429); waiting {Seconds}s before one retry", retryAfterSec);
            await Task.Delay(retryAfterSec * 1000, ct);
            response = await _http.GetAsync(url, ct);
        }

        return response;
    }

    // ── RSS parsing ───────────────────────────────────────────────────────────

    private static List<RssBookItem> ParseRss(string xml)
    {
        var result = new List<RssBookItem>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return result;
        }

        foreach (var item in doc.Descendants("item"))
        {
            // book_id is required — skip if missing or unparseable
            if (!long.TryParse(item.Element("book_id")?.Value, out var bookId))
                continue;

            var title  = item.Element("title")?.Value?.Trim()       ?? string.Empty;
            var author = item.Element("author_name")?.Value?.Trim() ?? string.Empty;

            // Goodreads sometimes puts "Title by Author" in the <title> element.
            // If we have no separate author_name, try to split it out.
            if (string.IsNullOrEmpty(author))
            {
                const string separator = " by ";
                var idx = title.LastIndexOf(separator, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    author = title[(idx + separator.Length)..].Trim();
                    title  = title[..idx].Trim();
                }
            }

            var isbn   = NormaliseIsbn(item.Element("isbn")?.Value);
            var isbn13 = NormaliseIsbn(item.Element("isbn13")?.Value);

            int? userRating = null;
            if (int.TryParse(item.Element("user_rating")?.Value, out var r) && r > 0)
                userRating = r;

            var userReadAt    = ParseRssDate(item.Element("user_read_at")?.Value);
            var userDateAdded = ParseRssDate(item.Element("user_date_added")?.Value)
                             ?? ParseRssDate(item.Element("pubDate")?.Value);

            var userShelves = item.Element("user_shelves")?.Value?.Trim();

            int? published = null;
            if (int.TryParse(item.Element("book_published")?.Value, out var yr) && yr > 0)
                published = yr;

            result.Add(new RssBookItem(
                BookId:        bookId,
                Title:         title,
                Author:        author,
                Isbn:          isbn,
                Isbn13:        isbn13,
                UserRating:    userRating,
                UserReadAt:    userReadAt,
                UserDateAdded: userDateAdded,
                UserShelves:   userShelves,
                BookPublished: published
            ));
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns null for blank, all-zero ISBNs, or ISBNs that are just placeholder zeros.
    /// </summary>
    private static string? NormaliseIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        // Goodreads uses 0000000000 / 0000000000000 as a sentinel for "no ISBN"
        if (s is "0000000000" or "0000000000000") return null;
        return s.Length > 0 ? s : null;
    }

    private static DateTimeOffset? ParseRssDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
    }

    public void Dispose() => _http.Dispose();
}
