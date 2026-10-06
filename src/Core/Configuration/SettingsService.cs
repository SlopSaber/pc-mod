using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Configuration {
    internal class SettingsService {
        private const int CurrentVersion = 12;
        private static readonly object SaveQueueLock = new object();
        private static Task _saveTail = Task.CompletedTask;

        internal string DataPath => "UserData";
        internal string ConfigPath => DataPath + @"\ScoreSaber";
        internal string ReplayPath => ConfigPath + @"\Replays";
        private string SettingsPath => ConfigPath + @"\ScoreSaber.json";

        internal Settings Current { get; private set; } = CreateDefaultSettings();

        internal void Load() {
            try {
                EnsureSaveDirectories();
                string serialized = ReadSettings(Path.GetFullPath(SettingsPath));

                if (serialized == null) {
                    Current = CreateDefaultSettings();
                    Save();
                    return;
                }

                Settings loaded;
                if (JsonConvert.DefaultSettings == null) {
                    JsonSerializer serializer = JsonSerializer.Create();
                    serializer.CheckAdditionalContent = true;
                    loaded = DeserializeOwned(serialized, serializer);
                } else {
                    loaded = JsonConvert.DeserializeObject<Settings>(serialized);
                }
                Current = loaded ?? CreateDefaultSettings();
                if (Current.fileVersion < CurrentVersion) {
                    Upgrade(Current);
                    Save();
                }
            } catch (Exception ex) {
                Plugin.Log.Error("Failed to load settings " + ex.ToString());
                Current = CreateDefaultSettings();
            }
        }

        private static string ReadSettings(string path) {
            string serialized = null;
            RunSaveWork(() => {
                if (File.Exists(path)) {
                    serialized = File.ReadAllText(path);
                }
            });
            return serialized;
        }

        private static Settings DeserializeOwned(string serialized, JsonSerializer serializer) {
            Settings loaded = null;
            RunSaveWork(() => {
                using (var reader = new JsonTextReader(new StringReader(serialized))) {
                    loaded = serializer.Deserialize<Settings>(reader);
                }
            });
            return loaded;
        }

        internal void Save() {
            try {
                EnsureSaveDirectories();
                Current.fileVersion = CurrentVersion;

                var serializerSettings = new JsonSerializerSettings {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    Formatting = Formatting.Indented
                };
                Settings current = Current;
                if (JsonConvert.DefaultSettings == null && current.GetType() == typeof(Settings) &&
                    (current.spectatorPositions == null || current.spectatorPositions.GetType() == typeof(List<Settings.SpectatorPoseRoot>))) {
                    Settings snapshot = current.CreateSaveSnapshot();
                    JsonSerializer serializer = JsonSerializer.Create(serializerSettings);
                    string path = Path.GetFullPath(SettingsPath);
                    RunSaveWork(() => File.WriteAllText(path, SerializeOwned(snapshot, serializer)));
                } else {
                    string serialized = JsonConvert.SerializeObject(Current, serializerSettings);
                    string path = Path.GetFullPath(SettingsPath);
                    RunSaveWork(() => File.WriteAllText(path, serialized));
                }
            } catch (Exception ex) {
                Plugin.Log.Error("Failed to save settings " + ex.ToString());
            }
        }

        private void EnsureSaveDirectories() {
            string dataPath = Path.GetFullPath(DataPath);
            string configPath = Path.GetFullPath(ConfigPath);
            string replayPath = Path.GetFullPath(ReplayPath);
            RunSaveWork(() => {
                Directory.CreateDirectory(dataPath);
                Directory.CreateDirectory(configPath);
                Directory.CreateDirectory(replayPath);
            });
        }

        private static string SerializeOwned(Settings snapshot, JsonSerializer serializer) {
            using (var output = new StringWriter(new StringBuilder(256), CultureInfo.InvariantCulture)) {
                using (var writer = new JsonTextWriter(output)) {
                    writer.Formatting = serializer.Formatting;
                    serializer.Serialize(writer, snapshot, null);
                }
                return output.ToString();
            }
        }

        private static void RunSaveWork(Action work) {
            Task task;
            lock (SaveQueueLock) {
                if (ExecutionContext.IsFlowSuppressed()) {
                    task = StartSaveWork(work);
                } else {
                    using (ExecutionContext.SuppressFlow()) {
                        task = StartSaveWork(work);
                    }
                }
            }
            task.GetAwaiter().GetResult();
        }

        private static Task StartSaveWork(Action work) {
            Task task = _saveTail.ContinueWith(
                static (_, state) => ((Action)state)(), work,
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _saveTail = task;
            _ = task.ContinueWith(static completed => {
                lock (SaveQueueLock) {
                    if (ReferenceEquals(_saveTail, completed)) {
                        _saveTail = Task.CompletedTask;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return task;
        }

        internal string ReplayPathFor(string playerId, string songHash, BeatmapKey beatmapKey) => $@"{ReplayPath}\{playerId}-{songHash}-{beatmapKey.difficulty.SerializedName()}-{beatmapKey.CharacteristicSerializedName()}.dat";

        internal string LegacyReplayPathFor(string playerId, string songName, string difficulty, string characteristic, string songHash) => $@"{ReplayPath}\{playerId}-{songName}-{difficulty}-{characteristic}-{songHash}.dat";

        internal string LegacyReplayPathFor(string playerId, string songName, string songHash) => $@"{ReplayPath}\{playerId}-{songName}-{songHash}.dat";

        private void EnsureDirectories() {
            Directory.CreateDirectory(DataPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(ReplayPath);
        }

        private static Settings CreateDefaultSettings() {
            var settings = new Settings();
            settings.SetDefaults();
            return settings;
        }

        private static void Upgrade(Settings settings) {
            if (settings.spectatorPositions == null || settings.spectatorPositions.Count == 0) {
                settings.SetDefaultSpectatorPositions();
            }
            if (settings.locationFilterMode == null) {
                settings.locationFilterMode = "Country";
            }
            if (settings.fileVersion < 8) {
                settings.replayCameraSmoothing = true;
            }
            if (settings.fileVersion < 9) {
                settings.replayOverrideHandedness = false;
            }
            if (settings.fileVersion < 10) {
                settings.publicLivePresenceOptOut = false;
                settings.liveChatOverlayEnabled = true;
                settings.liveChatOverlayGameplayEnabled = true;
                settings.liveChatOverlayScale = 1.15f;
                settings.liveChatOverlayTextScale = 1.25f;
            }
            if (settings.fileVersion < 11) {
                settings.useRecordedPlayerSettings = true;
            }
            if (settings.fileVersion < 12) {
                settings.shareHsvProfiles = false;
            }
        }
    }
}
