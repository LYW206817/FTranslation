namespace FastTranslation.Core;

/// <summary>
/// Numeric release version ("1.1.0", "1.1.0.0", "v1.1.0") compared field by field.
/// Prerelease and build metadata are stripped: the updater reads one release channel,
/// so "1.1.0-beta+abc" ranks as 1.1.0 and never orders below it.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, int Revision)
    : IComparable<ReleaseVersion>
{
    public static readonly ReleaseVersion None = new(0, 0, 0, 0);

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim().AsSpan();
        if (span[0] is 'v' or 'V') span = span[1..];
        var cut = span.IndexOfAny('+', '-');
        if (cut >= 0) span = span[..cut];
        if (span.IsEmpty) return false;

        Span<int> parts = stackalloc int[4];
        var count = 0;
        foreach (var range in span.Split('.'))
        {
            if (count == parts.Length) return false;
            var piece = span[range];
            if (piece.IsEmpty || piece.Length > 9) return false;
            if (!int.TryParse(piece, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
            parts[count++] = value;
        }
        if (count == 0) return false;

        version = new ReleaseVersion(parts[0],
            count > 1 ? parts[1] : 0,
            count > 2 ? parts[2] : 0,
            count > 3 ? parts[3] : 0);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a release version");

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    public bool IsNewerThan(ReleaseVersion other) => CompareTo(other) > 0;

    public override string ToString() => Revision > 0
        ? $"{Major}.{Minor}.{Patch}.{Revision}"
        : $"{Major}.{Minor}.{Patch}";
}
