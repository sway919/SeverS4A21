using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.SelfTests
{
    public static class SystemChatNoticeSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== system chat notice ===");
            var failures = 0;

            var gold = SystemChatNotice.Build(SystemChatNotice.FormatGold(1500));
            Check("system notice mode is the client system-window branch",
                gold.Length > 5 && gold[0] == SystemChatNotice.SystemWindowMode, ref failures);
            Check("gold line round-trips as client text",
                ReadText(gold) == "获得了 1500 金币", ref failures);

            var exp = SystemChatNotice.Build(SystemChatNotice.FormatExperience(20));
            Check("experience line is a separate system notice",
                ReadText(exp) == "获得了 20 经验值" && exp[0] == 2, ref failures);

            var item = SystemChatNotice.Build(SystemChatNotice.FormatItem("  布衣 "));
            Check("item line keeps the resolved name",
                ReadText(item) == "获得了 布衣", ref failures);
            Check("missing item name stays generic",
                SystemChatNotice.FormatItem(null) == "获得了 物品", ref failures);

            var area = ChatHandler.BuildNotificationBody(3, 9, 0, ClientTextEncoding.GetBytes("你好"));
            var party = ChatHandler.BuildNotificationBody(2, 9, 0, ClientTextEncoding.GetBytes("集合"));
            var guild = ChatHandler.BuildGuildNotificationBody(
                ClientTextEncoding.GetBytes("赛丽亚"),
                ClientTextEncoding.GetBytes("公会"));
            Check("area chat keeps the normal-tab mode", area[0] == 3, ref failures);
            Check("party chat keeps the party-tab mode", party[0] == 2, ref failures);
            Check("guild chat keeps the guild-tab mode", guild[0] == 6, ref failures);
            Check("system text and party chat use different packets",
                (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE
                    != (ushort)NotiPacketTypeA21.MESSAGE, ref failures);

            Console.WriteLine(failures == 0
                ? "system chat notice passed"
                : "system chat notice failures=" + failures);
            return failures;
        }

        private static string ReadText(byte[] body)
        {
            var length = BitConverter.ToInt32(body, 1);
            return ClientTextEncoding.GetString(body, 5, length);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            if (ok)
                return;
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }
}
