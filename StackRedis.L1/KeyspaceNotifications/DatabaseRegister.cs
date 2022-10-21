using StackRedis.L1.MemoryCache;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using StackRedis.L1.MemoryCache.Types;
using StackRedis.L1.MemoryCache.Types.SortedSet;

namespace StackRedis.L1.KeyspaceNotifications
{
    internal sealed class DatabaseRegister : IDisposable
    {
        internal static DatabaseRegister instance = new();

        private Dictionary<string, DatabaseInstanceData> _dbData = new();
        private static readonly object _lockObj = new();

        private DatabaseRegister()
        { }

        internal void RemoveInstanceData(string dbIdentifier)
        {
            lock (_lockObj)
            {
                if (_dbData.ContainsKey(dbIdentifier))
                {
                    _dbData.Remove(dbIdentifier);
                }
            }
        }

        internal DatabaseInstanceData GetDatabaseInstanceData(string dbIdentifier, IDatabase redisDb, string instance = null)
        {
            //Check if this db is already registered, and register it for notifications if necessary
            lock (_lockObj)
            {
                if (!_dbData.ContainsKey(dbIdentifier))
                {
                    _dbData.Add(dbIdentifier, new DatabaseInstanceData(redisDb, instance));
                }
            }

            return _dbData[dbIdentifier];
        }

        public void Dispose()
        {
            foreach(var db in _dbData)
            {
                db.Value.Dispose();
            }

            _dbData = new Dictionary<string, DatabaseInstanceData>();
        }
    }

    /// <summary>
    /// Holds details for each Redis database.
    /// </summary>
    internal class DatabaseInstanceData : IDisposable
    {
        /// <summary>
        /// The notification listener to handle keyspace notifications
        /// </summary>
        public NotificationListener Listener { get; private set; }

        /// <summary>
        /// Memory cache for this database
        /// </summary>
        public ObjMemCache MemoryCache { get; private set; }

        internal MemoryStrings MemoryStrings { get; private set; }
        internal MemoryHashes MemoryHashes { get; private set; }
        internal MemorySets MemorySets { get; private set; }
        internal MemorySortedSet MemorySortedSets { get; private set; }
        internal string Instance { get; private set; }

        internal DatabaseInstanceData(IDatabase redisDb, string instance = null)
        {
            Instance = instance;
            MemoryCache = new ObjMemCache();
            MemoryStrings = new MemoryStrings(MemoryCache);
            MemoryHashes = new MemoryHashes(MemoryCache);
            MemorySets = new MemorySets(MemoryCache);
            MemorySortedSets = new MemorySortedSet(MemoryCache);

            //If we have access to a redis instance, then listen to it for notifications
            if (redisDb != null)
            {
                Listener = new NotificationListener(redisDb);

                //Connect the memory cache to the listener. Its data will be updated when keyspace events occur.
                Listener.HandleKeyspaceEvents(this);
            }
        }
        
        public void Dispose()
        {
            Listener.Dispose();
            MemoryCache.Dispose();
        }
    }
}
