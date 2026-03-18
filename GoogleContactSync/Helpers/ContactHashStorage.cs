// Helpers/ContactHashStorage.cs
// Stores ETag map (resourceName → etag) for incremental sync detection.

using System.Collections.Generic;
using Windows.Storage;

namespace GoogleContactSync.Helpers
{
    public static class ContactHashStorage
    {
        private const string ContainerName = "ContactHashes";

        public static void SaveAll(Dictionary<string, string> uidToHash)
        {
            var settings  = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer(ContainerName,
                ApplicationDataCreateDisposition.Always);
            container.Values.Clear();
            foreach (var kv in uidToHash)
                if (!string.IsNullOrEmpty(kv.Key))
                    container.Values[SafeKey(kv.Key)] = kv.Key + "|" + kv.Value;
        }

        public static Dictionary<string, string> LoadAll()
        {
            var result   = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(ContainerName)) return result;
            var container = settings.Containers[ContainerName];
            foreach (var kv in container.Values)
            {
                string s = kv.Value as string;
                if (string.IsNullOrEmpty(s)) continue;
                int sep = s.IndexOf('|');
                if (sep < 0) continue;
                result[s.Substring(0, sep)] = s.Substring(sep + 1);
            }
            return result;
        }

        public static void Clear()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Containers.ContainsKey(ContainerName))
                settings.Containers[ContainerName].Values.Clear();
        }

        private static string SafeKey(string s)
        {
            return s.Length <= 200 ? s : s.Substring(s.Length - 200);
        }
    }
}
