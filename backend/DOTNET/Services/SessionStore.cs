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

        public MigrationSession Create(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("An authenticated owner is required.", nameof(ownerId));
            var s = new MigrationSession { OwnerId = ownerId };
            _store[s.SessionId] = s;
            return s;
        }

        public MigrationSession? Get(string id) =>
            _store.TryGetValue(id, out var s) ? s : null;

        public void Update(MigrationSession session)
        {
            if (string.IsNullOrWhiteSpace(session.SessionId) || string.IsNullOrWhiteSpace(session.OwnerId) ||
                !_store.TryGetValue(session.SessionId, out var existing) || !ReferenceEquals(existing, session))
                throw new KeyNotFoundException("Migration session not found.");
            if (!string.Equals(existing.OwnerId, session.OwnerId, StringComparison.Ordinal))
                throw new InvalidOperationException("Migration ownership is immutable.");
            _store[session.SessionId] = session;
        }

        public MigrationSession GetOrThrow(string id) =>
            Get(id) ?? throw new KeyNotFoundException("Migration session not found.");
    }
}
