using ScoreSaber.Features.Live.Ludus.Services;
using ScoreSaber.Features.Live.Protocol;

namespace ScoreSaber.Features.Live.Ludus.Packets.Handlers {
    internal sealed class ChatSnapshotEnvelopeHandler<TSession> : ILudusEnvelopeHandler<TSession>
        where TSession : ILudusSessionPacketContext {
        private readonly LudusChatMessageBuffer _messages;

        internal ChatSnapshotEnvelopeHandler(LudusChatMessageBuffer messages) {
            _messages = messages;
        }

        public LudusEnvelopeType Type => LudusEnvelopeType.ChatSnapshot;

        public void Handle(TSession session, DecodedLudusEnvelope envelope) {
            try {
                _messages.Replace(envelope.ChatSnapshot, session.CurrentLudusMatchId, envelope.PreparedChatKeys);
            } finally {
                envelope.PreparedChatKeys = null;
            }
            session.NotifyChatMessagesChanged(_messages.MessagesFor(session.CurrentLudusMatchId));
        }
    }
}
