using System.Globalization;
using System.Security.Cryptography;

namespace Tradeground.Api.Rooms;

/// <summary>
/// Players never type their own names: no personal data from children, and nothing to moderate.
/// </summary>
public static class NameGenerator
{
    private static readonly string[] Adjectives =
    [
        "Bullish", "Steady", "Clever", "Swift", "Patient", "Bold", "Calm", "Curious", "Lucky", "Sharp",
    ];

    private static readonly string[] Animals =
    [
        "Otter", "Falcon", "Panda", "Fox", "Owl", "Dolphin", "Tiger", "Koala", "Badger", "Heron",
    ];

    public static string Next() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Pick(Adjectives)} {Pick(Animals)} {RandomNumberGenerator.GetInt32(10, 100)}");

    private static string Pick(string[] options) => options[RandomNumberGenerator.GetInt32(options.Length)];
}
