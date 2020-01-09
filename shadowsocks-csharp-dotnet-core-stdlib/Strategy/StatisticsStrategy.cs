﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

using Newtonsoft.Json;

using NLog;

using Shadowsocks.Std.Controller;
using Shadowsocks.Std.Model;
using Shadowsocks.Std.Service;
using Shadowsocks.Std.Util;

namespace Shadowsocks.Std.Strategy
{
    using Statistics = Dictionary<string, List<StatisticsRecord>>;

    internal class StatisticsStrategy : IStrategy, IDisposable
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly ShadowsocksController _controller;
        private Server _currentServer;
        private readonly Timer _timer;
        private Statistics _filteredStatistics;
        private AvailabilityStatistics Service => _controller.availabilityStatistics;
        private int ChoiceKeptMilliseconds
            => (int)TimeSpan.FromMinutes(_controller.StatisticsConfiguration.ChoiceKeptMinutes).TotalMilliseconds;

        public StatisticsStrategy(ShadowsocksController controller)
        {
            _controller = controller;
            var servers = controller.GetCurrentConfiguration().configs;
            var randomIndex = new Random().Next() % servers.Count;
            _currentServer = servers[randomIndex];  //choose a server randomly at first
            // FIXME: consider Statistics and Config changing asynchrously.
            _timer = new Timer(ReloadStatisticsAndChooseAServer);
        }

        private void ReloadStatisticsAndChooseAServer(object obj)
        {
            _logger.Debug("Reloading statistics and choose a new server....");
            var servers = _controller.GetCurrentConfiguration().configs;
            LoadStatistics();
            ChooseNewServer(servers);
        }

        private void LoadStatistics()
        {
            _filteredStatistics =
                Service.FilteredStatistics ??
                Service.RawStatistics ??
                _filteredStatistics;
        }

        //return the score by data
        //server with highest score will be choosen
        private float? GetScore(string identifier, List<StatisticsRecord> records)
        {
            var config = _controller.StatisticsConfiguration;
            float? score = null;

            var averageRecord = new StatisticsRecord(identifier,
                records.Where(record => record.MaxInboundSpeed != null).Select(record => record.MaxInboundSpeed.Value).ToList(),
                records.Where(record => record.MaxOutboundSpeed != null).Select(record => record.MaxOutboundSpeed.Value).ToList(),
                records.Where(record => record.AverageLatency != null).Select(record => record.AverageLatency.Value).ToList());
            averageRecord.SetResponse(records.Select(record => record.AverageResponse).ToList());

            foreach (var calculation in config.Calculations)
            {
                var name = calculation.Key;
                var field = typeof (StatisticsRecord).GetField(name);
                dynamic value = field?.GetValue(averageRecord);
                var factor = calculation.Value;
                if (value == null || factor.Equals(0)) continue;
                score ??= 0;
                score += value * factor;
            }

            if (score != null)
            {
                _logger.Debug($"Highest score: {score} {JsonConvert.SerializeObject(averageRecord, Formatting.Indented)}");
            }
            return score;
        }

        private void ChooseNewServer(List<Server> servers)
        {
            if (_filteredStatistics == null || servers.Count == 0)
            {
                return;
            }
            try
            {
                var serversWithStatistics = (from server in servers
                    let id = server.Identifier()
                    where _filteredStatistics.ContainsKey(id)
                    let score = GetScore(id, _filteredStatistics[id])
                    where score != null
                    select new
                    {
                        server,
                        score
                    }).ToArray();

                if (serversWithStatistics.Length < 2)
                {
                    LogWhenEnabled("no enough statistics data or all factors in calculations are 0");
                    return;
                }

                var bestResult = serversWithStatistics
                    .Aggregate((server1, server2) => server1.score > server2.score ? server1 : server2);

                LogWhenEnabled($"Switch to server: {bestResult.server.FriendlyName()} by statistics: score {bestResult.score}");
                _currentServer = bestResult.server;
            }
            catch (Exception e)
            {
                _logger.LogUsefulException(e);
            }
        }

        private void LogWhenEnabled(string log)
        {
            if (_controller.GetCurrentStrategy()?.ID == ID) //output when enabled
            {
                Console.WriteLine(log);
            }
        }

        public string ID => "com.shadowsocks.strategy.scbs";

        public string Name => I18N.GetString("Choose by statistics");

        public Server GetAServer(IStrategyCallerType type, IPEndPoint localIPEndPoint, EndPoint destEndPoint)
        {
            if (_currentServer == null)
            {
                ChooseNewServer(_controller.GetCurrentConfiguration().configs);
            }
            return _currentServer;  //current server cached for CachedInterval
        }

        public void ReloadServers()
        {
            ChooseNewServer(_controller.GetCurrentConfiguration().configs);
            _timer?.Change(0, ChoiceKeptMilliseconds);
        }

        public void SetFailure(Server server)
        {
            _logger.Debug($"failure: {server.FriendlyName()}");
        }

        public void UpdateLastRead(Server server) { }

        public void UpdateLastWrite(Server server) { }

        public void UpdateLatency(Server server, TimeSpan latency) { }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
