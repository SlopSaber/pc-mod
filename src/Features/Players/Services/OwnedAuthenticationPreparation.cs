using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Players.Services {

    internal static class OwnedAuthenticationPreparation {

        internal static Task<PreparedFriends> PrepareFriends(IReadOnlyList<string> source) {
            if (source == null || (source.GetType() != typeof(List<string>) && source.GetType() != typeof(string[]))) {
                string friends = string.Join(",", source.Where(value => value != "0"));
                return Task.FromResult(new PreparedFriends(friends, null));
            }

            string[] owned = new string[source.Count];
            int index = 0;
            foreach (string friend in source) {
                owned[index++] = friend;
            }
            return ReplayStorageService.QueueOwnedPreparation(() => {
                string friends = string.Join(",", owned.Where(value => value != "0"));
                int count = string.IsNullOrEmpty(friends) ? 0 : friends.Split(',').Length;
                return new PreparedFriends(friends, count);
            });
        }

        internal static Task<string> PrepareTicket(byte[] source, int count) {
            byte[] owned = new byte[count];
            Buffer.BlockCopy(source, 0, owned, 0, count);
            return ReplayStorageService.QueueOwnedPreparation(() => BitConverter.ToString(owned).Replace("-", ""));
        }

        internal sealed class PreparedFriends {
            internal readonly string Text;
            internal readonly int? Count;

            internal PreparedFriends(string text, int? count) {
                Text = text;
                Count = count;
            }
        }
    }
}
