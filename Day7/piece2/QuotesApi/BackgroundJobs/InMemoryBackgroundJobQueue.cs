using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace QuotesApi.BackgroundJobs;

public sealed class InMemoryBackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<QuoteAuthorReportJob> _channel;

    public InMemoryBackgroundJobQueue(IOptions<BackgroundJobQueueOptions> options)
    {
        _channel = Channel.CreateBounded<QuoteAuthorReportJob>(
            new BoundedChannelOptions(options.Value.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
    }

    public bool TryEnqueue(QuoteAuthorReportJob job) => _channel.Writer.TryWrite(job);

    public ValueTask<QuoteAuthorReportJob> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
