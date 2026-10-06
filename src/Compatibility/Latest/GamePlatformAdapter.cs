#nullable enable

using Legato.Platform.Authentication;
using Legato.Platform.Friends;
using Legato.Platform.Users;
using OculusStudios.Platform.Core;
using Steamworks;
using ScoreSaber.Features.Players.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Legato.Platform {
    public class GamePlatformAdapter : IPlatformUserProvider, IPlatformAuthenticationProvider, IPlatformFriendsProvider, IDisposable {
        private readonly IPlatform _platform;
        private readonly CancellationTokenSource _lifetime = new();
        private HAuthTicket _steamTicket = HAuthTicket.Invalid;
        private Task<string>? _steamTicketTask;

        public GamePlatformAdapter(IPlatform platform) => _platform = platform;

        public Task<UserInfo> GetUserInfo(CancellationToken _) {
            UserInfo.Platform platform;
            switch (_platform.key) {
                case "steam":
                    platform = UserInfo.Platform.Steam;
                    break;
                case "oculus":
                case "oculus-mock":
                    platform = UserInfo.Platform.Oculus;
                    break;
                default:
                    platform = UserInfo.Platform.Test;
                    break;
            }

            return Task.FromResult(new UserInfo(platform, _platform.user.userId.ToString(), _platform.user.displayName));
        }

        public Task<string> GetAuthToken() {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_platform.key != "steam") return _platform.user.GetAccessTokenAsync();
            if (_steamTicketTask == null || _steamTicketTask.IsFaulted || _steamTicketTask.IsCanceled) {
                _steamTicketTask = GetSteamTicket();
            }
            return _steamTicketTask;
        }

        private async Task<string> GetSteamTicket() {
            var completion = new TaskCompletionSource<Task<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> preparedTicket = completion.Task.Unwrap();
            var ticket = HAuthTicket.Invalid;
            using var callback = Callback<GetTicketForWebApiResponse_t>.Create(response => {
                if (response.m_hAuthTicket != ticket || completion.Task.IsCompleted) return;
                if (response.m_eResult != EResult.k_EResultOK || response.m_rgubTicket == null ||
                    response.m_cubTicket <= 0 || response.m_cubTicket > response.m_rgubTicket.Length) {
                    completion.TrySetException(new InvalidOperationException($"Steam ticket request failed: {response.m_eResult}"));
                    return;
                }
                completion.TrySetResult(OwnedAuthenticationPreparation.PrepareTicket(response.m_rgubTicket, response.m_cubTicket));
            });
            try {
                // The game's access token is restricted to oculus-xplat-backend.
                // ScoreSaber needs its own Web API ticket, without that recipient.
                ticket = SteamUser.GetAuthTicketForWebApi(string.Empty);
                if (ticket == HAuthTicket.Invalid) throw new InvalidOperationException("Steam did not issue an authentication ticket");
                _steamTicket = ticket;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var delay = Task.Delay(TimeSpan.FromSeconds(15), timeout.Token);
                if (await Task.WhenAny(preparedTicket, delay) != preparedTicket) {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Steam authentication ticket timed out");
                }
                timeout.Cancel();
                _lifetime.Token.ThrowIfCancellationRequested();
                return await preparedTicket;
            } catch {
                ReleaseSteamTicket();
                throw;
            }
        }

        private void ReleaseSteamTicket() {
            if (_steamTicket == HAuthTicket.Invalid) return;
            try {
                SteamUser.CancelAuthTicket(_steamTicket);
            } catch (InvalidOperationException exception) when (exception.Message == "Steamworks is not initialized.") {
                // Steam has already invalidated its tickets during shutdown.
            } finally {
                _steamTicket = HAuthTicket.Invalid;
            }
        }

        public void Dispose() {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            // Keep the ticket valid until the server has verified it, not just
            // until Steam supplies the bytes to GetAuthToken.
            ReleaseSteamTicket();
        }

        public Task<string> GetCrossPlatformAccessToken(CancellationToken _) => _platform.user.GetXPlatformAccessTokenAsync(false);

        public Task<IReadOnlyList<string>> GetFriendUserIds() =>
            Task.FromResult(_platform.key == "steam" ? SteamFriendUserIds() : (IReadOnlyList<string>)new string[0]);

        private static IReadOnlyList<string> SteamFriendUserIds() {
            int friendCount = Steamworks.SteamFriends.GetFriendCount(Steamworks.EFriendFlags.k_EFriendFlagAll);
            var ids = new List<string>(friendCount);
            for (int i = 0; i < friendCount; i++) {
                ids.Add(Steamworks.SteamFriends.GetFriendByIndex(i, Steamworks.EFriendFlags.k_EFriendFlagImmediate).m_SteamID.ToString());
            }
            return ids;
        }
    }
}
