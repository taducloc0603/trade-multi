namespace TradeDesktop.Application.Models;

public sealed record SharedMapReadResult<T>(
    bool IsMapAvailable,
    bool IsParseSuccess,
    string? ErrorMessage,
    int Count,
    ulong Timestamp,
    IReadOnlyList<T> Records)
{
    public static SharedMapReadResult<T> MapNotFound(string mapName)
        => new(
            IsMapAvailable: false,
            IsParseSuccess: false,
            ErrorMessage: $"Không tìm thấy map: {mapName}",
            Count: 0,
            Timestamp: 0,
            Records: Array.Empty<T>());

    public static SharedMapReadResult<T> ParseError(string errorMessage, bool isMapAvailable = true)
        => new(
            IsMapAvailable: isMapAvailable,
            IsParseSuccess: false,
            ErrorMessage: errorMessage,
            Count: 0,
            Timestamp: 0,
            Records: Array.Empty<T>());

    public static SharedMapReadResult<T> Success(ulong timestamp, IReadOnlyList<T> records, int count)
        => new(
            IsMapAvailable: true,
            IsParseSuccess: true,
            ErrorMessage: null,
            Count: count,
            Timestamp: timestamp,
            Records: records);
}