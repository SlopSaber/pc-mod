using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.ViewControllers;
using HMUI;
using Newtonsoft.Json;
using ScoreSaber.Core.Presentation;
using ScoreSaber.Features.MainMenu.MainFlow.Teams;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zenject;

namespace ScoreSaber.Features.MainMenu.MainFlow.Teams.UI {
    [HotReload]
    internal class TeamViewController : BSMLAutomaticViewController {
        private const string TeamUrl = "https://raw.githubusercontent.com/Umbranoxio/ScoreSaber-Team/main/team.json";

        [UIComponent("tab-selector")]
        protected readonly TabSelector _tabSelector = null;

        [UIValue("team-hosts")]
        protected readonly List<object> _teamHosts = new List<object>();

        private Http _http = null;
        private ScoreSaberUIMaterials _materials = null;

        [Inject]
        internal void Construct(Http http, ScoreSaberUIMaterials materials) {
            _http = http;
            _materials = materials;
        }

        [UIAction("#post-parse")]
        protected void Parsed() {

            _tabSelector.transform.localScale *= 0.75f;
        }

        protected override async void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling) {

            if (firstActivation) {

                _teamHosts.Clear();
                var response = await GetTeam();
                if (response.IsPrepared) {
                    foreach (PreparedTeamGroup group in response.Groups) {
                        _teamHosts.Add(TeamToProfileHost(group.Members, group.Name ?? group.Type.ToString()));
                    }
                } else {
                    foreach (KeyValuePair<TeamType, List<TeamMember>> member in response.CallerTeam.TeamMembers) {
                        string teamName = member.Key.ToString();
                        if (teamName == "RT") {
                            teamName = "Ranking Team";
                        }
                        TeamHost host = TeamToProfileHost(member.Value, teamName);
                        _teamHosts.Add(host);
                    }
                }
            }

            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

            _tabSelector.GetTextSegmentedControl().didSelectCellEvent += DidSelect;
            if (_teamHosts.Count > 0) {
                TeamHost host = (TeamHost)_teamHosts[0];
                host.Init();
                foreach (TeamUserInfo profile in host.profiles) {
                    profile.LoadImage();
                }
            }
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling) {
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
            _tabSelector.GetTextSegmentedControl().didSelectCellEvent -= DidSelect;
        }

        private void DidSelect(SegmentedControl segmentedControl, int pos) {

            var teamHost = _teamHosts[pos] as TeamHost;
            teamHost.Init();
            foreach (TeamUserInfo profile in teamHost.profiles) {
                profile.LoadImage();
            }
        }

        private TeamHost TeamToProfileHost(List<TeamMember> team, string teamName) {

            List<TeamUserInfo> host = new List<TeamUserInfo>();
            foreach (TeamMember member in team) {
                host.Add(new TeamUserInfo(_materials, member.ProfilePicture, member.Name, member.Discord, member.GitHub, member.Twitch, member.Twitter, member.YouTube));
            }
            return new TeamHost(teamName, host);
        }

        private TeamHost TeamToProfileHost(PreparedTeamMember[] team, string teamName) {
            var profiles = new List<TeamUserInfo>();
            foreach (PreparedTeamMember member in team) {
                profiles.Add(new TeamUserInfo(_materials, member));
            }
            return new TeamHost(teamName, profiles);
        }

        private async Task<PreparedTeamResponse> GetTeam() {
            string response = await _http.GetRawAsync(TeamUrl);
            if (response == null) {
                throw new System.ArgumentNullException("value");
            }
            if (JsonConvert.DefaultSettings != null) {
                return new PreparedTeamResponse(JsonConvert.DeserializeObject<ScoreSaberTeam>(response));
            }

            JsonSerializer serializer = JsonSerializer.Create();
            serializer.CheckAdditionalContent = true;
            Task<PreparedTeamResponse> preparation;
            if (ExecutionContext.IsFlowSuppressed()) {
                preparation = StartPreparation(response, serializer);
            } else {
                using (ExecutionContext.SuppressFlow()) {
                    preparation = StartPreparation(response, serializer);
                }
            }
            return preparation.GetAwaiter().GetResult();
        }

        private static Task<PreparedTeamResponse> StartPreparation(string response, JsonSerializer serializer) {
            return Task.Factory.StartNew(
                () => PrepareOwned(response, serializer), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        private static PreparedTeamResponse PrepareOwned(string response, JsonSerializer serializer) {
            ScoreSaberTeam team;
            using (var reader = new JsonTextReader(new StringReader(response))) {
                team = serializer.Deserialize<ScoreSaberTeam>(reader);
            }
            if (team?.TeamMembers == null) {
                return new PreparedTeamResponse((PreparedTeamGroup[])null);
            }

            var groups = new List<PreparedTeamGroup>();
            foreach (KeyValuePair<TeamType, List<TeamMember>> group in team.TeamMembers) {
                string name = System.Enum.GetName(typeof(TeamType), group.Key);
                if (name == "RT") {
                    name = "Ranking Team";
                }
                PreparedTeamMember[] members = null;
                if (group.Value != null) {
                    var prepared = new List<PreparedTeamMember>();
                    foreach (TeamMember member in group.Value) {
                        prepared.Add(member == null ? null : new PreparedTeamMember(member));
                    }
                    members = prepared.ToArray();
                }
                groups.Add(new PreparedTeamGroup(group.Key, name, members));
            }
            return new PreparedTeamResponse(groups.ToArray());
        }
    }
}
