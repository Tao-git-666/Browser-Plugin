using System.Threading.Channels;
using CrmLogicLens.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api.Services;

public sealed class AnalysisJobQueue
{
    private readonly Channel<Guid> _channel;

    public AnalysisJobQueue(IOptions<AnalysisQueueOptions> options)
    {
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(jobId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
