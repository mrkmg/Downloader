﻿using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader
{
    internal class ChunkDownloader
    {
        private const int TimeoutIncrement = 10;
        private ThrottledStream destinationStream;
        public event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;
        public DownloadConfiguration Configuration { get; protected set; }
        public Chunk Chunk { get; protected set; }

        public ChunkDownloader(Chunk chunk, DownloadConfiguration config)
        {
            Chunk = chunk;
            Configuration = config;
            Configuration.PropertyChanged += ConfigurationPropertyChanged;
        }

        private void ConfigurationPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Configuration.MaximumBytesPerSecond) &&
                destinationStream?.CanRead == true)
            {
                destinationStream.BandwidthLimit = Configuration.MaximumSpeedPerChunk;
            }
        }

        public async Task<Chunk> Download(Request downloadRequest, CancellationToken cancellationToken)
        {
            try
            {
                await DownloadChunk(downloadRequest, cancellationToken).ConfigureAwait(false);
                return Chunk;
            }
            catch (TaskCanceledException) // when stream reader timeout occurred 
            {
                // re-request and continue downloading...
                return await Download(downloadRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (WebException) when (Chunk.CanTryAgainOnFailover())
            {
                // when the host forcibly closed the connection.
                await Task.Delay(Chunk.Timeout, cancellationToken).ConfigureAwait(false);
                // re-request and continue downloading...
                return await Download(downloadRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (Chunk.CanTryAgainOnFailover() &&
                                          (error.HasSource("System.Net.Http") ||
                                           error.HasSource("System.Net.Sockets") ||
                                           error.HasSource("System.Net.Security") ||
                                           error.InnerException is SocketException))
            {
                Chunk.Timeout += TimeoutIncrement; // decrease download speed to down pressure on host
                await Task.Delay(Chunk.Timeout, cancellationToken).ConfigureAwait(false);
                // re-request and continue downloading...
                return await Download(downloadRequest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await Task.Yield();
            }
        }

        private async Task DownloadChunk(Request downloadRequest, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Chunk.IsDownloadCompleted() == false)
            {
                HttpWebRequest request = downloadRequest.GetRequest();
                SetRequestRange(request);
                using HttpWebResponse downloadResponse = request.GetResponse() as HttpWebResponse;
                if (downloadResponse.StatusCode == HttpStatusCode.OK ||
                    downloadResponse.StatusCode == HttpStatusCode.PartialContent ||
                   downloadResponse.StatusCode == HttpStatusCode.Created ||
                   downloadResponse.StatusCode == HttpStatusCode.Accepted ||
                   downloadResponse.StatusCode == HttpStatusCode.ResetContent)
                {
                    Configuration.RequestConfiguration.CookieContainer = request.CookieContainer;
                    using Stream responseStream = downloadResponse?.GetResponseStream();
                    if (responseStream != null)
                    {
                        using (destinationStream = new ThrottledStream(responseStream, Configuration.MaximumSpeedPerChunk))
                        {
                            await ReadStream(destinationStream, token).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    throw new WebException($"Download response status was {downloadResponse.StatusCode}: {downloadResponse.StatusDescription}");
                }
            }
        }

        private void SetRequestRange(HttpWebRequest request)
        {
            // has limited range
            if (Chunk.End > 0 &&
                (Configuration.ChunkCount > 1 || Chunk.Position > 0 || Configuration.RangeDownload))
            {
                request.AddRange(Chunk.Start + Chunk.Position, Chunk.End);
            }
        }

        internal async Task ReadStream(Stream stream, CancellationToken token)
        {
            int readSize = 1;
            while (CanReadStream() && readSize > 0)
            {
                token.ThrowIfCancellationRequested();

                using var innerCts = new CancellationTokenSource(Chunk.Timeout);
                byte[] buffer = new byte[Configuration.BufferBlockSize];
                readSize = await stream.ReadAsync(buffer, 0, buffer.Length, innerCts.Token).ConfigureAwait(false);
                await Chunk.Storage.WriteAsync(buffer, 0, readSize).ConfigureAwait(false);
                Chunk.Position += readSize;

                OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(Chunk.Id) {
                    TotalBytesToReceive = Chunk.Length,
                    ReceivedBytesSize = Chunk.Position,
                    ProgressedByteSize = readSize,
                    ReceivedBytes = buffer.Take(readSize).ToArray()
                });
            }
        }

        private bool CanReadStream()
        {
            return Chunk.Length == 0 ||
                   Chunk.Length - Chunk.Position > 0;
        }

        private void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            DownloadProgressChanged?.Invoke(this, e);
        }
    }
}