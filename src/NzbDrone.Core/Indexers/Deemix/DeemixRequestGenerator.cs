using System.Collections.Generic;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers.Deemix
{
    public class DeemixRequestGenerator : IIndexerRequestGenerator
    {
        public DeemixIndexerSettings Settings { get; set; }
        public Logger Logger { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(new[]
            {
                new DeemixRequest(
                    Settings.BaseUrl,
                    x => x.RecentReleases())
            });

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequest(string.Format("artist:\"{0}\" album:\"{1}\"", searchCriteria.ArtistQuery, searchCriteria.AlbumQuery)));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequest(string.Format("artist:\"{0}\"", searchCriteria.ArtistQuery)));
            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetRequest(string searchParameters)
        {
            var request =
                new DeemixRequest(
                    Settings.BaseUrl,
                    x => x.Search(searchParameters));

            yield return request;
        }
    }
}
