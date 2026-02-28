using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace Chronicle.Plugin.Goodreads;

/// <summary>
/// Parses a Goodreads library CSV export file.
///
/// How to obtain the CSV:
///   1. Log in to goodreads.com
///   2. Go to My Books → Import/Export
///   3. Click "Export Library"
///   4. Download the resulting .csv file
///   5. Point Chronicle's Goodreads plugin settings at the file path
///
/// Notes on the format:
/// - ISBN / ISBN13 columns are stored as Excel formula strings: ="9781234567890"
///   The raw strings are cleaned by <see cref="StripExcelIsbn"/> after parsing.
/// - The "My Rating" column is 0 when the user has not rated the book.
/// - "Exclusive Shelf" is always exactly one of: "read", "currently-reading", "to-read".
/// </summary>
public static class GoodreadsCsvImporter
{
    /// <summary>Parses the CSV at <paramref name="filePath"/> and returns all rows.</summary>
    /// <exception cref="FileNotFoundException">File does not exist.</exception>
    /// <exception cref="InvalidOperationException">File cannot be parsed.</exception>
    public static List<GoodreadsCsvRow> Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(
                $"Goodreads CSV not found at: {filePath}", filePath);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord   = true,
            MissingFieldFound = null,   // ignore missing columns gracefully
            BadDataFound      = null,   // skip malformed rows rather than throwing
        };

        using var reader = new StreamReader(filePath);
        using var csv    = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<GoodreadsCsvRowMap>();

        try
        {
            var rows = csv.GetRecords<GoodreadsCsvRow>().ToList();

            // Clean ISBN fields after parsing — Goodreads wraps them as ="value"
            foreach (var row in rows)
            {
                row.Isbn   = StripExcelIsbn(row.Isbn);
                row.Isbn13 = StripExcelIsbn(row.Isbn13);
            }

            return rows;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse Goodreads CSV at '{filePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Goodreads exports ISBN cells as Excel formula strings: <c>="9781234567890"</c>.
    /// Strips the leading <c>="</c> and trailing <c>"</c>.
    /// Returns null for null, whitespace, or placeholder zero values.
    /// </summary>
    public static string? StripExcelIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();

        // Strip Excel formula prefix/suffix: ="..." → ...
        if (s.StartsWith("=\"", StringComparison.Ordinal)) s = s[2..];
        if (s.EndsWith('"'))                               s = s[..^1];

        s = s.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}

// ── CsvHelper class map ───────────────────────────────────────────────────────

/// <summary>Maps CSV column names to <see cref="GoodreadsCsvRow"/> properties.</summary>
internal sealed class GoodreadsCsvRowMap : ClassMap<GoodreadsCsvRow>
{
    public GoodreadsCsvRowMap()
    {
        Map(m => m.BookId).Name("Book Id");
        Map(m => m.Title).Name("Title");
        Map(m => m.Author).Name("Author");
        Map(m => m.Isbn).Name("ISBN");           // raw ="..." cleaned after parsing
        Map(m => m.Isbn13).Name("ISBN13");       // raw ="..." cleaned after parsing
        Map(m => m.MyRating).Name("My Rating").Optional();
        Map(m => m.YearPublished).Name("Year Published").Optional();
        Map(m => m.DateRead).Name("Date Read").Optional();
        Map(m => m.DateAdded).Name("Date Added").Optional();
        Map(m => m.ExclusiveShelf).Name("Exclusive Shelf").Optional();
    }
}
