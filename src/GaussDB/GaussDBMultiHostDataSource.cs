using HuaweiCloud.GaussDB.Internal;
using HuaweiCloud.GaussDB.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace HuaweiCloud.GaussDB;

/// <summary>
/// An <see cref="GaussDBDataSource" /> which manages connections for multiple hosts, is aware of their states (primary, secondary,
/// offline...) and can perform failover and load balancing across them.
/// </summary>
/// <remarks>
/// See <see href="https://www.gaussdb.org/doc/failover-and-load-balancing.html" />.
/// </remarks>
public sealed class GaussDBMultiHostDataSource : GaussDBDataSource
{
    internal override bool OwnsConnectors => false;

    readonly GaussDBDataSource[] _pools;
    readonly ConcurrentDictionary<string, GaussDBDataSource> _endpointPools = new(StringComparer.Ordinal);
    readonly SeedCluster[] _seedClusters;
    readonly string _urlKey;

    internal GaussDBDataSource[] Pools => _pools;

    readonly MultiHostDataSourceWrapper[] _wrappers;

    volatile int _roundRobinIndex = -1;

    internal GaussDBMultiHostDataSource(GaussDBConnectionStringBuilder settings, GaussDBDataSourceConfiguration dataSourceConfig)
        : base(settings, dataSourceConfig)
    {
        var hosts = settings.Host!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _pools = new GaussDBDataSource[hosts.Length];
        var seedEndpoints = new HaEndpoint[hosts.Length];
        for (var i = 0; i < hosts.Length; i++)
        {
            var poolSettings = settings.Clone();
            Debug.Assert(!poolSettings.Multiplexing);
            var host = hosts[i].AsSpan().Trim();
            if (GaussDBConnectionStringBuilder.TrySplitHostPort(host, out var newHost, out var newPort))
            {
                poolSettings.Host = newHost;
                poolSettings.Port = newPort;
                seedEndpoints[i] = new(newHost, newPort);
            }
            else
            {
                poolSettings.Host = host.ToString();
                seedEndpoints[i] = new(host.ToString(), settings.Port);
            }

            _pools[i] = settings.Pooling
                ? new PoolingDataSource(poolSettings, dataSourceConfig)
                : new UnpooledDataSource(poolSettings, dataSourceConfig);
            _endpointPools.TryAdd(seedEndpoints[i].Key, _pools[i]);
        }

        _seedClusters = BuildSeedClusters(settings, seedEndpoints);
        _urlKey = CreateKey(seedEndpoints);

        var targetSessionAttributeValues = Enum.GetValues<TargetSessionAttributes>().ToArray();
        var highestValue = 0;
        foreach (var value in targetSessionAttributeValues)
            if ((int)value > highestValue)
                highestValue = (int)value;

        _wrappers = new MultiHostDataSourceWrapper[highestValue + 1];
        foreach (var targetSessionAttribute in targetSessionAttributeValues)
            _wrappers[(int)targetSessionAttribute] = new(this, targetSessionAttribute);
    }

    /// <summary>
    /// Returns a new, unopened connection from this data source.
    /// </summary>
    /// <param name="targetSessionAttributes">Specifies the server type (e.g. primary, standby).</param>
    public GaussDBConnection CreateConnection(TargetSessionAttributes targetSessionAttributes)
        => GaussDBConnection.FromDataSource(_wrappers[(int)targetSessionAttributes]);

    /// <summary>
    /// Returns a new, opened connection from this data source.
    /// </summary>
    /// <param name="targetSessionAttributes">Specifies the server type (e.g. primary, standby).</param>
    public GaussDBConnection OpenConnection(TargetSessionAttributes targetSessionAttributes)
    {
        var connection = CreateConnection(targetSessionAttributes);

        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns a new, opened connection from this data source.
    /// </summary>
    /// <param name="targetSessionAttributes">Specifies the server type (e.g. primary, standby).</param>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    public async ValueTask<GaussDBConnection> OpenConnectionAsync(
        TargetSessionAttributes targetSessionAttributes,
        CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection(targetSessionAttributes);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns an <see cref="GaussDBDataSource" /> that wraps this multi-host one with the given server type.
    /// </summary>
    /// <param name="targetSessionAttributes">Specifies the server type (e.g. primary, standby).</param>
    public GaussDBDataSource WithTargetSession(TargetSessionAttributes targetSessionAttributes)
        => _wrappers[(int)targetSessionAttributes];

    static bool IsPreferred(DatabaseState state, TargetSessionAttributes preferredType)
        => state switch
        {
            DatabaseState.Offline => false,
            DatabaseState.Unknown => true, // We will check compatibility again after refreshing the database state

            DatabaseState.PrimaryReadWrite when preferredType is
                TargetSessionAttributes.Primary or
                TargetSessionAttributes.PreferPrimary or
                TargetSessionAttributes.ReadWrite
                => true,

            DatabaseState.PrimaryReadOnly when preferredType is
                TargetSessionAttributes.Primary or
                TargetSessionAttributes.PreferPrimary or
                TargetSessionAttributes.ReadOnly
                => true,

            DatabaseState.Standby when preferredType is
                TargetSessionAttributes.Standby or
                TargetSessionAttributes.PreferStandby or
                TargetSessionAttributes.ReadOnly
                => true,

            _ => preferredType == TargetSessionAttributes.Any
        };

    static bool IsOnline(DatabaseState state, TargetSessionAttributes preferredType)
    {
        Debug.Assert(preferredType is TargetSessionAttributes.PreferPrimary or TargetSessionAttributes.PreferStandby);
        return state != DatabaseState.Offline;
    }

    async ValueTask<GaussDBConnector?> TryGetIdleOrNew(
        IReadOnlyList<GaussDBDataSource> pools,
        GaussDBConnection conn,
        TimeSpan timeoutPerHost,
        bool async,
        TargetSessionAttributes preferredType, Func<DatabaseState, TargetSessionAttributes, bool> stateValidator,
        IList<Exception> exceptions,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < pools.Count; i++)
        {
            var pool = pools[i];
            var databaseState = pool.GetDatabaseState();
            if (!stateValidator(databaseState, preferredType))
                continue;

            GaussDBConnector? connector = null;

            try
            {
                if (pool.TryGetIdleConnector(out connector))
                {
                    if (databaseState == DatabaseState.Unknown)
                    {
                        databaseState = await connector.QueryDatabaseState(new GaussDBTimeout(timeoutPerHost), async, cancellationToken).ConfigureAwait(false);
                        Debug.Assert(databaseState != DatabaseState.Unknown);
                        if (!stateValidator(databaseState, preferredType))
                        {
                            pool.Return(connector);
                            continue;
                        }
                    }

                    return connector;
                }
                else
                {
                    connector = await pool.OpenNewConnector(conn, new GaussDBTimeout(timeoutPerHost), async, cancellationToken).ConfigureAwait(false);
                    if (connector is not null)
                    {
                        if (databaseState == DatabaseState.Unknown)
                        {
                            // While opening a new connector we might have refreshed the database state, check again
                            databaseState = pool.GetDatabaseState();
                            if (databaseState == DatabaseState.Unknown)
                                databaseState = await connector.QueryDatabaseState(new GaussDBTimeout(timeoutPerHost), async, cancellationToken).ConfigureAwait(false);
                            Debug.Assert(databaseState != DatabaseState.Unknown);
                            if (!stateValidator(databaseState, preferredType))
                            {
                                pool.Return(connector);
                                continue;
                            }
                        }

                        return connector;
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                if (connector is not null)
                    pool.Return(connector);
            }
        }

        return null;
    }

    async ValueTask<GaussDBConnector?> TryGet(
        IReadOnlyList<GaussDBDataSource> pools,
        GaussDBConnection conn,
        TimeSpan timeoutPerHost,
        bool async,
        TargetSessionAttributes preferredType,
        Func<DatabaseState, TargetSessionAttributes, bool> stateValidator,
        IList<Exception> exceptions,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < pools.Count; i++)
        {
            var pool = pools[i];
            var databaseState = pool.GetDatabaseState();
            if (!stateValidator(databaseState, preferredType))
                continue;

            GaussDBConnector? connector = null;

            try
            {
                connector = await pool.Get(conn, new GaussDBTimeout(timeoutPerHost), async, cancellationToken).ConfigureAwait(false);
                if (databaseState == DatabaseState.Unknown)
                {
                    // Get might have opened a new physical connection and refreshed the database state, check again
                    databaseState = pool.GetDatabaseState();
                    if (databaseState == DatabaseState.Unknown)
                        databaseState = await connector.QueryDatabaseState(new GaussDBTimeout(timeoutPerHost), async, cancellationToken).ConfigureAwait(false);

                    Debug.Assert(databaseState != DatabaseState.Unknown);
                    if (!stateValidator(databaseState, preferredType))
                    {
                        pool.Return(connector);
                        continue;
                    }
                }

                return connector;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                if (connector is not null)
                    pool.Return(connector);
            }
        }

        return null;
    }

    internal override async ValueTask<GaussDBConnector> Get(
        GaussDBConnection conn,
        GaussDBTimeout timeout,
        bool async,
        CancellationToken cancellationToken)
    {
        CheckDisposed();

        var exceptions = new List<Exception>();
        var timeoutPerHost = timeout.IsSet ? timeout.CheckAndGetTimeLeft() : TimeSpan.Zero;
        var preferredType = GetTargetSessionAttributes(conn);
        var checkUnpreferred = preferredType is TargetSessionAttributes.PreferPrimary or TargetSessionAttributes.PreferStandby;

        if (!Settings.UsesHaRouting)
        {
            var candidatePools = GetLegacyPools(conn.Settings.LoadBalanceHosts);
            var legacyConnector = await TryGetIdleOrNew(candidatePools, conn, timeoutPerHost, async, preferredType, IsPreferred, exceptions, cancellationToken).ConfigureAwait(false) ??
                                  (checkUnpreferred
                                      ? await TryGetIdleOrNew(candidatePools, conn, timeoutPerHost, async, preferredType, IsOnline, exceptions, cancellationToken).ConfigureAwait(false)
                                      : null) ??
                                  await TryGet(candidatePools, conn, timeoutPerHost, async, preferredType, IsPreferred, exceptions, cancellationToken).ConfigureAwait(false) ??
                                  (checkUnpreferred
                                      ? await TryGet(candidatePools, conn, timeoutPerHost, async, preferredType, IsOnline, exceptions, cancellationToken).ConfigureAwait(false)
                                      : null);

            return legacyConnector ?? throw NoSuitableHostsException(exceptions);
        }

        var clusterPlans = await BuildClusterRoutingPlans(conn, async, cancellationToken).ConfigureAwait(false);
        foreach (var clusterPlan in clusterPlans)
        {
            if (clusterPlan.Pools.Length == 0)
                continue;

            var connector = await TryGetIdleOrNew(clusterPlan.Pools, conn, timeoutPerHost, async, preferredType, IsPreferred, exceptions, cancellationToken).ConfigureAwait(false) ??
                            (checkUnpreferred
                                ? await TryGetIdleOrNew(clusterPlan.Pools, conn, timeoutPerHost, async, preferredType, IsOnline, exceptions, cancellationToken).ConfigureAwait(false)
                                : null) ??
                            await TryGet(clusterPlan.Pools, conn, timeoutPerHost, async, preferredType, IsPreferred, exceptions, cancellationToken).ConfigureAwait(false) ??
                            (checkUnpreferred
                                ? await TryGet(clusterPlan.Pools, conn, timeoutPerHost, async, preferredType, IsOnline, exceptions, cancellationToken).ConfigureAwait(false)
                                : null);

            if (connector is not null)
            {
                if (Settings.PriorityServers > 0)
                    GaussDBGlobalClusterStatusTracker.ReportPrimary(_urlKey, clusterPlan.ClusterKey);

                return connector;
            }
        }

        throw NoSuitableHostsException(exceptions);
    }

    GaussDBDataSource[] GetLegacyPools(bool loadBalanceHosts)
    {
        if (!loadBalanceHosts || _pools.Length <= 1)
            return _pools;

        var startIndex = GetRoundRobinIndex() % _pools.Length;
        var candidatePools = new GaussDBDataSource[_pools.Length];
        for (var i = 0; i < _pools.Length; i++)
            candidatePools[i] = _pools[(startIndex + i) % _pools.Length];

        return candidatePools;
    }

    ValueTask<ClusterRoutingPlan[]> BuildClusterRoutingPlans(GaussDBConnection conn, bool async, CancellationToken cancellationToken)
        => async
            ? BuildClusterRoutingPlansAsync(conn, cancellationToken)
            : new(BuildClusterRoutingPlansAsync(conn, cancellationToken).GetAwaiter().GetResult());

    async ValueTask<ClusterRoutingPlan[]> BuildClusterRoutingPlansAsync(GaussDBConnection conn, CancellationToken cancellationToken)
    {
        var orderedClusters = OrderClusters();
        var plans = new ClusterRoutingPlan[orderedClusters.Length];
        for (var i = 0; i < orderedClusters.Length; i++)
        {
            var cluster = orderedClusters[i];
            var endpoints = await ResolveClusterEndpoints(cluster, cancellationToken).ConfigureAwait(false);
            var orderedEndpoints = OrderClusterEndpoints(conn.Settings, cluster, endpoints);
            plans[i] = new(cluster.Key, orderedEndpoints.Select(GetOrAddEndpointPool)
                .Where(static pool => pool.GetDatabaseState() != DatabaseState.Offline)
                .ToArray());
        }

        return plans;
    }

    SeedCluster[] OrderClusters()
    {
        if (_seedClusters.Length <= 1)
            return _seedClusters;

        var preferredClusterKey = GaussDBGlobalClusterStatusTracker.GetPreferredClusterKey(_urlKey);
        if (preferredClusterKey is null)
            return _seedClusters;

        return _seedClusters
            .OrderByDescending(cluster => cluster.Key == preferredClusterKey)
            .ToArray();
    }

    async ValueTask<HaEndpoint[]> ResolveClusterEndpoints(SeedCluster cluster, CancellationToken cancellationToken)
    {
        var snapshot = await GaussDBCoordinatorListTracker.GetSnapshotAsync(
                cluster.Key,
                TimeSpan.FromSeconds(Settings.RefreshCNIpListTime),
                ct => RefreshCoordinatorEndpoints(cluster, ct),
                async: true,
                cancellationToken)
            .ConfigureAwait(false);

        return snapshot is { Length: > 0 }
            ? snapshot
            : cluster.SeedEndpoints;
    }

    async ValueTask<HaEndpoint[]?> RefreshCoordinatorEndpoints(SeedCluster cluster, CancellationToken cancellationToken)
    {
        foreach (var endpoint in cluster.SeedEndpoints)
        {
            var pool = GetOrAddEndpointPool(endpoint);
            try
            {
                var connection = await pool.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = Settings.UsingEip
                        ? "select node_host1,node_port1 from pgxc_node where node_type='C' and nodeis_active = true order by node_host1;"
                        : "select node_host,node_port from pgxc_node where node_type='C' and nodeis_active = true order by node_host;";

                    var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        var refreshedEndpoints = new List<HaEndpoint>();
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            refreshedEndpoints.Add(new(reader.GetString(0), reader.GetInt32(1)));

                        return refreshedEndpoints.Count == 0 ? null : refreshedEndpoints.ToArray();
                    }
                }
            }
            catch
            {
                // Fall back to static seed routing if discovery is unavailable from this endpoint.
            }
        }

        return null;
    }

    HaEndpoint[] OrderClusterEndpoints(
        GaussDBConnectionStringBuilder settings,
        SeedCluster cluster,
        IReadOnlyList<HaEndpoint> resolvedEndpoints)
    {
        if (resolvedEndpoints.Count <= 1)
            return resolvedEndpoints.ToArray();

        var allEndpoints = resolvedEndpoints.ToList();
        var priorityCount = settings.GetEffectivePriorityHostCount(cluster.SeedEndpoints.Length);

        List<HaEndpoint> priorityEndpoints;
        List<HaEndpoint> nonPriorityEndpoints;
        if (priorityCount > 0)
        {
            var priorityKeys = new HashSet<string>(cluster.SeedEndpoints.Take(priorityCount).Select(static endpoint => endpoint.Key), StringComparer.Ordinal);
            priorityEndpoints = allEndpoints.Where(endpoint => priorityKeys.Contains(endpoint.Key)).ToList();
            nonPriorityEndpoints = allEndpoints.Where(endpoint => !priorityKeys.Contains(endpoint.Key)).ToList();
        }
        else
        {
            priorityEndpoints = [];
            nonPriorityEndpoints = allEndpoints;
        }

        switch (settings.AutoBalanceModeParsed)
        {
        case HaAutoBalanceMode.Shuffle:
            Shuffle(nonPriorityEndpoints);
            return nonPriorityEndpoints.ToArray();
        case HaAutoBalanceMode.RoundRobin:
            return Rotate(nonPriorityEndpoints).ToArray();
        case HaAutoBalanceMode.Priority:
            if (priorityEndpoints.Count > 0)
            {
                var orderedPriorityEndpoints = Rotate(priorityEndpoints);
                Shuffle(nonPriorityEndpoints);
                orderedPriorityEndpoints.AddRange(nonPriorityEndpoints);
                return orderedPriorityEndpoints.ToArray();
            }

            return Rotate(allEndpoints).ToArray();
        case HaAutoBalanceMode.ShufflePriority:
            if (priorityEndpoints.Count > 0)
            {
                Shuffle(priorityEndpoints);
                Shuffle(nonPriorityEndpoints);
                priorityEndpoints.AddRange(nonPriorityEndpoints);
                return priorityEndpoints.ToArray();
            }

            return Rotate(allEndpoints).ToArray();
        default:
            return settings.LoadBalanceHosts
                ? Rotate(allEndpoints).ToArray()
                : allEndpoints.ToArray();
        }
    }

    GaussDBDataSource GetOrAddEndpointPool(HaEndpoint endpoint)
        => _endpointPools.GetOrAdd(endpoint.Key, _ =>
        {
            var poolSettings = Settings.Clone();
            poolSettings.Host = endpoint.Host;
            poolSettings.Port = endpoint.Port;
            return Settings.Pooling
                ? new PoolingDataSource(poolSettings, Configuration)
                : new UnpooledDataSource(poolSettings, Configuration);
        });

    List<T> Rotate<T>(IReadOnlyList<T> source)
    {
        if (source.Count <= 1)
            return source.ToList();

        var startIndex = GetRoundRobinIndex() % source.Count;
        var rotated = new List<T>(source.Count);
        for (var i = 0; i < source.Count; i++)
            rotated.Add(source[(startIndex + i) % source.Count]);
        return rotated;
    }

    static void Shuffle<T>(IList<T> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var swapIndex = Random.Shared.Next(i + 1);
            (values[i], values[swapIndex]) = (values[swapIndex], values[i]);
        }
    }

    static GaussDBException NoSuitableHostsException(IList<Exception> exceptions)
    {
        return exceptions.Count == 0
            ? new GaussDBException("No suitable host was found.")
            : exceptions[0] is PostgresException firstException && AllEqual(firstException, exceptions)
                ? firstException
                : new GaussDBException("Unable to connect to a suitable host. Check inner exception for more details.",
                    new AggregateException(exceptions));

        static bool AllEqual(PostgresException first, IList<Exception> exceptions)
        {
            foreach (var x in exceptions)
                if (x is not PostgresException ex || ex.SqlState != first.SqlState)
                    return false;
            return true;
        }
    }

    int GetRoundRobinIndex()
    {
        while (true)
        {
            var index = Interlocked.Increment(ref _roundRobinIndex);
            if (index >= 0)
                return index % _pools.Length;

            // Worst case scenario - we've wrapped around integer counter
            if (index == int.MinValue)
            {
                // This is the thread which wrapped around the counter - reset it to 0
                _roundRobinIndex = 0;
                return 0;
            }

            // This is not the thread which wrapped around the counter - just wait until it's 0 or more
            var sw = new SpinWait();
            while (_roundRobinIndex < 0)
                sw.SpinOnce();
        }
    }

    internal override void Return(GaussDBConnector connector)
        => throw new GaussDBException("GaussDB bug: a connector was returned to " + nameof(GaussDBMultiHostDataSource));

    internal override bool TryGetIdleConnector([NotNullWhen(true)] out GaussDBConnector? connector)
        => throw new GaussDBException("GaussDB bug: trying to get an idle connector from " + nameof(GaussDBMultiHostDataSource));

    internal override ValueTask<GaussDBConnector?> OpenNewConnector(GaussDBConnection conn, GaussDBTimeout timeout, bool async, CancellationToken cancellationToken)
        => throw new GaussDBException("GaussDB bug: trying to open a new connector from " + nameof(GaussDBMultiHostDataSource));

    /// <inheritdoc />
    public override void Clear()
    {
        foreach (var pool in _endpointPools.Values)
            pool.Clear();
    }

    /// <summary>
    /// Clears the database state (primary, secondary, offline...) for all data sources managed by this multi-host data source.
    /// Can be useful to make GaussDB retry a PostgreSQL instance which was previously detected to be offline.
    /// </summary>
    public void ClearDatabaseStates()
    {
        foreach (var pool in _endpointPools.Values)
        {
            pool.UpdateDatabaseState(default, default, default, ignoreTimeStamp: true);
        }
    }

    internal override (int Total, int Idle, int Busy) Statistics
    {
        get
        {
            var numConnectors = 0;
            var idleCount = 0;

            foreach (var pool in _endpointPools.Values)
            {
                var stat = pool.Statistics;
                numConnectors += stat.Total;
                idleCount += stat.Idle;
            }

            return (numConnectors, idleCount, numConnectors - idleCount);
        }
    }

    internal override bool TryRentEnlistedPending(
        Transaction transaction,
        GaussDBConnection connection,
        [NotNullWhen(true)] out GaussDBConnector? connector)
    {
        lock (_pendingEnlistedConnectors)
        {
            if (!_pendingEnlistedConnectors.TryGetValue(transaction, out var list))
            {
                connector = null;
                return false;
            }

            var preferredType = GetTargetSessionAttributes(connection);
            // First try to get a valid preferred connector.
            if (TryGetValidConnector(list, preferredType, IsPreferred, out connector))
            {
                return true;
            }

            // Can't get valid preferred connector. Try to get an unpreferred connector, if supported.
            if ((preferredType == TargetSessionAttributes.PreferPrimary || preferredType == TargetSessionAttributes.PreferStandby)
                && TryGetValidConnector(list, preferredType, IsOnline, out connector))
            {
                return true;
            }

            connector = null;
            return false;
        }

        bool TryGetValidConnector(List<GaussDBConnector> list, TargetSessionAttributes preferredType,
            Func<DatabaseState, TargetSessionAttributes, bool> validationFunc, [NotNullWhen(true)] out GaussDBConnector? connector)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                connector = list[i];
                var lastKnownState = connector.DataSource.GetDatabaseState(ignoreExpiration: true);
                Debug.Assert(lastKnownState != DatabaseState.Unknown);
                if (validationFunc(lastKnownState, preferredType))
                {
                    list.RemoveAt(i);
                    if (list.Count == 0)
                        _pendingEnlistedConnectors.Remove(transaction);
                    return true;
                }
            }

            connector = null;
            return false;
        }
    }

    static TargetSessionAttributes GetTargetSessionAttributes(GaussDBConnection connection)
        => connection.Settings.TargetSessionAttributesParsed ??
           (PostgresEnvironment.TargetSessionAttributes is { } s
               ? GaussDBConnectionStringBuilder.ParseTargetSessionAttributes(s)
               : TargetSessionAttributes.Any);

    static SeedCluster[] BuildSeedClusters(GaussDBConnectionStringBuilder settings, HaEndpoint[] seedEndpoints)
    {
        if (settings.PriorityServers > 0 && settings.PriorityServers < seedEndpoints.Length)
        {
            return
            [
                new(CreateKey(seedEndpoints[..settings.PriorityServers]), seedEndpoints[..settings.PriorityServers]),
                new(CreateKey(seedEndpoints[settings.PriorityServers..]), seedEndpoints[settings.PriorityServers..])
            ];
        }

        return [new(CreateKey(seedEndpoints), seedEndpoints)];
    }

    static string CreateKey(IReadOnlyCollection<HaEndpoint> endpoints)
        => string.Join(",", endpoints.Select(static endpoint => endpoint.Key).OrderBy(static endpoint => endpoint, StringComparer.Ordinal));

    readonly record struct SeedCluster(string Key, HaEndpoint[] SeedEndpoints);
    readonly record struct ClusterRoutingPlan(string ClusterKey, GaussDBDataSource[] Pools);
}
