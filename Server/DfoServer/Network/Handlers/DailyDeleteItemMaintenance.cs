using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    internal static class DailyDeleteItemMaintenance
    {
        internal static void Register(
            ISessionDirectory sessions,
            CharacterTransitionCoordinator transitions)
        {
            if (sessions == null || transitions == null)
                return;
            ClockService.Instance.RegisterDailyMoment(
                "daily-delete-items", 6, 0, _ => Run(sessions, transitions));
        }

        internal static void Run(
            ISessionDirectory sessions,
            CharacterTransitionCoordinator transitions)
        {
            if (sessions == null || transitions == null)
                return;

            foreach (var session in sessions.GetAllGameSessions())
            {
                var characterId = session?.Player?.CharacterId ?? 0;
                if (characterId <= 0)
                    continue;

                using (transitions.Acquire(characterId))
                {
                    var sessionId = session.SessionId;
                    if (session.Player?.CharacterId != characterId
                        || !transitions.IsCurrent(session)
                        || !InventoryContext.TryGetLease(characterId, out var lease)
                        || !InventoryContext.IsCurrentLease(lease, sessionId, characterId))
                        continue;

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    lock (lease.SyncRoot)
                    {
                        if (!InventoryItemLifecycleService.HasExpiredItems(lease.Inventory, now))
                            continue;
                    }

                    var changes = new InventoryMutationSet();
                    if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                            lease,
                            "daily-delete-items",
                            (_, _) =>
                            {
                                if (!transitions.IsCurrent(session)
                                    || !InventoryContext.IsCurrentLease(
                                        lease, sessionId, characterId))
                                    return false;

                                InventoryItemLifecycleService.RemoveExpiredItems(
                                    lease.Inventory,
                                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                    changes);
                                return true;
                            }) || !changes.HasChanges)
                        continue;

                    ClockService.Instance.ScheduleOneShotAsync(
                        "daily-delete-items-refresh:" + sessionId,
                        DateTime.UtcNow,
                        async _ =>
                        {
                            using (await transitions.AcquireAsync(characterId))
                            {
                                if (session.Player?.CharacterId != characterId
                                    || session.SessionId != sessionId
                                    || !transitions.IsCurrent(session)
                                    || !InventoryContext.IsCurrentLease(
                                        lease, sessionId, characterId))
                                    return;

                                foreach (var group in changes.Slots.GroupBy(
                                    slot => slot.ListType))
                                {
                                    await InventoryRefreshSender.SendOnlineUpdateItemList(
                                        session, group.Key,
                                        group.Select(slot => slot.SlotIndex));
                                }
                            }
                        });
                }
            }
        }
    }
}
