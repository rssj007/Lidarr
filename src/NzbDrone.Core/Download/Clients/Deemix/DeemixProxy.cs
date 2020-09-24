using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers.Exceptions;
using SocketIOClient;

namespace NzbDrone.Core.Download.Clients.Deemix
{
    public class DeemixClientItem : DownloadClientItem
    {
        public List<string> Files { get; set; } = new List<string>();
        public DateTime Started { get; set; }
    }

    public class DeemixProxy : IDisposable
    {
        private static int AddId;

        private readonly Logger _logger;
        private readonly ManualResetEventSlim _configReceived;
        private readonly Dictionary<int, DeemixPendingItem<string>> _pendingAdds;
        private readonly Dictionary<int, DeemixPendingItem<DeemixSearchResponse>> _pendingSearches;

        private bool _disposed;
        private SocketIO _client;
        private List<DeemixClientItem> _queue;
        private DeemixConfig _config;

        public DeemixProxy(string url,
                           Logger logger)
        {
            _logger = logger;

            _configReceived = new ManualResetEventSlim(false);
            _queue = new List<DeemixClientItem>();

            _pendingAdds = new Dictionary<int, DeemixPendingItem<string>>();
            _pendingSearches = new Dictionary<int, DeemixPendingItem<DeemixSearchResponse>>();

            Connect(url);
        }

        public DeemixConfig GetSettings()
        {
            if (!_client.Connected)
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            return _config;
        }

        public List<DeemixClientItem> GetQueue()
        {
            if (!_client.Connected)
            {
                throw new DownloadClientUnavailableException("Deemix not connected");
            }

            foreach (var item in _queue)
            {
                item.OutputPath = new OsPath(item.Files.GetCommonBaseDirectory());
            }

            return _queue;
        }

        public void RemoveFromQueue(string downloadId)
        {
            _client.EmitAsync("removeFromQueue", downloadId).GetAwaiter().GetResult();
        }

        public string Download(string url, int bitrate)
        {
            _logger.Trace($"Downloading {url} bitrate {bitrate}");

            using (var pending = new DeemixPendingItem<string>())
            {
                var ack = Interlocked.Increment(ref AddId);
                _pendingAdds[ack] = pending;

                _client.EmitAsync("addToQueue",
                                  new
                                  {
                                      url,
                                      bitrate,
                                      ack
                                  })
                    .GetAwaiter().GetResult();

                _logger.Trace($"Awaiting result for add {ack}");
                var added = pending.Wait(60000);

                _pendingAdds.Remove(ack);

                if (!added)
                {
                    throw new DownloadClientUnavailableException("Could not add item");
                }

                return pending.Item;
            }
        }

        public DeemixSearchResponse Search(string term)
        {
            _logger.Trace($"Searching for {term}");

            using (var pending = new DeemixPendingItem<DeemixSearchResponse>())
            {
                var ack = Interlocked.Increment(ref AddId);
                _pendingSearches[ack] = pending;

                _client.EmitAsync("albumSearch",
                                  new
                                  {
                                      term,
                                      start = 0,
                                      nb = 100,
                                      ack
                                  })
                    .GetAwaiter().GetResult();

                _logger.Trace($"Awaiting result for search {ack}");
                var gotResult = pending.Wait(60000);

                _pendingSearches.Remove(ack);

                if (!gotResult)
                {
                    throw new DownloadClientUnavailableException("Could not search for {0}", term);
                }

                return pending.Item;
            }
        }

        public DeemixSearchResponse RecentReleases()
        {
            _logger.Trace("Getting recent releases");

            using (var pending = new DeemixPendingItem<DeemixSearchResponse>())
            {
                var ack = Interlocked.Increment(ref AddId);
                _pendingSearches[ack] = pending;

                _client.EmitAsync("newReleases",
                                  new
                                  {
                                      ack
                                  })
                    .GetAwaiter().GetResult();

                _logger.Trace($"Awaiting result for RSS {ack}");
                var gotResult = pending.Wait(60000);

                _pendingSearches.Remove(ack);

                if (!gotResult)
                {
                    throw new DownloadClientUnavailableException("Could not get recent releases");
                }

                return pending.Item;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed state (managed objects).
                _configReceived.Dispose();
            }

            _disposed = true;
        }

        private void Disconnect()
        {
            if (_client != null)
            {
                _logger.Trace("Disconnecting");
                _client.DisconnectAsync().GetAwaiter().GetResult();
            }
        }

        private void Connect(string url)
        {
            _configReceived.Reset();
            _logger.Trace("reset received event");
            _queue = new List<DeemixClientItem>();

            _client = new SocketIO(url);

            _client.On("init_settings", OnInitSettings);
            _client.On("updateSettings", OnUpdateSettings);
            _client.On("init_downloadQueue", OnInitQueue);
            _client.On("addedToQueue", OnAddedToQueue);
            _client.On("updateQueue", OnUpdateQueue);
            _client.On("finishDownload", response => UpdateDownloadStatus(response, DownloadItemStatus.Completed));
            _client.On("startDownload", response => UpdateDownloadStatus(response, DownloadItemStatus.Downloading));
            _client.On("removedFromQueue", OnRemovedFromQueue);
            _client.On("removedAllDownloads", OnRemovedAllFromQueue);
            _client.On("removedFinishedDownloads", OnRemovedFinishedFromQueue);
            _client.On("loginNeededToDownload", OnLoginRequired);
            _client.On("queueError", OnQueueError);
            _client.On("albumSearch", OnSearchResult);
            _client.On("newReleases", OnSearchResult);

            _logger.Trace("Connecting to deemix");
            _client.ConnectAsync().GetAwaiter().GetResult();

            _logger.Trace("waiting for config");
            var gotConfig = _configReceived.Wait(60000);
            _logger.Trace($"got config event {gotConfig}");

            if (!gotConfig)
            {
                throw new DownloadClientUnavailableException("Unable to connect to Deemix");
            }
        }

        private void OnInitSettings(SocketIOResponse response)
        {
            _logger.Trace("got settings");
            _config = response.GetValue<DeemixConfig>();
            _logger.Trace($"Download path: {_config.DownloadLocation}");
            _configReceived.Set();
        }

        private void OnUpdateSettings(SocketIOResponse response)
        {
            _logger.Trace("got settings update");
            _config = response.GetValue<DeemixConfig>(0);
        }

        private void OnInitQueue(SocketIOResponse response)
        {
            _logger.Trace("got queue");
            var dq = response.GetValue<DeemixQueue>();

            var items = dq.QueueList.Values.ToList();
            _logger.Trace($"sent {items.Count} items");

            _queue = items.Select(x => ToDownloadClientItem(x)).ToList();
            _logger.Trace($"queue length {_queue.Count}");
            _logger.Trace(_queue.ConcatToString(x => x.Title, "\n"));
        }

        private void OnUpdateQueue(SocketIOResponse response)
        {
            _logger.Trace("Got update queue");
            var item = response.GetValue<DeemixQueueUpdate>();

            var queueItem = _queue.Single(x => x.DownloadId == item.Uuid);

            if (item.Progress.HasValue)
            {
                var progress = (double)item.Progress.Value / 100;
                queueItem.RemainingSize = (long)((1 - progress) * queueItem.TotalSize);

                var elapsed = DateTime.UtcNow - queueItem.Started;
                queueItem.RemainingTime = TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - progress) / progress));
            }

            if (item.DownloadPath != null)
            {
                queueItem.Files.Add(item.DownloadPath);
            }
        }

        private void UpdateDownloadStatus(SocketIOResponse response, DownloadItemStatus status)
        {
            _logger.Trace($"Got update download status {status}");
            var uuid = response.GetValue<string>();

            var queueItem = _queue.Single(x => x.DownloadId == uuid);
            queueItem.Status = status;
        }

        private void OnRemovedFromQueue(SocketIOResponse response)
        {
            _logger.Trace("Got removed from queue");
            var uuid = response.GetValue<string>();

            var queueItem = _queue.Single(x => x.DownloadId == uuid);
            _queue.Remove(queueItem);
        }

        private void OnRemovedAllFromQueue(SocketIOResponse response)
        {
            _logger.Trace("Got removed all from queue");
            _queue = new List<DeemixClientItem>();
        }

        private void OnRemovedFinishedFromQueue(SocketIOResponse response)
        {
            _logger.Trace("Got removed all from queue");
            _queue = _queue.Where(x => x.Status != DownloadItemStatus.Completed).ToList();
        }

        private void OnAddedToQueue(SocketIOResponse response)
        {
            _logger.Trace("Got added to queue");
            var item = response.GetValue<DeemixQueueItem>();
            _logger.Trace($"got ack {item.Ack}");

            if (item.Ack.HasValue && _pendingAdds.TryGetValue(item.Ack.Value, out var pending))
            {
                pending.Item = item.Uuid;
                pending.Ack();
            }

            var dci = ToDownloadClientItem(item);
            dci.Started = DateTime.UtcNow;
            _queue.Add(dci);
        }

        private void OnQueueError(SocketIOResponse response)
        {
            var error = response.GetValue().ToJson();
            _logger.Error($"Queue error:\n {error}");
        }

        private void OnSearchResult(SocketIOResponse response)
        {
            _logger.Trace("Got search response");
            var result = response.GetValue<DeemixSearchResponse>();
            _logger.Trace($"got ack {result.Ack}");

            if (result.Ack.HasValue && _pendingSearches.TryGetValue(result.Ack.Value, out var pending))
            {
                pending.Item = result;
                pending.Ack();
            }
        }

        private void OnLoginRequired(SocketIOResponse response)
        {
            throw new DownloadClientUnavailableException("login required");
        }

        private static DeemixClientItem ToDownloadClientItem(DeemixQueueItem x)
        {
            return new DeemixClientItem
            {
                DownloadId = x.Uuid,
                Title = $"{x.Artist} - {x.Title} [WEB] {GetFormat(x)}",
                TotalSize = x.Size * 100,
                RemainingSize = (100 - x.Progress) * x.Size,
                Files = x.Files,
                Status = GetItemStatus(x),
                CanMoveFiles = true,
                CanBeRemoved = true
            };
        }

        private static string GetFormat(DeemixQueueItem item)
        {
            switch (item.Bitrate)
            {
                case "9":
                    return "[FLAC]";
                case "3":
                    return "[MP3 320]";
                case "1":
                    return "[MP3 128]";
                default:
                    return "";
            }
        }

        private static DownloadItemStatus GetItemStatus(DeemixQueueItem item)
        {
            if (item.Failed)
            {
                return DownloadItemStatus.Failed;
            }

            if (item.Progress == 0)
            {
                return DownloadItemStatus.Queued;
            }

            if (item.Progress < 100)
            {
                return DownloadItemStatus.Downloading;
            }

            return DownloadItemStatus.Completed;
        }
    }
}
