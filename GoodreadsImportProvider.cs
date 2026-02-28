using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Goodreads;

/// <summary>
/// Chronicle import plugin for Goodreads.
///
/// Goodreads shut down their public API in December 2020.
/// This plugin uses two alternative data sources:
///
///   RSS mode  — Fetches public shelf RSS feeds.
///               Requires the user's numeric Goodreads user ID (from their profile URL).
///               The targeted shelves must be set to Public in Goodreads account settings.
///               Paginated at 200 items/page; all standard shelves are fetched.
///
///   CSV mode  — Parses the library export CSV downloaded from:
///               goodreads.com → My Books → Import/Export → Export Library
///               Works for all shelves (including private ones).
///               No account access or API key required — just a local file path.
///
/// Rating conversion (Goodreads 1–5 → Chronicle 1–10):
///   Chronicle rating = Goodreads rating × 2
///   (e.g. ★★★★ = 4 → 8 in Chronicle; unrated / 0 = skipped)
///
/// Shelf → LibraryStatus mapping:
///   read              → Completed  (ProgressPercent = 100)
///   currently-reading → Watching   (ProgressPercent = null)
///   to-read           → PlanToWatch
/// </summary>
public sealed class GoodreadsImportProvider : IImportProvider
{
    // ── IImportProvider identity ──────────────────────────────────────────────

    public string PluginId    => "goodreads";
    public string Name        => "Goodreads";
    public string Version     => "1.0.0";
    public string Author      => "Michael Beck";
    public string Description =>
        "Import reading history, ratings and shelves from Goodreads via RSS feeds or CSV export.";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyImportMode  = "import_mode";
    private const string KeyUserId      = "goodreads_user_id";
    private const string KeyCsvFilePath = "csv_file_path";

    private const string ModeRss = "rss";
    private const string ModeCsv = "csv";

    // ── Runtime state ─────────────────────────────────────────────────────────

    private string _importMode  = ModeRss;
    private string _userId      = string.Empty;
    private string _csvFilePath = string.Empty;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key          = KeyImportMode,
                Label        = "Import Mode",
                Description  = "Choose how to import your Goodreads data. " +
                               "RSS requires a public profile; CSV works for any account.",
                Type         = SettingType.Dropdown,
                DefaultValue = ModeRss,
                Options      =
                [
                    new SelectOption { Value = ModeRss, Label = "RSS Feeds (public shelves)" },
                    new SelectOption { Value = ModeCsv, Label = "CSV Export (all shelves)" },
                ],
            },
            new SettingDefinition
            {
                Key         = KeyUserId,
                Label       = "Goodreads User ID",
                Description = "Your numeric user ID from your Goodreads profile URL. " +
                              "Example: goodreads.com/user/show/12345678 → ID is 12345678. " +
                              "Required for RSS mode. Your shelves must be set to Public.",
                Type        = SettingType.Text,
                Required    = false,
            },
            new SettingDefinition
            {
                Key         = KeyCsvFilePath,
                Label       = "CSV File Path",
                Description = "Full path to your Goodreads library export CSV. " +
                              "Export from: goodreads.com → My Books → Import/Export → Export Library. " +
                              "Required for CSV mode.",
                Type        = SettingType.FilePath,
                Required    = false,
            },
        ]
    };

    // ── Configure ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue(KeyImportMode, out var mode) &&
            mode is ModeRss or ModeCsv)
            _importMode = mode;

        settings.TryGetValue(KeyUserId,      out var userId);
        settings.TryGetValue(KeyCsvFilePath, out var csvFilePath);

        _userId      = userId?.Trim()      ?? string.Empty;
        _csvFilePath = csvFilePath?.Trim() ?? string.Empty;
    }

    // ── Auth — no OAuth device flow ───────────────────────────────────────────

    public Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Goodreads does not support OAuth authentication. " +
            "For RSS mode, enter your public Goodreads User ID (from your profile URL). " +
            "For CSV mode, export your library from goodreads.com and supply the file path.");

    public Task<DeviceAuthPollResult> PollAuthAsync(
        string pollCode, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Goodreads does not use a device code / poll flow.");

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        var ok = _importMode == ModeCsv
            ? !string.IsNullOrWhiteSpace(_csvFilePath) && File.Exists(_csvFilePath)
            : !string.IsNullOrWhiteSpace(_userId);

        return Task.FromResult(ok);
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public ImportCapabilities GetCapabilities() =>
        new(SupportsHistory:    true,
            SupportsRatings:    true,
            SupportsWatchlist:  true,
            RequiresDeviceAuth: false);

    // ── Import — history ──────────────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var rows = await FetchAllBooksAsync(ct);
        var result = new List<ImportedWatchEvent>();

        foreach (var row in rows)
        {
            if (row.Shelf is not ("read" or "currently-reading")) continue;

            var watchedAt = row.ReadAt ?? row.AddedAt ?? DateTimeOffset.UtcNow;
            if (since.HasValue && watchedAt < since.Value) continue;

            result.Add(new ImportedWatchEvent(
                ExternalId:      BuildExternalId(row.Ids),
                AdditionalIds:   row.Ids,
                MediaType:       "book",
                Title:           row.Title,
                Year:            row.Year,
                WatchedAt:       watchedAt,
                ProgressPercent: row.Shelf == "read" ? 100.0 : null
            ));
        }

        return result;
    }

    // ── Import — ratings ──────────────────────────────────────────────────────

    public async Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default)
    {
        var rows = await FetchAllBooksAsync(ct);
        var result = new List<ImportedRating>();

        foreach (var row in rows)
        {
            if (!row.Rating.HasValue || row.Rating.Value <= 0) continue;

            // Goodreads 1–5 → Chronicle 1–10
            var chronicleRating = Math.Clamp(row.Rating.Value * 2, 1, 10);
            var ratedAt = row.ReadAt ?? row.AddedAt ?? DateTimeOffset.UtcNow;

            result.Add(new ImportedRating(
                ExternalId:    BuildExternalId(row.Ids),
                AdditionalIds: row.Ids,
                MediaType:     "book",
                Title:         row.Title,
                Year:          row.Year,
                Rating:        chronicleRating,
                RatedAt:       ratedAt
            ));
        }

        return result;
    }

    // ── Import — watchlist ────────────────────────────────────────────────────

    public async Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(
        CancellationToken ct = default)
    {
        var rows = await FetchAllBooksAsync(ct);
        var result = new List<ImportedWatchlistEntry>();

        foreach (var row in rows)
        {
            if (row.Shelf != "to-read") continue;

            result.Add(new ImportedWatchlistEntry(
                ExternalId:    BuildExternalId(row.Ids),
                AdditionalIds: row.Ids,
                MediaType:     "book",
                Title:         row.Title,
                Year:          row.Year,
                AddedAt:       row.AddedAt ?? DateTimeOffset.UtcNow
            ));
        }

        return result;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        await IsAuthenticatedAsync(ct);

    // ── Internal data model ───────────────────────────────────────────────────

    private readonly record struct BookRow(
        Dictionary<string, string> Ids,
        string              Title,
        int?                Year,
        int?                Rating,
        DateTimeOffset?     ReadAt,
        DateTimeOffset?     AddedAt,
        string              Shelf
    );

    // ── Unified fetch — routes to RSS or CSV ──────────────────────────────────

    private Task<List<BookRow>> FetchAllBooksAsync(CancellationToken ct = default) =>
        _importMode == ModeCsv
            ? Task.FromResult(FetchFromCsv())
            : FetchFromRssAsync(ct);

    // ── CSV path ──────────────────────────────────────────────────────────────

    private List<BookRow> FetchFromCsv()
    {
        if (string.IsNullOrWhiteSpace(_csvFilePath))
            throw new InvalidOperationException(
                "CSV file path is not configured. " +
                "Set it in Plugins → Goodreads → Settings → CSV File Path.");

        var csvRows = GoodreadsCsvImporter.Parse(_csvFilePath);
        var result  = new List<BookRow>(csvRows.Count);

        foreach (var row in csvRows)
        {
            var ids   = BuildIds(row.BookId.ToString(), row.Isbn, row.Isbn13);
            var shelf = NormaliseShelf(row.ExclusiveShelf ?? string.Empty);

            DateTimeOffset? readAt  = null;
            DateTimeOffset? addedAt = null;

            if (DateTimeOffset.TryParse(row.DateRead,  out var dr)) readAt  = dr;
            if (DateTimeOffset.TryParse(row.DateAdded, out var da)) addedAt = da;

            result.Add(new BookRow(ids, row.Title, row.YearPublished,
                row.MyRating, readAt, addedAt, shelf));
        }

        return result;
    }

    // ── RSS path ──────────────────────────────────────────────────────────────

    private async Task<List<BookRow>> FetchFromRssAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_userId))
            throw new InvalidOperationException(
                "Goodreads User ID is not configured. " +
                "Set it in Plugins → Goodreads → Settings → Goodreads User ID.");

        using var client = new GoodreadsRssClient();
        var result       = new List<BookRow>();

        // Fetch all three standard shelves
        foreach (var shelf in new[] { "read", "currently-reading", "to-read" })
        {
            ct.ThrowIfCancellationRequested();
            var items = await client.GetShelfAsync(_userId, shelf, ct);

            foreach (var item in items)
            {
                var ids = BuildIds(item.BookId.ToString(), item.Isbn, item.Isbn13);
                result.Add(new BookRow(
                    ids,
                    item.Title,
                    item.BookPublished,
                    item.UserRating,
                    item.UserReadAt,
                    item.UserDateAdded,
                    NormaliseShelf(shelf)
                ));
            }
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildIds(
        string? goodreadsId, string? isbn, string? isbn13)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(goodreadsId)) d["goodreads"] = goodreadsId;
        if (!string.IsNullOrWhiteSpace(isbn13))      d["isbn13"]    = isbn13;
        if (!string.IsNullOrWhiteSpace(isbn))        d["isbn"]      = isbn;
        return d;
    }

    private static string BuildExternalId(Dictionary<string, string> ids)
    {
        if (ids.TryGetValue("goodreads", out var gid) && !string.IsNullOrWhiteSpace(gid))
            return $"goodreads:{gid}";
        if (ids.TryGetValue("isbn13",    out var i13) && !string.IsNullOrWhiteSpace(i13))
            return $"isbn13:{i13}";
        if (ids.TryGetValue("isbn",      out var i10) && !string.IsNullOrWhiteSpace(i10))
            return $"isbn:{i10}";
        return string.Empty;
    }

    /// <summary>Normalises Goodreads shelf names to canonical lower-kebab form.</summary>
    private static string NormaliseShelf(string shelf) =>
        shelf.Trim().ToLowerInvariant() switch
        {
            "read"              => "read",
            "currently-reading" => "currently-reading",
            "to-read"           => "to-read",
            var other           => other,
        };
}
