using System.Threading.Channels;

namespace AShareRadar.ServiceHost.Jobs;

public sealed class BackgroundJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid jobId)
    {
        _channel.Writer.TryWrite(jobId);
    }
}
