using System.Threading.Channels;
using Tradeground.Game;

namespace Tradeground.Api.Rooms;

public sealed class RoomBusyException() : Exception("The room is too busy. Try again.");

/// <summary>
/// The actor model: one room, one mailbox, one worker.
/// </summary>
/// <remarks>
/// Many HTTP requests arrive at the same time on different threads. Instead of locking the room,
/// each request drops a work item into a channel. A single loop takes items out one by one and
/// runs them against the room. Only that loop ever touches the GameRoom, so the game logic needs
/// no locks and every room processes orders in a strict, well-defined sequence.
/// The channel is bounded: if a flood of requests fills it, new ones fail fast with
/// RoomBusyException instead of growing memory without limit (backpressure).
/// </remarks>
public sealed class RoomActor : IAsyncDisposable
{
    private const int MailboxCapacity = 1_000;

    private readonly GameRoom _room;
    private readonly Channel<Action> _mailbox;
    private readonly Task _worker;

    public RoomActor(string code, string hostKey, GameRoom room, DateTimeOffset createdAt)
    {
        Code = code;
        HostKey = hostKey;
        CreatedAt = createdAt;
        _room = room;
        _mailbox = Channel.CreateBounded<Action>(new BoundedChannelOptions(MailboxCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(RunAsync);
    }

    public string Code { get; }
    public string HostKey { get; }
    public DateTimeOffset CreatedAt { get; }
    public GameMode Mode => _room.Mode;

    /// <summary>Runs <paramref name="work"/> on the room's single worker and returns its result.</summary>
    public Task<T> Ask<T>(Func<GameRoom, T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Run()
        {
            try
            {
                completion.SetResult(work(_room));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }

        if (!_mailbox.Writer.TryWrite(Run))
        {
            completion.SetException(new RoomBusyException());
        }

        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var item in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            item();
        }
    }
}
