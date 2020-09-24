using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Download.Clients.Deemix;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Deemix
{
    public static class DeemixParser
    {
        private static readonly int[] _bitrates = new[] { 1, 3, 9 };

        public static IList<ReleaseInfo> ParseResponse(DeemixSearchResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();

            if (response?.Data == null ||
                response.Total == 0)
            {
                return torrentInfos;
            }

            foreach (var result in response.Data)
            {
                foreach (var bitrate in _bitrates)
                {
                    torrentInfos.Add(ToReleaseInfo(result, bitrate));
                }
            }

            // order by date
            return
                torrentInfos
                    .OrderByDescending(o => o.Size)
                    .ToArray();
        }

        private static ReleaseInfo ToReleaseInfo(DeemixGwAlbum x, int bitrate)
        {
            var result = new ReleaseInfo
            {
                Guid = $"Deemix-{x.AlbumId}-{bitrate}",
                Artist = x.ArtistName,
                Album = x.AlbumTitle,
                DownloadUrl = x.Link,
                InfoUrl = x.Link,
                PublishDate = x.DigitalReleaseDate ?? x.PhysicalReleaseDate ?? DateTime.UtcNow,
                DownloadProtocol = DownloadProtocol.Deemix
            };

            long actualBitrate;
            string format;
            switch (bitrate)
            {
                case 9:
                    actualBitrate = 1411;
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC";
                    break;
                case 3:
                    actualBitrate = 320;
                    result.Codec = "MP3";
                    result.Container = "320";
                    format = "MP3 320";
                    break;
                case 1:
                    actualBitrate = 128;
                    result.Codec = "MP3";
                    result.Container = "128";
                    format = "MP3 128";
                    break;
                default:
                    throw new NotImplementedException();
            }

            // bitrate is in kbit/sec, 128 = 1024/8
            result.Size = x.DurationInSeconds * actualBitrate * 128L;
            result.Title = $"{x.ArtistName} - {x.AlbumTitle} [WEB] [{format}]";

            if (x.Explicit)
            {
                result.Title += " [Explicit]";
            }

            return result;
        }
    }
}
