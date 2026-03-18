// Helpers/SyncStateStorage.cs
// Stores per-contact sync state in LocalSettings (no file I/O needed).
// Avoids IAsyncOperation/AsTask issues by using LocalSettings instead of files.

using System;
using System.Collections.Generic;
using Windows.Data.Json;
using Windows.Storage;

namespace GoogleContactSync.Helpers
{
    public class ContactSyncState
    {
        public string ETag        { get; set; } = "";
        public string UpdateTime  { get; set; } = "";
        public string LocalHash   { get; set; } = "";
    }

    public static class SyncStateStorage
    {
        private const string SettingsKey = "SyncStateV1";

        public static void Save(Dictionary<string, ContactSyncState> state)
        {
            try
            {
                var root = new JsonObject();
                foreach (var kv in state)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    var obj = new JsonObject();
                    obj.SetNamedValue("e", JsonValue.CreateStringValue(kv.Value.ETag       ?? ""));
                    obj.SetNamedValue("u", JsonValue.CreateStringValue(kv.Value.UpdateTime ?? ""));
                    obj.SetNamedValue("h", JsonValue.CreateStringValue(kv.Value.LocalHash  ?? ""));
                    root.SetNamedValue(kv.Key, obj);
                }
                ApplicationData.Current.LocalSettings.Values[SettingsKey] =
                    root.Stringify();
            }
            catch { }
        }

        public static Dictionary<string, ContactSyncState> Load()
        {
            var result = new Dictionary<string, ContactSyncState>();
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (!settings.Values.ContainsKey(SettingsKey)) return result;

                string json = settings.Values[SettingsKey] as string;
                if (string.IsNullOrEmpty(json)) return result;

                var root = JsonObject.Parse(json);
                foreach (var key in root.Keys)
                {
                    var obj = root.GetNamedObject(key);
                    result[key] = new ContactSyncState
                    {
                        ETag       = obj.ContainsKey("e") ? obj.GetNamedString("e") : "",
                        UpdateTime = obj.ContainsKey("u") ? obj.GetNamedString("u") : "",
                        LocalHash  = obj.ContainsKey("h") ? obj.GetNamedString("h") : ""
                    };
                }
            }
            catch { }
            return result;
        }

        public static void Clear()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(SettingsKey);
        }
    }
}
