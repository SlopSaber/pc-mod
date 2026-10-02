using ScoreSaber.Core.Timing;
using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Live.Protocol;
using ScoreSaber.Features.Players.Domain;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;

namespace ScoreSaber.Features.Live.Ludus.Packets {
    internal sealed class LudusPacketSender {
        private readonly Func<Func<byte[]>, bool> _sendDeferred;
        private readonly ScoreSaberClock _clock;
        private ulong _outgoingSequence = 1;

        internal LudusPacketSender(Func<Func<byte[]>, bool> sendDeferred, ScoreSaberClock clock) {
            _sendDeferred = sendDeferred;
            _clock = clock;
        }

        internal ulong LastReceivedSequence { get; set; }

        internal void ResetSequences() {
            _outgoingSequence = 1;
            LastReceivedSequence = 0;
        }

        internal void Connect(
            GameSession session,
            LivePlayerPlatform platform,
            string gameVersion,
            string clientVersion,
            LudusRoomContextType initialRoomContext,
            bool publicLivePresenceOptOut,
            List<LiveMod> mods) {

            string sessionId = session.SessionId;
            string sessionKey = session.SessionKey;
            string playerId = session.PlayerId;
            List<LiveMod> ownedMods = CopyMods(mods);
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeConnect(
                string.Empty,
                sessionId,
                sessionKey,
                string.Empty,
                playerId,
                platform,
                gameVersion,
                clientVersion,
                initialRoomContext,
                publicLivePresenceOptOut,
                ownedMods,
                clientTimeUnixMs,
                sequence));
        }

        internal void Heartbeat(string connectionId) {
            ulong lastReceivedSequence = LastReceivedSequence;
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeHeartbeat(lastReceivedSequence, clientTimeUnixMs, sequence, connectionId));
        }

        internal void SetRoomContext(LudusRoomContextType roomContext, string tournamentId, List<LiveMod> mods, string connectionId) {
            List<LiveMod> ownedMods = CopyMods(mods);
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeSetRoomContext(roomContext, tournamentId, ownedMods, clientTimeUnixMs, sequence, connectionId));
        }

        internal void SetClientType(LudusClientType clientType, string connectionId) {
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeSetClientType(clientType, clientTimeUnixMs, sequence, connectionId));
        }

        internal void JoinRoom(string matchId, List<LiveMod> mods, string connectionId) {
            List<LiveMod> ownedMods = CopyMods(mods);
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeJoinRoom(matchId, string.Empty, ownedMods, clientTimeUnixMs, sequence, connectionId));
        }

        internal void ReadyState(string matchId, bool ready, string connectionId) {
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeReadyState(matchId, ready, clientTimeUnixMs, sequence, connectionId));
        }

        internal void DownloadState(string matchId, LudusDownloadState state, string errorMessage, string connectionId) {
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeDownloadState(matchId, state, errorMessage, clientTimeUnixMs, sequence, connectionId));
        }

        internal void PromptResponse(CompeteOrganizerPrompt prompt, string matchId, string playerId, bool accepted, string connectionId) {
            string commandId = prompt.CommandId;
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodePromptResponse(commandId, matchId, playerId, accepted, clientTimeUnixMs, sequence, connectionId));
        }

        internal void ChatMessage(string matchId, string text, string senderDisplayName, string connectionId) {
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodeChatMessage(matchId, text, senderDisplayName, clientTimeUnixMs, sequence, connectionId));
        }

        internal void Presence(LudusPlayState playState, LudusDownloadState downloadState, string currentMatchId, string currentMapHash, string connectionId) {
            long clientTimeUnixMs = NowUnixMs();
            ulong sequence = NextSequence();
            _sendDeferred(() => LudusProto.EncodePresence(playState, downloadState, currentMatchId, currentMapHash, clientTimeUnixMs, sequence, connectionId));
        }

        internal bool ReplayPacket(ReplayStreamPacket packet, string connectionId) {
            ulong sequence = _outgoingSequence;
            long clientTimeUnixMs = NowUnixMs();
            if (!_sendDeferred(() => LudusProto.EncodeReplayPacket(packet, clientTimeUnixMs, sequence, connectionId))) {
                return false;
            }

            _outgoingSequence++;
            return true;
        }

        private ulong NextSequence() => _outgoingSequence++;

        private static List<LiveMod> CopyMods(List<LiveMod> mods) {
            if (mods == null) {
                return null;
            }
            var ownedMods = new List<LiveMod>(mods.Count);
            foreach (LiveMod mod in mods) {
                ownedMods.Add(mod == null ? null : new LiveMod { Id = mod.Id, Version = mod.Version });
            }
            return ownedMods;
        }

        private long NowUnixMs() => _clock.UnixTimeMilliseconds();
    }
}
