using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GoldenSyrupGames.T2MD.Http
{
    /// <summary>
    /// Constants for HTTP utilities
    /// </summary>
    public static class HttpConstants
    {
        /// <summary>
        /// Default rate limit in requests per second
        /// </summary>
        public const int DefaultRateLimit = 10;
    }

    // Rate limiting class for controlling API request rates
    public sealed class RateLimiter : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _requestsPerSecond;
        private readonly Channel<DateTime> _requestTimestamps;
        private readonly CancellationTokenSource _cts;

        public RateLimiter(int requestsPerSecond)
        {
            _requestsPerSecond = Math.Max(1, requestsPerSecond); // Ensure minimum of 1 request per second
            _semaphore = new SemaphoreSlim(_requestsPerSecond, _requestsPerSecond);
            _requestTimestamps = Channel.CreateUnbounded<DateTime>();
            _cts = new CancellationTokenSource();
            // Start the cleanup task without keeping a reference
            // This task will run for the lifetime of the RateLimiter instance
            _ = RunCleanupAsync(_cts.Token);
        }

        private async Task RunCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for the next timestamp
                    if (await _requestTimestamps.Reader.WaitToReadAsync(cancellationToken))
                    {
                        if (_requestTimestamps.Reader.TryPeek(out DateTime oldestRequest))
                        {
                            // Calculate time to wait until the oldest request can be released (1 second from its timestamp)
                            TimeSpan timeToWait = oldestRequest.AddSeconds(1) - DateTime.UtcNow;

                            if (timeToWait > TimeSpan.Zero)
                            {
                                await Task.Delay(timeToWait, cancellationToken);
                            }

                            // Remove the timestamp and release the semaphore
                            if (_requestTimestamps.Reader.TryRead(out _))
                            {
                                _semaphore.Release();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        public async Task WaitAsync()
        {
            await _semaphore.WaitAsync();
            await _requestTimestamps.Writer.WriteAsync(DateTime.UtcNow);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _semaphore.Dispose();
            _cts.Dispose();
        }
    }

    // HTTP client wrapper that enforces rate limiting for all requests
    public sealed class RateLimitedHttpClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly RateLimiter _rateLimiter;

        public RateLimitedHttpClient(int requestsPerSecond)
        {
            _httpClient = new HttpClient();
            _rateLimiter = new RateLimiter(requestsPerSecond);
        }

        public async Task<string> GetStringAsync(string url)
        {
            await _rateLimiter.WaitAsync();
            return await _httpClient.GetStringAsync(url);
        }

        public async Task<HttpResponseMessage> GetAsync(string url)
        {
            await _rateLimiter.WaitAsync();
            return await _httpClient.GetAsync(url);
        }

        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content)
        {
            await _rateLimiter.WaitAsync();
            return await _httpClient.PostAsync(url, content);
        }

        public async Task<byte[]> GetByteArrayAsync(string url)
        {
            await _rateLimiter.WaitAsync();
            return await _httpClient.GetByteArrayAsync(url);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            await _rateLimiter.WaitAsync();
            return await _httpClient.SendAsync(request);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _rateLimiter.Dispose();
        }
    }
}
