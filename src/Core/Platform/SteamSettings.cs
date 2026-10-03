using Microsoft.Win32;
using System;
using System.IO;
using Newtonsoft.Json;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Platform {
    internal class SteamSettings {

        internal class PartialSteamVRSettings {
            public LastKnown LastKnown { get; set; }
        }

        internal class LastKnown {
            public string HMDManufacturer { get; set; }
            public string HMDModel { get; set; }
        }

        internal static string HMDName = null;

        private static readonly object PreparationLock = new object();
        private static Task _preparationTail = Task.CompletedTask;

        internal static void Initialize() {
            HMDName = AttemptGetHMD();
        }

        private static string AttemptGetHMD() {
            try {
                string json = CompletePreparation(new PreparationRequest(null));
                if (json == null) return null;

                if (JsonConvert.DefaultSettings != null) {
                    return FormatHMD(JsonConvert.DeserializeObject<PartialSteamVRSettings>(json));
                }

                return CompletePreparation(new PreparationRequest(json));
            } catch (Exception ex) {
                Plugin.Log.Info($"Failed to get HMD from SteamSettings {ex}");
                return null;
            }
        }

        private static string GetSteamDir() {
            string steamInstall = ReadSteamInstallPath(RegistryView.Registry64);
            if (string.IsNullOrEmpty(steamInstall)) {
                steamInstall = ReadSteamInstallPath(RegistryView.Default);
            }
            return steamInstall;
        }

        private static string ReadSteamInstallPath(RegistryView view) {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (RegistryKey software = root?.OpenSubKey("SOFTWARE"))
            using (RegistryKey wow6432 = software?.OpenSubKey("WOW6432Node"))
            using (RegistryKey valve = wow6432?.OpenSubKey("Valve"))
            using (RegistryKey steam = valve?.OpenSubKey("Steam")) {
                return steam?.GetValue("InstallPath").ToString();
            }
        }

        private static string FormatHMD(PartialSteamVRSettings settings) {
            return $"{settings.LastKnown.HMDManufacturer}:{settings.LastKnown.HMDModel}";
        }

        private static string CompletePreparation(PreparationRequest request) {
            PreparationResult result = QueuePreparation(request).GetAwaiter().GetResult();
            if (result.Error != null) {
                ExceptionDispatchInfo.Capture(result.Error).Throw();
            }

            return result.Value;
        }

        private static Task<PreparationResult> QueuePreparation(PreparationRequest request) {
            lock (PreparationLock) {
                Task<PreparationResult> task = _preparationTail.ContinueWith(
                    (_, state) => ((PreparationRequest)state).Run(), request,
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                _preparationTail = task;
                _ = task.ContinueWith(completed => {
                    lock (PreparationLock) {
                        if (ReferenceEquals(_preparationTail, completed)) {
                            _preparationTail = Task.CompletedTask;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return task;
            }
        }

        private sealed class PreparationResult {
            internal string Value { get; }
            internal Exception Error { get; }

            internal PreparationResult(string value, Exception error = null) {
                Value = value;
                Error = error;
            }
        }

        private sealed class PreparationRequest {
            private readonly string _json;

            internal PreparationRequest(string json) {
                _json = json;
            }

            internal PreparationResult Run() {
                try {
                    if (_json == null) {
                        string steamDir = GetSteamDir();
                        if (string.IsNullOrEmpty(steamDir)) return new PreparationResult(null);

                        string configPath = Path.Combine(steamDir, "config", "steamvr.vrsettings");
                        return new PreparationResult(File.Exists(configPath) ? File.ReadAllText(configPath) : null);
                    }

                    JsonSerializer serializer = JsonSerializer.Create();
                    serializer.CheckAdditionalContent = true;
                    using (var text = new StringReader(_json))
                    using (var reader = new JsonTextReader(text)) {
                        return new PreparationResult(FormatHMD(serializer.Deserialize<PartialSteamVRSettings>(reader)));
                    }
                } catch (Exception error) {
                    return new PreparationResult(null, error);
                }
            }
        }
    }
}
