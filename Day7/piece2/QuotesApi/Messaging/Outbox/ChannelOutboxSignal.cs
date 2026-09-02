using System.Threading.Channels;

namespace QuotesApi.Messaging.Outbox;

/// <summary>
/// A bounded channel of capacity one with DropWrite, which is the right shape
/// for a "there is work" edge rather than a queue of items.
///
/// Capacity one because the signal carries no information beyond "look again",
/// so a hundred commits in a burst need one wake-up, not a hundred. DropWrite
/// because Notify() runs on a request thread that has already committed: it
/// must never block and must never throw, and a full channel already means the
/// relay has an unconsumed wake-up pending, so dropping loses nothing.
/// </summary>
public sealed class ChannelOutboxSignal : IOutboxSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });

    public void Notify() => _channel.Writer.TryWrite(0);

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            return await _channel.Reader.ReadAsync(linked.Token) is 0;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            // Poll interval elapsed with no signal. Not an error -- this is
            // the fallback path doing its job.
            return false;
        }
    }
}
