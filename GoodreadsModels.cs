namespace Chronicle.Plugin.Goodreads;

// ── RSS models ────────────────────────────────────────────────────────────────

/// <summary>
/// One book item parsed from a Goodreads public shelf RSS feed.
/// </summary>
public record RssBookItem(
    long            BookId,
    string          Title,
    string          Author,
    string?         Isbn,
    string?         Isbn13,
    int?            UserRating,
    DateTimeOffset? UserReadAt,
    DateTimeOffset? UserDateAdded,
    /// <summary>
    /// The shelf name as returned by the RSS feed, e.g. "read",
    /// "currently-reading", or "to-read".
    /// </summary>
    string?         UserShelves,
    int?            BookPublished
);

// ── CSV models ────────────────────────────────────────────────────────────────

/// <summary>
/// Maps one row from a Goodreads library CSV export.
/// Goodreads exports ISBN fields as Excel formula strings (="value"),
/// so ISBN properties are stripped by <see cref="GoodreadsCsvImporter"/> before use.
/// </summary>
public class GoodreadsCsvRow
{
    public long    BookId         { get; set; }
    public string  Title          { get; set; } = string.Empty;
    public string  Author         { get; set; } = string.Empty;
    /// <summary>ISBN-10 (cleaned — Excel ="..." prefix removed).</summary>
    public string? Isbn           { get; set; }
    /// <summary>ISBN-13 (cleaned — Excel ="..." prefix removed).</summary>
    public string? Isbn13         { get; set; }
    /// <summary>User's 1–5 star rating (0 = not rated).</summary>
    public int?    MyRating       { get; set; }
    public int?    YearPublished  { get; set; }
    /// <summary>Date the user marked the book as read (may be empty).</summary>
    public string? DateRead       { get; set; }
    /// <summary>Date the book was added to any shelf.</summary>
    public string? DateAdded      { get; set; }
    /// <summary>
    /// The primary shelf — exactly one of: "read", "currently-reading", "to-read".
    /// </summary>
    public string? ExclusiveShelf { get; set; }
}
