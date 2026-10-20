using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Handlers;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class EmblemCollectItemSelfTest
    {
        // 实机抓包 body（26B）。客户端只写 +0x0E 起的 12 字节，前 14 字节是栈残留，
        // 真实字段为：+0x0E u16 箱子槽位 | +0x10 u32 箱子模板 | +0x14 u16 目标槽位 | +0x16 u32 目标模板。
        // 帧 1 来自 2026-10-10（残留值与真实箱位/槽位巧合一致，曾被误当成布局依据）；
        // 帧 2-4 来自 2026-10-12（残留与真实值不一致，字段值与 inventory.db 的槽位/模板逐项吻合）。
        private static readonly (string Name, byte[] Body, short BoxSlot, int BoxTemplateId, short TargetSlot, int TargetTemplateId)[] CapturedFrames =
        {
            ("2026-10-10", new byte[]
            {
                0x04, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00,
                0x04, 0x00, 0x56, 0xF0, 0x28, 0x00, 0x09, 0x00, 0x8C, 0xDE, 0xF7, 0x05,
            }, 4, 0x0028F056, 9, 0x05F7DE8C),
            ("2026-10-12 白金箱", new byte[]
            {
                0x04, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0C, 0x00,
                0x6E, 0x00, 0x54, 0xF0, 0x28, 0x00, 0x0C, 0x00, 0x02, 0x21, 0xFB, 0x05,
            }, 110, 2682964, 12, 100344066),
            ("2026-10-12 华丽箱", new byte[]
            {
                0x04, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x13, 0x00,
                0x70, 0x00, 0x53, 0xF0, 0x28, 0x00, 0x13, 0x00, 0x48, 0xE8, 0x01, 0x00,
            }, 112, 2682963, 19, 125000),
            ("2026-10-12 灿烂箱", new byte[]
            {
                0x04, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x13, 0x00,
                0x6F, 0x00, 0x56, 0xF0, 0x28, 0x00, 0x13, 0x00, 0x48, 0xE8, 0x01, 0x00,
            }, 111, 2682966, 19, 125000),
        };

        private const int BrilliantBoxItemId = 2682966;  // 灿烂的徽章回收箱, [emblem collector] 3
        private const int GorgeousBoxItemId = 2682963;   // 华丽的徽章回收箱, [emblem collector] 2
        private const int PlatinumBoxItemId = 2682964;   // 白金的徽章回收箱, [emblem collector] 4
        private const int BrilliantEmblemId1 = 2520035;  // 灿烂的绿色徽章 [物理暴击] grade 3
        private const int BrilliantEmblemId2 = 2520037;  // 灿烂的绿色徽章 [物理暴击] grade 3
        private const int GorgeousEmblemId1 = 2520020;   // 华丽的绿色徽章 [物理暴击] grade 2
        private const int GorgeousEmblemId2 = 2520021;   // 华丽的绿色徽章 [物理暴击] grade 2
        private const int PlatinumEmblemItemId = 2540006; // 白金徽章 [鬼斩+上挑] grade 4, [avatar emblem target type]=[S socket]
        private const int EquipmentItemId = 27850;
        // 丝绒臂章: PVF equipment/character/common/support/support_440001.equ, [equipment type]=[support]。
        // 辅助装备/魔法石只有 1 个徽章孔(IsSingleMiddleEquipmentSocket), 白金(S socket)徽章镶在此类装备上。
        private const int SupportEquipmentItemId = 440001;
        private const int FillerItemId = 2682963;        // 华丽回收箱, 仅用于填满背包的占位堆叠物
        private const short BoxSlot = 4;
        private const short EquipmentSlot = 9;

        public static int Run()
        {
            Console.WriteLine("=== EMBLEM_COLLECT_ITEM selftest ===");
            var failures = 0;

            VerifyRequestParsing(ref failures);
            VerifyResponseBodies(ref failures);
            VerifyMutationRules(ref failures);

            Console.WriteLine(failures == 0
                ? "EMBLEM_COLLECT_ITEM selftest passed"
                : $"EMBLEM_COLLECT_ITEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRequestParsing(ref int failures)
        {
            foreach (var (name, body, boxSlot, boxTemplateId, targetSlot, targetTemplateId) in CapturedFrames)
            {
                Check(
                    $"0x03B8 request parses the captured body ({name})",
                    InventoryHandler.TryParseEmblemCollectBody(body, out var request)
                    && request.BoxSlotIndex == boxSlot
                    && request.BoxTemplateId == boxTemplateId
                    && request.TargetSlotIndex == targetSlot
                    && request.TargetTemplateId == targetTemplateId,
                    ref failures);
            }

            // 回归：客户端构造点从不写前 14 字节（栈残留），它们取任何值都不得影响解析。
            var arbitraryLeading = (byte[])CapturedFrames[2].Body.Clone();
            for (var i = 0; i < 14; i++)
                arbitraryLeading[i] = 0xFF;
            Check(
                "0x03B8 request ignores the leading 14 bytes",
                InventoryHandler.TryParseEmblemCollectBody(arbitraryLeading, out var arbitrary)
                && arbitrary.BoxSlotIndex == 112
                && arbitrary.BoxTemplateId == 2682963
                && arbitrary.TargetSlotIndex == 19
                && arbitrary.TargetTemplateId == 125000,
                ref failures);

            Check(
                "0x03B8 request rejects a short body",
                !InventoryHandler.TryParseEmblemCollectBody(new byte[25], out _),
                ref failures);

            Check(
                "0x03B8 request rejects null body",
                !InventoryHandler.TryParseEmblemCollectBody(null, out _),
                ref failures);

            var zeroBoxTemplate = (byte[])CapturedFrames[2].Body.Clone();
            Array.Clear(zeroBoxTemplate, 0x10, 4);
            Check(
                "0x03B8 request rejects a zero box template id",
                !InventoryHandler.TryParseEmblemCollectBody(zeroBoxTemplate, out _),
                ref failures);

            var zeroTargetTemplate = (byte[])CapturedFrames[2].Body.Clone();
            Array.Clear(zeroTargetTemplate, 0x16, 4);
            Check(
                "0x03B8 request rejects a zero target template id",
                !InventoryHandler.TryParseEmblemCollectBody(zeroTargetTemplate, out _),
                ref failures);
        }

        private static void VerifyResponseBodies(ref int failures)
        {
            // 客户端处理器 VA 0x125A9F0 读满 8 字节：u32 status + u32 返还的徽章模板 id。
            // 客户端派发器先吃掉 body 的第 1 字节（作为处理器的 arg2），handler 从 body+1 读 8 字节。
            var okAck = InventoryHandler.BuildEmblemCollectAck(125000);
            Check(
                "success ack is 9 bytes: dispatcher arg + status 0 + returned emblem template id",
                okAck.Length == 9
                && okAck[0] != 0
                && BitConverter.ToUInt32(okAck, 1) == 0
                && BitConverter.ToUInt32(okAck, 5) == 125000,
                ref failures);

            var errorAck = InventoryHandler.BuildEmblemCollectError(4, 125000);
            Check(
                "error ack is 9 bytes: dispatcher arg + status + a valid item id",
                errorAck.Length == 9
                && errorAck[0] != 0
                && BitConverter.ToUInt32(errorAck, 1) == 4
                && BitConverter.ToUInt32(errorAck, 5) == 125000,
                ref failures);

            var windowReset = InventoryHandler.BuildEmblemCollectWindowReset();
            Check(
                "window reset ack is 9 bytes: status 0 + returned id -1 (reset without item display)",
                windowReset.Length == 9
                && windowReset[0] != 0
                && BitConverter.ToUInt32(windowReset, 1) == 0
                && BitConverter.ToUInt32(windowReset, 5) == 0xFFFFFFFF,
                ref failures);

            // 客户端在"命令数据没读全"时发 OVERFLOW_INFO (count=1, type)，实测失败回包后必发 01-B8-03。
            Check(
                "overflow report 01-B8-03 is routed to the emblem collect resend path",
                InventoryHandler.IsEmblemCollectOverflowInfo(new byte[] { 0x01, 0xB8, 0x03 })
                && !InventoryHandler.IsEmblemCollectOverflowInfo(new byte[] { 0x01, 0x1B, 0x00 })
                && !InventoryHandler.IsEmblemCollectOverflowInfo(new byte[] { 0x01, 0xB8 })
                && !InventoryHandler.IsEmblemCollectOverflowInfo(null),
                ref failures);
        }

        private static void VerifyMutationRules(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("emblem collect mutation checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            VerifyBrilliantCollect(ref failures);
            VerifyGorgeousCollect(ref failures);
            VerifyPlatinumCollect(ref failures);
            VerifyEquippedTargetCollect(ref failures);
            VerifyRejections(ref failures);
        }

        private static void VerifyBrilliantCollect(ref int failures)
        {
            var inventory = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                BrilliantBoxItemId,
                EquipmentSlot,
                EquipmentItemId,
                out var result);

            var returnedSlot = FindMainSlot(inventory, BrilliantEmblemId1);
            Check(
                "brilliant box returns one grade-3 emblem, destroys the other and consumes the box",
                ok
                && result != null
                && result.ReturnedEmblemTemplateId == BrilliantEmblemId1
                && result.DestroyedEmblemTemplateId == BrilliantEmblemId2
                && result.ReturnedEmblemSlotIndex == returnedSlot
                && result.BoxRemainingCount == 0
                && returnedSlot >= 0
                && inventory.GetItem(InventoryListType.Main, returnedSlot)?.Count == 1
                && inventory.GetItem(InventoryListType.Main, BoxSlot) == null,
                ref failures);

            var collected = inventory.GetItem(InventoryListType.Main, EquipmentSlot);
            Check(
                "emblem collect keeps the open socket count and marks both sockets empty",
                collected != null
                && collected.EmblemSocketCount == 2
                && collected.EmblemId1 == -1
                && collected.EmblemId2 == -1,
                ref failures);
        }

        private static void VerifyGorgeousCollect(ref int failures)
        {
            var inventory = CreateCollectInventory(GorgeousBoxItemId, GorgeousEmblemId1, GorgeousEmblemId2);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                GorgeousBoxItemId,
                EquipmentSlot,
                EquipmentItemId,
                out var result);

            Check(
                "gorgeous box returns one grade-2 emblem",
                ok
                && result != null
                && result.ReturnedEmblemTemplateId == GorgeousEmblemId1
                && result.DestroyedEmblemTemplateId == GorgeousEmblemId2
                && FindMainSlot(inventory, GorgeousEmblemId1) >= 0
                && inventory.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == -1
                && inventory.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId2 == -1,
                ref failures);
        }

        private static void VerifyPlatinumCollect(ref int failures)
        {
            var inventory = new InventoryService(characterId: 92021, accountId: 92020);
            var box = ItemCore.Create(ItemCore.KindConsumable, PlatinumBoxItemId);
            box.Count = 1;
            inventory.SetItem(InventoryListType.Main, BoxSlot, box);

            var support = ItemCore.Create(ItemCore.KindEquipment, SupportEquipmentItemId);
            support.EmblemSocketCount = 1;
            support.EmblemId1 = PlatinumEmblemItemId;
            support.EmblemId2 = 0;
            inventory.SetItem(InventoryListType.Main, EquipmentSlot, support);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                PlatinumBoxItemId,
                EquipmentSlot,
                SupportEquipmentItemId,
                out var result);

            var returnedSlot = FindMainSlot(inventory, PlatinumEmblemItemId);
            Check(
                "platinum box collects the single support emblem and destroys nothing",
                ok
                && result != null
                && result.ReturnedEmblemTemplateId == PlatinumEmblemItemId
                && result.DestroyedEmblemTemplateId == 0
                && result.ReturnedEmblemSlotIndex == returnedSlot
                && result.BoxRemainingCount == 0
                && returnedSlot >= 0
                && inventory.GetItem(InventoryListType.Main, returnedSlot)?.Count == 1
                && inventory.GetItem(InventoryListType.Main, BoxSlot) == null,
                ref failures);

            var collected = inventory.GetItem(InventoryListType.Main, EquipmentSlot);
            Check(
                "single-socket collect keeps one open socket, Id1=-1 and Id2=0",
                collected != null
                && collected.EmblemSocketCount == 1
                && collected.EmblemId1 == -1
                && collected.EmblemId2 == 0,
                ref failures);
        }

        private static void VerifyEquippedTargetCollect(ref int failures)
        {
            var inventory = new InventoryService(characterId: 92011, accountId: 92010);
            var box = ItemCore.Create(ItemCore.KindConsumable, BrilliantBoxItemId);
            box.Count = 1;
            inventory.SetItem(InventoryListType.Main, BoxSlot, box);

            var equipped = ItemCore.Create(ItemCore.KindEquipment, EquipmentItemId);
            equipped.EmblemSocketCount = 2;
            equipped.EmblemId1 = BrilliantEmblemId1;
            equipped.EmblemId2 = BrilliantEmblemId2;
            var weaponSlot = (short)EquipmentType.Weapon;
            inventory.SetItem(InventoryListType.Equipment, weaponSlot, equipped);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                BrilliantBoxItemId,
                weaponSlot,
                EquipmentItemId,
                out var result);

            Check(
                "emblem collect supports an equipped target and reports the equipment list",
                ok
                && result != null
                && result.TargetEquipped
                && result.TargetListType == InventoryListType.Equipment
                && inventory.GetItem(InventoryListType.Equipment, weaponSlot)?.EmblemId1 == -1
                && inventory.GetItem(InventoryListType.Equipment, weaponSlot)?.EmblemId2 == -1,
                ref failures);
        }

        private static void VerifyRejections(ref int failures)
        {
            var gradeMismatch = CreateCollectInventory(GorgeousBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "grade-2 box rejects grade-3 emblems without mutating state",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    gradeMismatch,
                    BoxSlot,
                    GorgeousBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && gradeMismatch.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && gradeMismatch.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1,
                ref failures);

            var singleEmblem = CreateCollectInventory(BrilliantBoxItemId, GorgeousEmblemId1, 0);
            Check(
                "single emblem whose grade is outside the box parameter set is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    singleEmblem,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && singleEmblem.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && singleEmblem.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == GorgeousEmblemId1,
                ref failures);

            var brilliantOnPlatinum = new InventoryService(characterId: 92031, accountId: 92030);
            var brilliantBox = ItemCore.Create(ItemCore.KindConsumable, BrilliantBoxItemId);
            brilliantBox.Count = 1;
            brilliantOnPlatinum.SetItem(InventoryListType.Main, BoxSlot, brilliantBox);
            var supportWithPlatinum = ItemCore.Create(ItemCore.KindEquipment, SupportEquipmentItemId);
            supportWithPlatinum.EmblemSocketCount = 1;
            supportWithPlatinum.EmblemId1 = PlatinumEmblemItemId;
            brilliantOnPlatinum.SetItem(InventoryListType.Main, EquipmentSlot, supportWithPlatinum);
            Check(
                "brilliant box rejects a single grade-4 platinum emblem without mutating state",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    brilliantOnPlatinum,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    SupportEquipmentItemId,
                    out _)
                && brilliantOnPlatinum.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && brilliantOnPlatinum.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == PlatinumEmblemItemId,
                ref failures);

            var platinumOnBrilliant = CreateCollectInventory(PlatinumBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "platinum box rejects grade-3 emblems on a two-socket equipment",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    platinumOnBrilliant,
                    BoxSlot,
                    PlatinumBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && platinumOnBrilliant.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && platinumOnBrilliant.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1,
                ref failures);

            // 两孔装备只镶 1 颗（等级与箱子匹配）也必须拒绝：客户端补丁跳过了"每个孔都要有徽章"，
            // 服务端必须自己保证；单孔装备（白金徽章的辅助装备/魔法石）仍允许 1 颗。
            var halfSocketed = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, 0);
            Check(
                "two-socket equipment with only one matching emblem is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    halfSocketed,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && halfSocketed.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && halfSocketed.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1,
                ref failures);

            var emptyBoxSlot = CreateCollectInventory(0, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "missing box in the declared slot is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    emptyBoxSlot,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _),
                ref failures);

            var wrongBoxTemplate = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "box template mismatch is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    wrongBoxTemplate,
                    BoxSlot,
                    GorgeousBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _),
                ref failures);

            var wrongTargetTemplate = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "target template mismatch is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    wrongTargetTemplate,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId + 1,
                    out _),
                ref failures);

            var fullBackpack = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            FillRemainingMainSlots(fullBackpack);
            Check(
                "a full backpack rejects the collect without consuming the box or clearing emblems",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    fullBackpack,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && fullBackpack.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && fullBackpack.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1
                && fullBackpack.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId2 == BrilliantEmblemId2,
                ref failures);

            // 拒绝原因 → 客户端 status: 4=未镶满, 2=两颗徽章等级不同, 1=等级都不符合箱子参数集合。
            var notFilled = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, 0);
            Check(
                "a two-socket equipment with one matching emblem reports failure status 4",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    notFilled,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _,
                    out var notFilledStatus)
                && notFilledStatus == InventoryEquipmentMutationService.EmblemCollectFailureNotFilled,
                ref failures);

            var gradeMismatchPair = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, GorgeousEmblemId1);
            Check(
                "a grade-3 box with a grade-3 and a grade-2 emblem reports failure status 2",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    gradeMismatchPair,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _,
                    out var gradeMismatchStatus)
                && gradeMismatchStatus == InventoryEquipmentMutationService.EmblemCollectFailureGradeMismatch,
                ref failures);

            var wrongGrades = CreateCollectInventory(BrilliantBoxItemId, GorgeousEmblemId1, GorgeousEmblemId2);
            Check(
                "a grade-3 box with two grade-2 emblems reports failure status 1",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    wrongGrades,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _,
                    out var wrongGradesStatus)
                && wrongGradesStatus == InventoryEquipmentMutationService.EmblemCollectFailureNotCollectable,
                ref failures);
        }

        private static void FillRemainingMainSlots(InventoryService inventory)
        {
            var filler = ItemCore.Create(ItemCore.KindConsumable, FillerItemId);
            filler.Count = 1;
            for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
            {
                if (slot == BoxSlot || slot == EquipmentSlot)
                    continue;

                inventory.SetItem(InventoryListType.Main, slot, filler.Copy());
            }
        }

        private static InventoryService CreateCollectInventory(int boxItemId, int emblemId1, int emblemId2)
        {
            var inventory = new InventoryService(characterId: 92001, accountId: 92000);
            if (boxItemId > 0)
            {
                var box = ItemCore.Create(ItemCore.KindConsumable, boxItemId);
                box.Count = 1;
                inventory.SetItem(InventoryListType.Main, BoxSlot, box);
            }

            var equipment = ItemCore.Create(ItemCore.KindEquipment, EquipmentItemId);
            equipment.EmblemSocketCount = 2;
            equipment.EmblemId1 = emblemId1;
            equipment.EmblemId2 = emblemId2;
            inventory.SetItem(InventoryListType.Main, EquipmentSlot, equipment);
            return inventory;
        }

        private static short FindMainSlot(InventoryService inventory, int itemTemplateId)
        {
            for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
            {
                var item = inventory.GetItem(InventoryListType.Main, slot);
                if (item != null && item.ItemId == itemTemplateId && item.Count > 0)
                    return slot;
            }

            return -1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
