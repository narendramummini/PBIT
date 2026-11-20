using System;
using System.Collections.Concurrent;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    /// <summary>
    /// In-process session store. Replace with Redis/DB for multi-instance deployments.
    /// </summary>
    public class SessionStore
    {
        private readonly ConcurrentDictionary<string, MigrationSession> _store = new();

        public MigrationSession Create()
        {
            var s = new MigrationSession();
            _store[s.SessionId] = s;
            return s;
        }

        public MigrationSession? Get(string id) =>
            _store.TryGetValue(id, out var s) ? s : null;

        public void Update(MigrationSession session) =>
            _store[session.SessionId] = session;

        public MigrationSession GetOrThrow(string id) =>
            Get(id) ?? throw new KeyNotFoundException($"Session {id} not found");
    }
}
