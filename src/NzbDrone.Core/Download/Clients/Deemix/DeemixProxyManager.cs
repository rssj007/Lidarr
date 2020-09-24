using System.Collections.Concurrent;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Download.Clients.Deemix
{
    public interface IDeemixProxyManager
    {
        DeemixProxy GetProxy(DeemixSettings settings);
        DeemixProxy GetProxy(string url);
    }

    public class DeemixProxyManager : IDeemixProxyManager
    {
        private readonly Logger _logger;

        private readonly ConcurrentDictionary<string, DeemixProxy> _store;

        public DeemixProxyManager(Logger logger)
        {
            _logger = logger;

            _store = new ConcurrentDictionary<string, DeemixProxy>();
        }

        public DeemixProxy GetProxy(DeemixSettings settings)
        {
            return GetProxy(GetUrl(settings));
        }

        public DeemixProxy GetProxy(string url)
        {
            return _store.GetOrAdd(url, (x) => new DeemixProxy(x, _logger));
        }

        private static string GetUrl(DeemixSettings settings)
        {
            return HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase);
        }
    }
}
