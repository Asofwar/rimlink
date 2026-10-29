namespace Rimlink.Client.Net;

/// <summary>
/// Dev tooling only (the BridgeTools net_drop tool): a network outage of one client without touching the machine's
/// network. <see cref="Drop"/> breaks every request and response stream in flight and fails new requests until the
/// time is up, as a lost connection would; nothing happens unless it is called.
/// </summary>
public sealed class NetworkDropHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly object sync = new();
    private CancellationTokenSource outage = new();
    private DateTime downUntil = DateTime.MinValue;

    public bool Down
    {
        get { lock (sync) return DateTime.UtcNow < downUntil; }
    }

    public void Drop(TimeSpan duration)
    {
        CancellationTokenSource broken;
        lock (sync)
        {
            downUntil = DateTime.UtcNow + duration;
            broken = outage;
            outage = new CancellationTokenSource();
        }
        broken.Cancel();
        broken.Dispose();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CancellationToken lost;
        lock (sync)
        {
            if (DateTime.UtcNow < downUntil) throw Lost();
            lost = outage.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lost);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Lost();
        }
        // The body of a streaming call is read long after SendAsync returns: it has to break on a drop too.
        response.Content = new DroppableContent(response.Content, lost);
        return response;
    }

    /// <summary>Shaped like a failed socket, so gRPC reports Unavailable as for a real outage.</summary>
    private static HttpRequestException Lost() =>
        new(HttpRequestError.ConnectionError, "network down (dev drop)", new IOException("network down (dev drop)"));

    private sealed class DroppableContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly CancellationToken lost;

        public DroppableContent(HttpContent inner, CancellationToken lost)
        {
            this.inner = inner;
            this.lost = lost;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => inner.CopyToAsync(stream, context);

        protected override async Task<Stream> CreateContentReadStreamAsync() => new DroppableStream(await inner.ReadAsStreamAsync(), lost);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class DroppableStream(Stream inner, CancellationToken lost) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lost);
            try
            {
                return await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (lost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("network down (dev drop)");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
