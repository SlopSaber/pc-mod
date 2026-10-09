using ScoreSaber.Features.Live.Ludus.Services;
using ScoreSaber.Features.Live.Protocol;

namespace ScoreSaber.Features.Live.Ludus.Packets.Handlers {
    internal sealed class ChatMessageEnvelopeHandler<TSession> : ILudusEnvelopeHandler<TSession>
        where TSession : ILudusSessionPacketContext {
        private readonly LudusChatMessageBuffer _messages;

        internal ChatMessageEnvelopeHandler(LudusChatMessageBuffer messages) {
            _messages = messages;
        }

        public LudusEnvelopeType Type => LudusEnvelopeType.ChatMessage;

        public void Handle(TSession session, DecodedLudusEnvelope envelope) {
            bool changed;
            try {
                changed = _messages.Apply(envelope.ChatMessage, session.CurrentLudusMatchId, envelope.PreparedChatMessageMatch);
            } finally {
                if (envelope != null) {
                    envelope.PreparedChatMessageMatch = null;
                }
            }
            if (changed) {
                session.NotifyChatMessagesChanged(_messages.MessagesFor(session.CurrentLudusMatchId));
            }
        }
    }
}
