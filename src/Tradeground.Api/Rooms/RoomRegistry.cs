using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Tradeground.Game;

namespace Tradeground.Api.Rooms;

/// <summary>Holds all live rooms in memory. Phase 2 moves room state to Postgres and Redis.</summary>
public sealed class RoomRegistry(TimeProvider time)
{
    // No 0/O or 1/I/L, so codes are easy to read aloud in a classroom.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;

    private readonly ConcurrentDictionary<string, RoomActor> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public RoomActor Create(GameMode mode, ulong seed)
    {
        while (true)
        {
            var code = RandomNumberGenerator.GetString(CodeAlphabet, CodeLength);
            var actor = new RoomActor(code, Secrets.NewToken(), new GameRoom(mode, seed), time.GetUtcNow());
            if (_rooms.TryAdd(code, actor))
            {
                return actor;
            }

            // Extremely rare code collision: throw this actor away and try another code.
            _ = actor.DisposeAsync().AsTask();
        }
    }

    public bool TryGet(string code, [NotNullWhen(true)] out RoomActor? room) => _rooms.TryGetValue(code, out room);
}

public static class Secrets
{
    /// <summary>128 random bits as hex: unguessable, so it can act as a bearer key.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Constant-time comparison, so response timing can't leak how much of a key matched.</summary>
    public static bool Matches(string? provided, string expected) =>
        provided is not null &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(expected));
}
