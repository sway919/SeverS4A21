using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    /// <summary>
    /// 系统栏文本。客户端 011838C0 按首字节分支；mode 2（011839A2）读一条 DSTR
    /// 后调用 01A62DF0，把文本送进系统聊天槽。玩家聊天仍走 NOTI MESSAGE 的模式字节：
    /// 1 悄悄话、2 队伍、3 普通、6 公会、7 另一路悄悄话。
    /// </summary>
    public static class SystemChatNotice
    {
        public const byte SystemWindowMode = 2;

        public static byte[] Build(string message)
            => ServerNoticeMessageBuilder.Build(message ?? string.Empty, SystemWindowMode);

        public static string FormatGold(long amount)
            => "获得了 " + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 金币";

        public static string FormatExperience(long amount)
            => "获得了 " + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 经验值";

        public static string FormatItem(string itemName)
        {
            var name = string.IsNullOrWhiteSpace(itemName) ? "物品" : itemName.Trim();
            return "获得了 " + name;
        }

        public static string ResolveItemName(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return "物品";

            try
            {
                var stackableName = Clean(StackableItemProvider.Load(itemTemplateId)?.Name);
                if (stackableName != null)
                    return stackableName;

                if (ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment))
                {
                    var equipmentName = Clean(equipment?.Name);
                    if (equipmentName != null)
                        return equipmentName;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[SystemChatNotice] item name lookup failed id=" +
                    itemTemplateId + " " + ex.Message);
            }

            return "物品";
        }

        private static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var trimmed = name.Trim('`', ' ', '\t', '\r', '\n');
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
