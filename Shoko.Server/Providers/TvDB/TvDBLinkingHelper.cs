﻿using System;
using System.Collections.Generic;
using System.Linq;
using Shoko.Commons.Extensions;
using Shoko.Commons.Utils;
using Shoko.Models.Azure;
using Shoko.Models.Enums;
using Shoko.Models.Server;
using Shoko.Server.Extensions;
using Shoko.Server.Models;
using Shoko.Server.Repositories;

namespace Shoko.Server
{
    public static class TvDBLinkingHelper
    {
        #region TvDB Matching

        public static void GenerateTvDBEpisodeMatches(int animeID)
        {
            var matches = GetTvDBEpisodeMatches(animeID);

            // wipe old links except User Verified
            RepoFactory.CrossRef_AniDB_TvDB_Episode.DeleteAllUnverifiedLinksForAnime(animeID);

            List<CrossRef_AniDB_TvDB_Episode> tosave = new List<CrossRef_AniDB_TvDB_Episode>();
            foreach (var match in matches)
            {
                if (match.AniDB == null || match.TvDB == null) continue;
                var xref = RepoFactory.CrossRef_AniDB_TvDB_Episode.GetByAniDBAndTvDBEpisodeIDs(match.AniDB.EpisodeID,
                    match.TvDB.Id);
                // Don't touch User Verified links
                if (xref?.MatchRating == MatchRating.UserVerified) continue;

                // The only existing links will be User Verified, so no need to check for duplicates
                xref = new CrossRef_AniDB_TvDB_Episode
                {
                    AniDBEpisodeID = match.AniDB.EpisodeID,
                    TvDBEpisodeID = match.TvDB.Id,
                    MatchRating = match.Rating
                };

                tosave.Add(xref);
            }

            if (tosave.Count == 0) return;

            tosave.Batch(50).ForEach(RepoFactory.CrossRef_AniDB_TvDB_Episode.Save);
        }

        public static List<(AniDB_Episode AniDB, TvDB_Episode TvDB, MatchRating Rating)> GetTvDBEpisodeMatches(
            int animeID)
        {
            /*   These all apply to normal episodes mainly.
             *   It will fail for specials (BD will cause most to have the same air date).
             *
             *   We will keep a running score to determine how accurate we believe the link is. Lower is better
             *   Ideal score is 1 to 1 match with perfect air dates
             *
             *   if the episodes are 1-1:
             *     Try to match air date.
             *     if no match:
             *       match to the next episode after the previous match (starting at one)
             *     if two episodes air on the same day:
             *       match in order of episode, grouped by air date
             *
             *   if the episodes are not 1-1:
             *      try to match air date.
             *      group episodes in order by air date
             *
             *      if two episodes air on the same day on both sides:
             *        split them as equally as possible, these will likely need manually overriden
             *      if no match:
             *        if all episodes belong to the same season:
             *          split episodes equally and increment from previous match. these will almost definitely need overriden
             *        else:
             *          increment and hope for the best...
             */

            // Get All AniDB and TvDB episodes for a series, normal and specials done separately
            // Due to fun season splitting on TvDB,
            // we need extra logic to determine if a series is one or more seasons

            // Get TvDB first, if we can't get the episodes, then there's no valid link
            List<CrossRef_AniDB_TvDB> tvxrefs = RepoFactory.CrossRef_AniDB_TvDB.GetByAnimeID(animeID);
            int tvdbID = tvxrefs.FirstOrDefault()?.TvDBID ?? 0;
            if (tvdbID == 0) return new List<(AniDB_Episode AniDB, TvDB_Episode TvDB, MatchRating Rating)>();

            List<TvDB_Episode> tveps = RepoFactory.TvDB_Episode.GetBySeriesID(tvdbID);
            List<TvDB_Episode> tvepsNormal = tveps.Where(a => a.SeasonNumber != 0).OrderBy(a => a.SeasonNumber)
                .ThenBy(a => a.EpisodeNumber).ToList();

            // Get AniDB
            List<AniDB_Episode> anieps = RepoFactory.AniDB_Episode.GetByAnimeID(animeID);
            List<AniDB_Episode> aniepsNormal = anieps.Where(a => a.EpisodeType == (int) EpisodeType.Episode)
                .OrderBy(a => a.EpisodeNumber).ToList();

            SVR_AniDB_Anime anime = RepoFactory.AniDB_Anime.GetByAnimeID(animeID);

            List<(AniDB_Episode, TvDB_Episode, MatchRating)> matches =
                new List<(AniDB_Episode, TvDB_Episode, MatchRating)>();

            // Only try to match normal epiosdes if this is a series
            if (anime?.AnimeType != (int) AnimeType.Movie && aniepsNormal.Count > 0 && tvepsNormal.Count > 0)
                TryToMatchNormalEpisodesToTvDB(aniepsNormal, tvepsNormal, anime?.EndDate == null, ref matches);

            // Specials. We aren't going to try too hard here.
            // We'll try by titles and dates, but we'll rely mostly on overrides
            List<AniDB_Episode> aniepsSpecial = anieps.Where(a => a.EpisodeType == (int) EpisodeType.Special)
                .OrderBy(a => a.EpisodeNumber).ToList();

            List<TvDB_Episode> tvepsSpecial =
                tveps.Where(a => a.SeasonNumber == 0).OrderBy(a => a.EpisodeNumber).ToList();

            if (aniepsSpecial.Count > 0 && tvepsSpecial.Count > 0)
                TryToMatchSpeicalsToTvDB(aniepsSpecial, tvepsSpecial, ref matches);

            return matches;
        }

        private static void TryToMatchNormalEpisodesToTvDB(List<AniDB_Episode> aniepsNormal,
            List<TvDB_Episode> tvepsNormal, bool isAiring, ref List<(AniDB_Episode, TvDB_Episode, MatchRating)> matches)
        {
            // determine 1-1
            bool one2one = aniepsNormal.Count == tvepsNormal.Count;

            List<IGrouping<int, TvDB_Episode>> seasonLookup =
                tvepsNormal.GroupBy(a => a.SeasonNumber).OrderBy(a => a.Key).ToList();

            // Exclude shows with numbered titles from title matching.
            // Those are always in order, so go by dates and fill in the rest
            bool hasNumberedTitles = HasNumberedTitles(aniepsNormal) || HasNumberedTitles(tvepsNormal);

            if (!one2one && seasonLookup.Count > 0)
            {
                // we'll need to split seasons and see if the series spans multiple or matches a specific season
                List<TvDB_Episode> temp = new List<TvDB_Episode>();
                TryToMatchSeasonsByAirDates(aniepsNormal, seasonLookup, isAiring, ref temp);
                if (temp.Count > 0) tvepsNormal = temp;

                one2one = aniepsNormal.Count == tvepsNormal.Count;
                // airing shows won't match
                if (!one2one && isAiring)
                {
                    // Saiki K => regular matching detection (5 to 1)
                    // We'll group by week, and we'll cheat by using ISO6801 calendar,
                    // as it ensures that the week is not split on the end of the year
                    var airdategroupings = aniepsNormal.GroupBy(a =>
                            a.GetAirDateAsDate()?.ToIso8601Weeknumber() ?? DateTime.Today.ToIso8601Weeknumber())
                        .Select(a => a.Count()).ToList();
                    double average = airdategroupings.Average();
                    int firstgroupingcount = airdategroupings.First();
                    double epsilon = firstgroupingcount / (aniepsNormal.Count - 1.5D);
                    bool isregular = Math.Sqrt((firstgroupingcount - average) * (firstgroupingcount - average)) <=
                                     epsilon;
                    bool weekly1to1 = isregular && firstgroupingcount == 1;
                    if (isregular && !weekly1to1)
                    {
                        // it's regularly split, so we'll treat it as {firstgroupingcount} to one
                        // TODO find out how the TvDB episodes might line up.
                        // In this case, Saiki K was 5 to 1, and we can match by air week ISO6801
                        one2one = false;
                        // skip the next step, since we are pretty confident in the season matching here
                        goto matchepisodes;
                    }

                    // no need for else, the goto skips ahead
                    if(weekly1to1)
                    {
                        // TODO we may need to check the TvDB side for splitting episodes, but they don't do it often
                        one2one = true;
                    }
                }

                if (!one2one && !hasNumberedTitles)
                {
                    temp.Clear();

                    TryToMatchSeasonsByEpisodeTitles(aniepsNormal, seasonLookup, ref temp);
                    if (temp.Count > 0) tvepsNormal = temp;

                    one2one = aniepsNormal.Count == tvepsNormal.Count;
                }
            }

            matchepisodes:

            // It's one to one, possibly spanning multiple seasons
            if (one2one)
            {
                TryToMatchEpisodes1To1ByAirDate(ref aniepsNormal, ref tvepsNormal, ref matches);

                if (!hasNumberedTitles)
                    TryToMatchEpisodes1To1ByTitle(ref aniepsNormal, ref tvepsNormal, ref matches);

                FillUnmatchedEpisodes1To1(ref aniepsNormal, ref tvepsNormal, ref matches);
            }
            else
            {
                // Not 1 to 1, this can be messy
            }
        }

        private static void TryToMatchSpeicalsToTvDB(List<AniDB_Episode> aniepsSpecial, List<TvDB_Episode> tvepsSpecial,
            ref List<(AniDB_Episode, TvDB_Episode, MatchRating)> matches)
        {
            // Specials are almost never going to be one to one. We'll assume they are and let the user fix them
            // Air Dates are less accurate for specials (BD/DVD release makes them all the same). Try Titles first
            TryToMatchEpisodes1To1ByTitle(ref aniepsSpecial, ref tvepsSpecial, ref matches);
            TryToMatchEpisodes1To1ByAirDate(ref aniepsSpecial, ref tvepsSpecial, ref matches);
            FillUnmatchedEpisodes1To1(ref aniepsSpecial, ref tvepsSpecial, ref matches);
        }

        private static readonly char[] separators = " /.,<>?;':\"\\!@#$%^&*()-=_+|`~".ToCharArray();

        public static bool IsTitleNumberedAndConsecutive(string title1, string title2)
        {
            // Return if it's Episode {number} ex Nodame Cantibile's "Lesson 1" or Air Gear's "Trick 1"
            // This will fail if you use not English TvDB or the title is "First Epiosde"

            if (string.IsNullOrEmpty(title1) || string.IsNullOrEmpty(title2)) return false;

            string[] parts1 = title1.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts1.Length == 0) return false;
            string end1 = parts1[parts1.Length - 1];
            if (!double.TryParse(end1, out double endNumber1)) return false;

            string[] parts2 = title2.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts2.Length == 0) return false;
            string end2 = parts2[parts2.Length - 1];
            if (!double.TryParse(end2, out double endNumber2)) return false;

            // There are cases with .5 episodes, so count it as consecutive if there is no more than 1 distance
            // We only care about the range surrounding 1, so it's fine to leave it squared
            double distSq = (endNumber2 - endNumber1) * (endNumber2 - endNumber1);
            // Double precision fun
            return distSq < 1.0001D;
        }

        public static bool HasNumberedTitles(List<AniDB_Episode> eps)
        {
            return eps.Zip(eps.Skip(1), Tuple.Create).All(a =>
                IsTitleNumberedAndConsecutive(a.Item1.GetEnglishTitle(), a.Item2.GetEnglishTitle()));
        }

        public static bool HasNumberedTitles(List<TvDB_Episode> eps)
        {
            return eps.Zip(eps.Skip(1), Tuple.Create).All(a =>
                IsTitleNumberedAndConsecutive(a.Item1.EpisodeName, a.Item2.EpisodeName));
        }

        private static void TryToMatchSeasonsByAirDates(List<AniDB_Episode> aniepsNormal,
            List<IGrouping<int, TvDB_Episode>> seasonLookup, bool isAiring, ref List<TvDB_Episode> temp)
        {
            // Compare the start and end of the series to each season
            // This should be almost always accurate
            // Pre-screenings of several months will break it

            if (seasonLookup.Count == 0) return;

            DateTime start = aniepsNormal.Min(a => a.GetAirDateAsDate() ?? DateTime.MaxValue);
            if (start == DateTime.MaxValue) return;
            start = start.AddDays(-7);

            DateTime endTvDB = seasonLookup.Max(b =>
                b.Where(c => c.AirDate != null).Select(c => c.AirDate.Value).OrderBy(a => a).LastOrDefault());
            if (endTvDB == default) return;

            // luckily AniDB always has more Air Date info than TvDB
            DateTime end = aniepsNormal.Max(a =>
                a.GetAirDateAsDate() ?? endTvDB);
            if (isAiring) end = endTvDB;

            end = end.AddDays(7);
            foreach (var season in seasonLookup)
            {
                var epsInSeason = season.OrderBy(a => a.EpisodeNumber).ToList();
                DateTime? seasonStart = epsInSeason.FirstOrDefault()?.AirDate;
                if (seasonStart == null) continue;
                DateTime? seasonEnd = epsInSeason.LastOrDefault(a => a.AirDate != null)?.AirDate;
                // no need to check seasonEnd, worst case, it's equal to seasonStart
                // It is extremely unlikely that a TvDB season begins before a series, while including it
                if (seasonStart < start)
                {
                    continue;
                }

                // It started within the AniDB series

                if (seasonEnd > end)
                {
                    continue;
                }
                temp.AddRange(epsInSeason);
            }
        }

        private static void TryToMatchSeasonsByEpisodeTitles(List<AniDB_Episode> aniepsNormal,
            List<IGrouping<int, TvDB_Episode>> seasonLookup, ref List<TvDB_Episode> temp)
        {
            // Will try to compare the Titles for the first and last episodes of the series
            // This is very inacurrate, but may fix the situations with pre-screenings

            // first ep
            var aniepstart = aniepsNormal.FirstOrDefault();
            string anistart = aniepstart?.GetEnglishTitle();
            if (string.IsNullOrEmpty(anistart)) return;

            // last ep
            var aniepend = aniepsNormal.FirstOrDefault();
            string aniend = aniepend?.GetEnglishTitle();
            if (string.IsNullOrEmpty(aniend)) return;

            foreach (var season in seasonLookup)
            {
                var epsInSeason = season.OrderBy(a => a.EpisodeNumber).ToList();

                string tvstart = epsInSeason.FirstOrDefault()?.EpisodeName;
                if (string.IsNullOrEmpty(tvstart)) continue;
                // fuzzy match
                if (anistart.FuzzyMatches(tvstart))
                {
                    temp.AddRange(epsInSeason);
                    continue;
                }

                string tvend = epsInSeason.LastOrDefault()?.EpisodeName;
                if (string.IsNullOrEmpty(tvend)) continue;
                // fuzzy match
                if (aniend.FuzzyMatches(tvend))
                {
                    temp.AddRange(epsInSeason);
                }
            }
        }

        private static void TryToMatchEpisodes1To1ByAirDate(ref List<AniDB_Episode> aniepsNormal,
            ref List<TvDB_Episode> tvepsNormal,
            ref List<(AniDB_Episode, TvDB_Episode, MatchRating)> matches)
        {
            foreach (var aniep in aniepsNormal.ToList())
            {
                DateTime? aniair = aniep.GetAirDateAsDate();
                if (aniair == null) continue;
                foreach (var tvep in tvepsNormal)
                {
                    DateTime? tvair = tvep.AirDate;
                    if (tvair == null) continue;

                    // check if the dates are within reason
                    if (!aniair.Value.IsWithinErrorMargin(tvair.Value, TimeSpan.FromDays(1.5))) continue;
                    // Add them to the matches and remove them from the lists to process
                    matches.Add((aniep, tvep, MatchRating.Good));
                    tvepsNormal.Remove(tvep);
                    aniepsNormal.Remove(aniep);
                    break;
                }
            }
        }

        private static void TryToMatchEpisodes1To1ByTitle(ref List<AniDB_Episode> aniepsNormal,
            ref List<TvDB_Episode> tvepsNormal,
            ref List<(AniDB_Episode, TvDB_Episode, MatchRating)> matches)
        {
            foreach (var aniep in aniepsNormal.ToList())
            {
                string anititle = aniep.GetEnglishTitle();
                if (string.IsNullOrEmpty(anititle)) continue;

                foreach (var tvep in tvepsNormal)
                {
                    string tvtitle = tvep.EpisodeName;
                    if (string.IsNullOrEmpty(tvtitle)) continue;

                    // fuzzy match
                    if (!anititle.FuzzyMatches(tvtitle)) continue;
                    // Add them to the matches and remove them from the lists to process
                    matches.Add((aniep, tvep, MatchRating.Bad));
                    tvepsNormal.Remove(tvep);
                    aniepsNormal.Remove(aniep);
                    break;
                }
            }
        }

        private static void FillUnmatchedEpisodes1To1(ref List<AniDB_Episode> aniepsNormal,
            ref List<TvDB_Episode> tvepsNormal,
            ref List<(AniDB_Episode AniDB, TvDB_Episode TvDB, MatchRating Match)> matches)
        {
            if (aniepsNormal.Count == 0) return;
            // Find the missing episodes, and if there is a remaining episode to fill it with, then do it

            // special handling for if the first episodes are missing.
            // This will happen often since many shows are pre-screened
            // Find the first linked episode, and work backwards

            // Aggregate throws on an empty list.... Why doesn't it just return default like everything else...
            if (matches.Count > 0)
            {
                if (aniepsNormal.Min(a => a.EpisodeNumber == 1))
                {
                    var minaniep = matches.Aggregate((a, b) => a.AniDB.EpisodeNumber < b.AniDB.EpisodeNumber ? a : b);
                    var mintvep = minaniep.TvDB;
                    foreach (var aniep in aniepsNormal.Where(a => a.EpisodeNumber < minaniep.AniDB.EpisodeNumber)
                        .OrderByDescending(a => a.EpisodeNumber).ToList())
                    {
                        (int season, int epnumber) = mintvep.GetPreviousEpisode();
                        var tvep = tvepsNormal.FirstOrDefault(a =>
                            a.SeasonNumber == season && a.EpisodeNumber == epnumber);
                        // Give up if it's not found
                        if (tvep == null) break;

                        matches.Add((aniep, tvep, MatchRating.Bad));
                        aniepsNormal.Remove(aniep);
                        tvepsNormal.Remove(tvep);
                    }
                }

                foreach (var aniDbEpisode in aniepsNormal.OrderBy(a => a.EpisodeNumber).ToList())
                {
                    int aniEpNumber = aniDbEpisode.EpisodeNumber;

                    // Find the episode that was the last linked episode before this number
                    var previouseps = matches.Where(a => a.AniDB.EpisodeNumber < aniEpNumber).ToList();
                    if (previouseps.Count == 0) break;
                    var previousep =
                        previouseps.Aggregate((a, b) => a.AniDB.EpisodeNumber > b.AniDB.EpisodeNumber ? a : b);
                    // Now we need to figure out what the next episode is
                    (int nextSeason, int nextEpisode) = previousep.TvDB.GetNextEpisode();
                    if (nextSeason == 0 || nextEpisode == 0) continue;
                    var nextEp =
                        tvepsNormal.FirstOrDefault(a => a.SeasonNumber == nextSeason && a.EpisodeNumber == nextEpisode);
                    if (nextEp == null) continue;

                    // add the mapping and remove it from the possible listings
                    matches.Add((aniDbEpisode, nextEp, MatchRating.Ugly));
                    aniepsNormal.Remove(aniDbEpisode);
                    tvepsNormal.Remove(nextEp);
                }
            }

            // just map whatever is left to something.... It's almost certainly wrong
            foreach (var aniep in aniepsNormal.ToList())
            {
                var tvep = tvepsNormal.FirstOrDefault();
                if (tvep == null) break;
                matches.Add((aniep, tvep, MatchRating.SarahJessicaParker));
                aniepsNormal.Remove(aniep);
                tvepsNormal.Remove(tvep);
            }
        }

        public static List<CrossRef_AniDB_TvDB_Episode_Override> GetSpecialsOverridesFromLegacy(
            List<Azure_CrossRef_AniDB_TvDB> links)
        {
            var list = links.Select(a => (a.AnimeID, a.AniDBStartEpisodeType, a.AniDBStartEpisodeNumber, a.TvDBID,
                a.TvDBSeasonNumber, a.TvDBStartEpisodeNumber)).ToList();
            return GetSpecialsOverridesFromLegacy(list);
        }

        public static List<CrossRef_AniDB_TvDB_Episode_Override> GetSpecialsOverridesFromLegacy(
            List<CrossRef_AniDB_TvDBV2> links)
        {
            var list = links.Select(a => (a.AnimeID, a.AniDBStartEpisodeType, a.AniDBStartEpisodeNumber, a.TvDBID,
                a.TvDBSeasonNumber, a.TvDBStartEpisodeNumber)).ToList();
            return GetSpecialsOverridesFromLegacy(list);
        }

        private static List<CrossRef_AniDB_TvDB_Episode_Override> GetSpecialsOverridesFromLegacy(
            List<(int AnimeID, int AniDBStartType, int AniDBStartNumber, int TvDBID, int TvDBSeason, int TvDBStartNumber
                )> links)
        {
            if (links.Count == 0) return new List<CrossRef_AniDB_TvDB_Episode_Override>();
            // First, sort by AniDB type and number
            var xrefs = links.OrderBy(a => a.AniDBStartType).ThenBy(a => a.AniDBStartNumber).ToList();
            int AnimeID = xrefs.FirstOrDefault().AnimeID;
            var anime = RepoFactory.AniDB_Anime.GetByAnimeID(AnimeID);
            if (anime == null) return new List<CrossRef_AniDB_TvDB_Episode_Override>();
            var episodes = RepoFactory.AniDB_Episode.GetByAnimeID(AnimeID)
                .Where(a => a.EpisodeType == (int) EpisodeType.Special).OrderBy(a => a.EpisodeNumber).ToList();

            List<CrossRef_AniDB_TvDB_Episode_Override> output = new List<CrossRef_AniDB_TvDB_Episode_Override>();

            for (int i = 0; i < episodes.Count; i++)
            {
                var episode = episodes[i];
                var xref = GetXRefForEpisode(episode.EpisodeType, episode.EpisodeNumber, xrefs);
                if (xref.AniDBStartType == 0) continue;

                // Get TvDB ep
                var tvep = RepoFactory.TvDB_Episode.GetBySeriesIDSeasonNumberAndEpisode(xref.TvDBID, xref.TvDBSeason,
                    xref.TvDBStartNumber);
                if (tvep == null) continue;

                int delta = episode.EpisodeNumber - xref.AniDBStartNumber;

                if (delta > 0)
                {
                    for (int j = 0; j < delta; j++)
                    {
                        var nextep = tvep.GetNextEpisode();
                        // continue outer loop
                        if (nextep.episodeNumber == 0) goto label0;
                        tvep = RepoFactory.TvDB_Episode.GetBySeriesIDSeasonNumberAndEpisode(xref.TvDBID, nextep.season,
                            nextep.episodeNumber);
                    }
                }

                var newxref = new CrossRef_AniDB_TvDB_Episode_Override
                {
                    AniDBEpisodeID = episode.EpisodeID,
                    TvDBEpisodeID = tvep.Id
                };
                output.Add(newxref);

                label0:;
            }

            return output;
        }

        private static (int AnimeID, int AniDBStartType, int AniDBStartNumber, int TvDBID, int TvDBSeason, int
            TvDBStartNumber) GetXRefForEpisode(int type, int number,
                List<(int AnimeID, int AniDBStartType, int AniDBStartNumber, int TvDBID, int TvDBSeason, int
                    TvDBStartNumber)> xrefs)
        {
            for (int i = 0; i < xrefs.Count; i++)
            {
                var xref = xrefs[i];
                if (i + 1 == xrefs.Count) return xref;
                var next = xrefs[i + 1];
                if (next.AniDBStartType != type) continue;
                if (xref.AniDBStartNumber <= number && next.AniDBStartNumber > number) return xref;
            }

            return default;
        }

        #endregion

        private static int ToIso8601Weeknumber(this DateTime date)
        {
            var thursday = date.AddDays(3 - date.DayOfWeek.DayOffset());
            return (thursday.DayOfYear - 1) / 7 + 1;
        }

        private static int DayOffset(this DayOfWeek weekDay)
        {
            return ((int)weekDay + 6) % 7;
        }
    }
}
