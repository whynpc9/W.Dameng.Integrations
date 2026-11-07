using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace W.TickerQ.Dameng;

internal static class RetryIntervalMapping
{
    internal static readonly ValueConverter<int[], string> Converter = new(
        value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<int[]>(json, (JsonSerializerOptions?)null)!);

    internal static readonly ValueComparer<int[]> Comparer = new(
        (left, right) => left == right || (left != null && right != null && left.SequenceEqual(right)),
        value => value == null ? 0 : Hash(value),
        value => value == null ? null! : value.ToArray());

    private static int Hash(int[] value)
    {
        var hash = new HashCode();
        foreach (var interval in value)
            hash.Add(interval);
        return hash.ToHashCode();
    }
}
