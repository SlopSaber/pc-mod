#pragma warning disable IDE1006
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

namespace ScoreSaber.Features.MainMenu.MainFlow.Teams {

    internal class ScoreSaberTeam {
        [JsonProperty("TeamMembers")]
        public Dictionary<TeamType, List<TeamMember>> TeamMembers { get; set; }
    }

    internal class TeamMember {
        [JsonProperty("Name")]
        internal string Name { get; set; }
        [JsonProperty("ProfilePicture")]
        internal string ProfilePicture { get; set; }
        [JsonProperty("Discord")]
        internal string Discord { get; set; }
        [JsonProperty("GitHub")]
        internal string GitHub { get; set; }
        [JsonProperty("Twitch")]
        internal string Twitch { get; set; }
        [JsonProperty("Twitter")]
        internal string Twitter { get; set; }
        [JsonProperty("YouTube")]
        internal string YouTube { get; set; }
    }

    internal sealed class PreparedTeamResponse {
        internal readonly bool IsPrepared;
        internal readonly ScoreSaberTeam CallerTeam;
        internal readonly PreparedTeamGroup[] Groups;

        internal PreparedTeamResponse(ScoreSaberTeam callerTeam) {
            CallerTeam = callerTeam;
        }

        internal PreparedTeamResponse(PreparedTeamGroup[] groups) {
            IsPrepared = true;
            Groups = groups;
        }
    }

    internal sealed class PreparedTeamGroup {
        internal readonly TeamType Type;
        internal readonly string Name;
        internal readonly PreparedTeamMember[] Members;

        internal PreparedTeamGroup(TeamType type, string name, PreparedTeamMember[] members) {
            Type = type;
            Name = name;
            Members = members;
        }
    }

    internal sealed class PreparedTeamMember {
        internal readonly string Username;
        internal readonly string ProfilePicture;
        internal readonly string ProfilePictureUrl;
        internal readonly string Discord;
        internal readonly string GitHub;
        internal readonly string Twitch;
        internal readonly string Twitter;
        internal readonly string YouTube;

        internal PreparedTeamMember(TeamMember member) {
            Username = member.Name == "williums"
                ? "<color=#FF0000>w</color><color=#FF7F00>i</color><color=#FFFF00>l</color><color=#00FF00>l</color><color=#0000FF>i</color><color=#4B0082>u</color><color=#8B00FF>m</color><color=#FF0000>s</color>"
                : member.Name;
            ProfilePicture = member.ProfilePicture;
            ProfilePictureUrl = ProfilePicture == null ? null : "https://raw.githubusercontent.com/Umbranoxio/ScoreSaber-Team/main/images/" + ProfilePicture;
            Discord = member.Discord == null ? null : string.Empty + member.Discord;
            GitHub = member.GitHub == null ? null : "https://github.com/" + member.GitHub;
            Twitch = member.Twitch == null ? null : "https://www.twitch.tv/" + member.Twitch;
            Twitter = member.Twitter == null ? null : "https://twitter.com/" + member.Twitter;
            YouTube = member.YouTube == null ? null : "https://www.youtube.com/channel/" + member.YouTube;
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum TeamType {
        Backend,
        Frontend,
        Mod,
        PPv3,
        Admin,
        RT,
        NAT,
        QAT,
        CAT,
        CCT
    }
}
