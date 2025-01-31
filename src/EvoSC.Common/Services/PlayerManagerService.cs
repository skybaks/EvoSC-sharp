﻿using EvoSC.Common.Interfaces;
using EvoSC.Common.Interfaces.Database.Repository;
using EvoSC.Common.Interfaces.Models;
using EvoSC.Common.Interfaces.Services;
using EvoSC.Common.Util;
using EvoSC.Common.Util.Algorithms;
using GbxRemoteNet.Structs;
using Microsoft.Extensions.Logging;

namespace EvoSC.Common.Services;

public class PlayerManagerService : IPlayerManagerService
{
    private readonly ILogger<PlayerManagerService> _logger;
    private readonly IPlayerRepository _playerRepository;
    private readonly IPlayerCacheService _playerCache;
    private readonly IServerClient _server;

    public PlayerManagerService(ILogger<PlayerManagerService> logger, IPlayerRepository playerRepository, IPlayerCacheService playerCache, IServerClient server)
    {
        _logger = logger;
        _playerRepository = playerRepository;
        _playerCache = playerCache;
        _server = server;
    }

    public async Task<IPlayer?> GetPlayerAsync(string accountId) =>
        await _playerRepository.GetPlayerByAccountIdAsync(accountId);

    public async Task<IPlayer> GetOrCreatePlayerAsync(string accountId)
    {
        var player = await GetPlayerAsync(accountId);

        if (player != null)
        {
            return player;
        }

        return await CreatePlayerAsync(accountId);
    }

    public async Task<IPlayer> CreatePlayerAsync(string accountId)
    {
        var playerLogin = PlayerUtils.ConvertAccountIdToLogin(accountId);

        TmPlayerDetailedInfo? playerInfo = null;
        // TODO: Create player with default properties when limited information is available #81 https://github.com/EvoTM/EvoSC-sharp/issues/81
        try
        {
            playerInfo = await _server.Remote.GetDetailedPlayerInfoAsync(playerLogin);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Player not on server");
        }

        if (playerInfo == null)
        {
            throw new InvalidOperationException("Player info is null, cannot create player.");
        }

        return await _playerRepository.AddPlayerAsync(accountId, playerInfo);
    }

    public async Task<IOnlinePlayer> GetOnlinePlayerAsync(string accountId)
    {
        var player = await _playerCache.GetOnlinePlayerCachedAsync(accountId);

        if (player == null)
        {
            throw new InvalidOperationException(
                $"Failed to get online player with account ID '{accountId}' from cache. Player object is null.");
        }

        return player;
    }

    public Task<IOnlinePlayer> GetOnlinePlayerAsync(IPlayer player) => GetOnlinePlayerAsync(player.AccountId);

    public Task UpdateLastVisitAsync(IPlayer player) => _playerRepository.UpdateLastVisitAsync(player);

    public Task<IEnumerable<IOnlinePlayer>> GetOnlinePlayersAsync() => Task.FromResult(_playerCache.OnlinePlayers);

    private const int MinMatchingCharacters = 2;
    
    public async Task<IEnumerable<IOnlinePlayer>> FindOnlinePlayerAsync(string nickname)
    {
        var players = (await GetOnlinePlayersAsync()).ToArray();
        var distances = new List<dynamic>();

        foreach (var player in players)
        {
            var cleanedName = FormattingUtils.CleanTmFormatting(player.NickName);
            var editDistance = StringEditDistance.GetDistance(nickname, cleanedName);

            // need at least 3 matching characters and ignore completely wrong names
            if (editDistance >= cleanedName.Length - MinMatchingCharacters)
            {
                continue;
            }
            
            distances.Add(new {Player = player, Distance = editDistance});
        }

        return distances
            .OrderBy(e => e.Distance)
            .Select(e => (IOnlinePlayer)e.Player)
            .ToList();
    }
}
