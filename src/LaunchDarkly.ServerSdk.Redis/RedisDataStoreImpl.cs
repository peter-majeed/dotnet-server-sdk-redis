﻿using System;
using System.Collections.Generic;
using LaunchDarkly.Logging;
using LaunchDarkly.Sdk.Server.Interfaces;
using StackExchange.Redis;

using static LaunchDarkly.Sdk.Server.Interfaces.DataStoreTypes;

namespace LaunchDarkly.Sdk.Server.Integrations
{
    internal sealed class RedisDataStoreImpl : IPersistentDataStore
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly string _prefix;
        private readonly Logger _log;
        
        // This is used for unit testing only
        internal Action _updateHook;

        internal RedisDataStoreImpl(
            ConfigurationOptions redisConfig,
            string prefix,
            Logger log
            )
        {
            _log = log;
            var redisConfigCopy = redisConfig.Clone();
            _log.Info("Creating Redis feature store using Redis server(s) at [{0}]",
                string.Join(", ", redisConfig.EndPoints));
            _redis = ConnectionMultiplexer.Connect(redisConfigCopy);
            _prefix = prefix;
        }
        
        public bool Initialized()
        {
            IDatabase db = _redis.GetDatabase();
            return db.KeyExists(_prefix);
        }

        public void Init(FullDataSet<SerializedItemDescriptor> allData)
        {
            IDatabase db = _redis.GetDatabase();
            ITransaction txn = db.CreateTransaction();
            foreach (var collection in allData.Data)
            {
                string key = ItemsKey(collection.Key);
                txn.KeyDeleteAsync(key);
                foreach (var item in collection.Value.Items)
                {
                    txn.HashSetAsync(key, item.Key, item.Value.SerializedItem);
                    // Note, these methods are async because this Redis client treats all actions
                    // in a transaction as async - they are only sent to Redis when we execute the
                    // transaction. We don't need to await them.
                }
            }
            txn.StringSetAsync(_prefix, "");
            txn.Execute();
        }
        
        public SerializedItemDescriptor? Get(DataKind kind, string key)
        {
            IDatabase db = _redis.GetDatabase();
            string json = db.HashGet(ItemsKey(kind), key);
            if (json == null)
            {
                _log.Debug("[get] Key: {0} not found in \"{1}\"", key, kind.Name);
                return null;
            }
            return new SerializedItemDescriptor(0, false, json);
        }

        public KeyedItems<SerializedItemDescriptor> GetAll(DataKind kind)
        {
            IDatabase db = _redis.GetDatabase();
            HashEntry[] allEntries = db.HashGetAll(ItemsKey(kind));
            var result = new List<KeyValuePair<string, SerializedItemDescriptor>>();
            foreach (HashEntry entry in allEntries)
            {
                result.Add(new KeyValuePair<string, SerializedItemDescriptor>(entry.Name,
                    new SerializedItemDescriptor(0, false, entry.Value)));
            }
            return new KeyedItems<SerializedItemDescriptor>(result);
        }

        public bool Upsert(DataKind kind, string key, SerializedItemDescriptor newItem)
        {
            IDatabase db = _redis.GetDatabase();
            string baseKey = ItemsKey(kind);
            while (true)
            {
                string oldData;
                try
                {
                    oldData = db.HashGet(baseKey, key);
                }
                catch (RedisTimeoutException e)
                {
                    _log.Error("Timeout in update when reading {0} from {1}: {2}", key, baseKey, e.ToString());
                    throw;
                }
                var oldVersion = (oldData is null) ? 0 : kind.Deserialize(oldData).Version;
                if (oldVersion >= newItem.Version)
                {
                    _log.Debug("Attempted to {0} key: {1} version: {2} with a version that is" +
                        " the same or older: {3} in \"{4}\"",
                        newItem.Deleted ? "delete" : "update",
                        key, oldVersion, newItem.Version, kind.Name);
                    return false;
                }

                // This hook is used only in unit tests
                _updateHook?.Invoke();

                // Note that transactions work a bit differently in StackExchange.Redis than in other
                // Redis clients. The same Redis connection is shared across all threads, so it can't
                // set a WATCH at the moment we start the transaction. Instead, it saves up all of
                // the actions we send during the transaction, and replays them all within a MULTI
                // when the transaction. AddCondition() is this client's way of doing a WATCH, and it
                // can only use an equality match on the whole value (which is unfortunate since a
                // serialized flag value could be fairly large).
                ITransaction txn = db.CreateTransaction();
                txn.AddCondition(oldData is null ? Condition.HashNotExists(baseKey, key) :
                    Condition.HashEqual(baseKey, key, oldData));

                txn.HashSetAsync(baseKey, key, newItem.SerializedItem);

                try
                {
                    bool success = txn.Execute();
                    if (!success)
                    {
                        // The watch was triggered, we should retry
                        _log.Debug("Concurrent modification detected, retrying");
                        continue;
                    }
                }
                catch (RedisTimeoutException e)
                {
                    _log.Error("Timeout on update of {0} in {1}: {2}", key, baseKey, e.ToString());
                    throw;
                }
                return true;
            }
        }

        public bool IsStoreAvailable()
        {
            try
            {
                Initialized(); // don't care about the return value, just that it doesn't throw an exception
                return true;
            }
            catch
            { // don't care about exception class, since any exception means the Redis request couldn't be made
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                _redis.Dispose();
            }
        }
        
        private string ItemsKey(DataKind kind) =>
            _prefix + ":" + kind.Name;
    }
}