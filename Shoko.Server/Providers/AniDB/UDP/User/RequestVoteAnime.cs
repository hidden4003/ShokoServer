using System;
using Microsoft.Extensions.Logging;
using Shoko.Server.Providers.AniDB.Interfaces;
using Shoko.Server.Providers.AniDB.UDP.Exceptions;
using Shoko.Server.Providers.AniDB.UDP.Generic;

namespace Shoko.Server.Providers.AniDB.UDP.User;

/// <summary>
/// Vote for an anime
/// </summary>
public class RequestVoteAnime : UDPRequest<ResponseVote>
{
    /// <summary>
    /// AnimeID to vote on
    /// </summary>
    public int AnimeID { get; set; }

    /// <summary>
    /// Between 0 exclusive and 10 inclusive, will be rounded to nearest tenth
    /// </summary>
    public double Value { get; set; }

    private int AniDBValue => Value < 0 ? -1 : (int)(Math.Round(Value, 1, MidpointRounding.AwayFromZero) * 100D);

    /// <summary>
    /// If the anime is not finished (or you haven't finished it), then it is Temporary
    /// </summary>
    public bool Temporary { get; set; }

    protected override string BaseCommand => $"VOTE type={(Temporary ? 2 : 1)}&id={AnimeID}&value={AniDBValue}";

    protected override UDPResponse<ResponseVote> ParseResponse(UDPResponse<string> response)
    {
        var code = response.Code;
        var receivedData = response.Response;
        var parts = receivedData.Split('|');
        if (parts.Length != 4)
        {
            throw new UnexpectedUDPResponseException("Incorrect Number of Parts Returned", code, receivedData, Command);
        }

        if (!int.TryParse(parts[1], out var value))
        {
            throw new UnexpectedUDPResponseException("Value should be an int, but it's not", code, receivedData, Command);
        }

        if (!int.TryParse(parts[2], out var type))
        {
            throw new UnexpectedUDPResponseException("Vote type should be an int, but it's not", code, receivedData, Command);
        }

        if (!int.TryParse(parts[3], out var id))
        {
            throw new UnexpectedUDPResponseException("ID should be an int, but it's not", code, receivedData, Command);
        }

        return new UDPResponse<ResponseVote>
        {
            Code = code,
            Response = new ResponseVote
            {
                EntityName = parts[0],
                Value = value,
                Type = (VoteType)type,
                EntityID = id,
            },
        };
    }

    public RequestVoteAnime(ILoggerFactory loggerFactory, IUDPConnectionHandler handler) : base(loggerFactory, handler)
    {
    }
}
