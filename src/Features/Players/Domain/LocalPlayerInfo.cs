namespace ScoreSaber.Features.Players.Domain {
    internal class LocalPlayerInfo {
        private readonly string _preparedFriendIds;
        private readonly int? _preparedFriendCount;

        internal string playerId { get; set; }
        internal string playerName { get; set; }
        internal string playerKey { get; set; }
        internal string playerFriends { get; set; }
        internal string playerNonce { get; set; }
        internal string authType { get; set; }
        internal LocalPlayerInfo(string playerId, string playerName, string playerFriends, string authType, string playerNonce) {

            this.playerId = playerId;
            this.playerName = playerName;
            this.playerFriends = playerFriends;
            this.authType = authType;
            this.playerNonce = playerNonce;
        }

        internal LocalPlayerInfo(string playerId, string playerName, string playerFriends, string authType, string playerNonce, int preparedFriendCount)
            : this(playerId, playerName, playerFriends, authType, playerNonce) {
            _preparedFriendIds = playerFriends;
            _preparedFriendCount = preparedFriendCount;
        }

        internal bool TryGetPreparedFriendCount(out int count) {
            count = _preparedFriendCount.GetValueOrDefault();
            return _preparedFriendCount.HasValue && object.ReferenceEquals(playerFriends, _preparedFriendIds);
        }

    }
}
