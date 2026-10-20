using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 0x03B8 EMBLEM_COLLECT_ITEM (ENUM_CMDPACKET_EMBLEM_COLLECT_ITEM): 徽章回收箱。
        //
        // 请求体固定 26B。客户端唯一构造点 VA 0x16F6130 只写 +0x0E 起的 12 字节，
        // +0x00..+0x0D 既不初始化也不写值（栈残留），故解析只能使用这四个字段：
        //   +0x0E u16 箱子槽位(Main) | +0x10 u32 箱子模板 id | +0x14 u16 目标槽位 | +0x16 u32 目标物品模板 id
        // 依据：客户端 disasm(0x16F6130) + 2026-10-12 三帧实测 body（与 inventory.db 槽位/模板逐项吻合），
        // 详见 Docs/A21 徽章回收箱0x03B8请求与回包结构.md。
        private const int EmblemCollectBodyLength = 26;
        private const int EmblemCollectBoxSlotOffset = 0x0E;
        private const int EmblemCollectBoxTemplateOffset = 0x10;
        private const int EmblemCollectTargetSlotOffset = 0x14;
        private const int EmblemCollectTargetTemplateOffset = 0x16;

        // 回包固定 8B：u32 status + u32 返还的徽章模板 id（成功时才有意义）。
        // 客户端处理器 VA 0x125A9F0（注册点 0x125AF0C）读满 8 字节：
        //   status 0        = 成功，其后 u32 作为返还的徽章模板 id
        //   status 1/3/5    = 文案 0x11719「无法回收该装备的徽章」
        //   status 2        = 文案 0x11718「装备上的徽章等级不同…」
        //   status 4        = 文案 0x11716「只有镶嵌了2个徽章的装备…才能使用徽章回收箱。」
        //   其它值          = 客户端静默返回（玩家看不到任何反馈）
        // 具体取值由业务层 TryCollectEquipmentEmblems 的 failureStatus 给出（见 mutation service 的
        // EmblemCollectFailure* 常量），handler 只做透传；解析失败固定 status 1。
        // 客户端读取器不校验长度（先 memcpy 再无条件前进游标），短包会读到命令体之外，因此失败
        // 回包同样补齐 8 字节。
        private const uint EmblemCollectStatusOk = 0;
        private const uint EmblemCollectStatusNotCollectable = 1;

        public async Task Handle_EMBLEM_COLLECT_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var commandType = (ushort)CmdPacketType.EMBLEM_COLLECT_ITEM;
            FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM type=0x{commandType:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseEmblemCollectBody(body, out var request))
            {
                await SendEmblemCollectError(session, commandType, 0, EmblemCollectStatusNotCollectable);
                return;
            }

            var (cid, _) = ResolveOwner(session);
            EquipmentEmblemCollectResult result;
            bool ok;
            // 失败 status 由业务层（InventoryEquipmentMutationService.EmblemCollectFailure*）按拒绝原因给出。
            var failureStatus = EmblemCollectStatusNotCollectable;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitEmblemCollect(
                    lease,
                    request.BoxSlotIndex,
                    request.BoxTemplateId,
                    request.TargetSlotIndex,
                    request.TargetTemplateId,
                    out result,
                    out failureStatus,
                    out persistenceFailed);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM: FAILED box=0x{request.BoxTemplateId:X8}@{request.BoxSlotIndex} " +
                    $"target=0x{request.TargetTemplateId:X8}@{request.TargetSlotIndex} failureStatus={failureStatus} persistenceFailed={persistenceFailed}");
                await SendEmblemCollectError(session, commandType, request.TargetTemplateId, failureStatus);
                return;
            }

            var ackBody = BuildEmblemCollectAck(result.ReturnedEmblemTemplateId);
            RememberEmblemCollectAck(session.SessionId, ackBody);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, ackBody));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main,
                new[] { result.BoxSlotIndex, result.ReturnedEmblemSlotIndex });
            await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            var destroyedEmblem = result.DestroyedEmblemTemplateId > 0
                ? $"0x{result.DestroyedEmblemTemplateId:X8}"
                : "none";
            FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT: OK box=0x{result.BoxItemTemplateId:X8}@{result.BoxSlotIndex} " +
                $"target=0x{request.TargetTemplateId:X8}@{result.TargetSlotIndex} returned=0x{result.ReturnedEmblemTemplateId:X8}@{result.ReturnedEmblemSlotIndex} " +
                $"destroyed={destroyedEmblem}");
        }

        internal static bool TryParseEmblemCollectBody(byte[] body, out EmblemCollectRequest request)
        {
            request = default;
            if (body == null || body.Length < EmblemCollectBodyLength)
                return false;

            var boxSlot = BitConverter.ToUInt16(body, EmblemCollectBoxSlotOffset);
            var boxTemplateId = BitConverter.ToInt32(body, EmblemCollectBoxTemplateOffset);
            var targetSlot = BitConverter.ToUInt16(body, EmblemCollectTargetSlotOffset);
            var targetTemplateId = BitConverter.ToInt32(body, EmblemCollectTargetTemplateOffset);

            // 槽位按 u16 读取，超过 short 上限时强转会变成负槽位，直接判非法。
            if (boxTemplateId <= 0
                || targetTemplateId <= 0
                || boxSlot > short.MaxValue
                || targetSlot > short.MaxValue)
                return false;

            request = new EmblemCollectRequest((short)boxSlot, boxTemplateId, (short)targetSlot, targetTemplateId);
            return true;
        }

        // CMD 回包的 body 前必须带 1 个字节：客户端派发器会先吃掉它（实测该字节作为 arg2 传给处理器
        // 0x125A9F0，且处理器有 `arg2 != 0` 的前置门），之后 handler 才从 body+1 开始读它的 8 字节。
        // 少了这一字节会导致 status 整体错位（实测读到 0x48000000）并被判 OVERFLOW_INFO。
        private const byte EmblemCollectDispatcherArg = 0x01;

        internal static byte[] BuildEmblemCollectAck(int returnedEmblemTemplateId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(EmblemCollectDispatcherArg);
            writer.WriteUInt32(EmblemCollectStatusOk);
            writer.WriteUInt32((uint)Math.Max(0, returnedEmblemTemplateId));
            return writer.ToArray();
        }

        // 失败回包之后再补一条"窗口复位"包：客户端处理器在 status=0 且返还 id = -1 时只走
        // 窗口查找/复位分支（0x125AB03 → 0x1259F90），不显示道具。缺了它，客户端的回收窗口会
        // 停留在上一次的状态，之后其它回收箱再也打不开（实测）。
        internal static byte[] BuildEmblemCollectWindowReset()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(EmblemCollectDispatcherArg);
            writer.WriteUInt32(EmblemCollectStatusOk);
            writer.WriteUInt32(0xFFFFFFFFu);
            return writer.ToArray();
        }

        // failureStatus = 0 说明业务层没有给出具体拒绝原因（例如业务已应用但事务提交失败），
        // 此时回退到通用文案 status 1，避免把失败包发成客户端眼中的成功回包。
        private async Task SendEmblemCollectError(EnhancedClientSession session, ushort commandType, int relatedItemTemplateId, uint failureStatus)
        {
            var status = failureStatus != 0 ? failureStatus : EmblemCollectStatusNotCollectable;
            var body = BuildEmblemCollectError(status, relatedItemTemplateId);
            RememberEmblemCollectAck(session.SessionId, body);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, body));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, BuildEmblemCollectWindowReset()));
        }

        // 失败回包同样要带一个**有效物品模板 id**：实测客户端对 {status,0} 记 `[CMD RECV] 952 Result : Error`
        // 并要求重发（OVERFLOW_INFO），而对成功回包 {0, 返还徽章模板 id} 记 `Result : Ok`。
        internal static byte[] BuildEmblemCollectError(uint status, int relatedItemTemplateId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(EmblemCollectDispatcherArg);
            writer.WriteUInt32(status);
            writer.WriteUInt32((uint)Math.Max(0, relatedItemTemplateId));
            return writer.ToArray();
        }

        // 客户端在"命令的数据没读全"时发 OVERFLOW_INFO(0x00D9)，payload 为 (count, type)；
        // 徽章回收的回包在 3.2 修复前会触发它。应答方式与抽奖开箱同款：用同类型 0x00D9
        // 回一个同形状确认，**不重发 0x03B8 回包**（实测重发只会让客户端继续补发 OVERFLOW_INFO）。
        // 缓存的最近一条回包只用于诊断日志与防环计数，不会重发。
        private const int EmblemCollectOverflowConfirmLimit = 2;
        private static readonly object EmblemCollectAckCacheLock = new object();
        private static readonly Dictionary<Guid, (byte[] Body, DateTime At, int Confirms)> EmblemCollectAckCache =
            new Dictionary<Guid, (byte[], DateTime, int)>();
        private static readonly TimeSpan EmblemCollectAckCacheTtl = TimeSpan.FromMinutes(10);

        private static void RememberEmblemCollectAck(Guid sessionId, byte[] body)
        {
            if (body == null)
                return;

            lock (EmblemCollectAckCacheLock)
            {
                var now = DateTime.UtcNow;
                if (EmblemCollectAckCache.Count > 0)
                {
                    var stale = new List<Guid>();
                    foreach (var pair in EmblemCollectAckCache)
                    {
                        if (now - pair.Value.At > EmblemCollectAckCacheTtl)
                            stale.Add(pair.Key);
                    }

                    foreach (var key in stale)
                        EmblemCollectAckCache.Remove(key);
                }

                EmblemCollectAckCache[sessionId] = (body, now, 0);
            }
        }

        // 返回 true 表示这条 OVERFLOW_INFO 属于徽章回收（已处理或已记录），调用方不应再交给抽奖处理器。
        internal static bool IsEmblemCollectOverflowInfo(byte[] body)
        {
            // (count=1, type=0x03B8)，与抽奖的 01-1B-00 同格式（LotteryItemHandler.IsLotteryOverflowConfirm）。
            return body != null
                && body.Length == 3
                && body[0] == 0x01
                && body[1] == 0xB8
                && body[2] == 0x03;
        }

        internal async Task<bool> TryHandleEmblemCollectOverflowInfo(EnhancedClientSession session, byte[] body)
        {
            if (!IsEmblemCollectOverflowInfo(body))
                return false;

            byte[] cached;
            int confirms;
            lock (EmblemCollectAckCacheLock)
            {
                if (!EmblemCollectAckCache.TryGetValue(session.SessionId, out var entry))
                {
                    FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM: OVERFLOW_INFO but no cached ack recorded");
                    return true;
                }

                cached = entry.Body;
                confirms = entry.Confirms;
                if (confirms >= EmblemCollectOverflowConfirmLimit)
                {
                    EmblemCollectAckCache.Remove(session.SessionId);
                    FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM: OVERFLOW_INFO confirm limit reached, dropping");
                    return true;
                }

                EmblemCollectAckCache[session.SessionId] = (entry.Body, entry.At, confirms + 1);
            }

            // 与抽奖同款握手：客户端发 OVERFLOW_INFO 后，服务端用**同类型 0x00D9**回一个同形状确认
            // （LotteryOverflowConfirmAckBuilder 就是 `01 + requestBody[1..2]`）。回 0x03B8 重发是错的，
            // 客户端会一直补发 OVERFLOW_INFO。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.OVERFLOW_INFO,
                LotteryOverflowConfirmAckBuilder.Build(body)));
            FileLogger.Log($"[{ProtocolName}] EMBLEM_COLLECT_ITEM: OVERFLOW_INFO confirm #{confirms + 1} lastAck({cached.Length}B): {BitConverter.ToString(cached)}");
            return true;
        }
    }

    internal readonly struct EmblemCollectRequest
    {
        public EmblemCollectRequest(short boxSlotIndex, int boxTemplateId, short targetSlotIndex, int targetTemplateId)
        {
            BoxSlotIndex = boxSlotIndex;
            BoxTemplateId = boxTemplateId;
            TargetSlotIndex = targetSlotIndex;
            TargetTemplateId = targetTemplateId;
        }

        public short BoxSlotIndex { get; }

        public int BoxTemplateId { get; }

        public short TargetSlotIndex { get; }

        // 目标物品的模板 id（不是实例 uid）：服务端按「槽位 + 模板 id」定位目标。
        public int TargetTemplateId { get; }
    }
}
