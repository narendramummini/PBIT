using System;
using System.Collections.Generic;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    public static class MigrationSessionAccess
    {
        public static MigrationSession GetForCaller(
            SessionStore store, string sessionId, bool isAuthenticated, string? callerId)
        {
            if (!isAuthenticated || string.IsNullOrWhiteSpace(callerId))
                throw new KeyNotFoundException("Migration session not found.");

            var session = store.Get(sessionId);
            if (session == null || !string.Equals(session.OwnerId, callerId, StringComparison.Ordinal))
                throw new KeyNotFoundException("Migration session not found.");
            return session;
        }
    }
}
