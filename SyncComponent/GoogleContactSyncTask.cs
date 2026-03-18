// SyncComponent/GoogleContactSyncTask.cs
// Self-contained background task — runs every 15 minutes.
// Performs bidirectional sync with conflict resolution.
// Does NOT depend on linked files from main project.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Data.Json;
using Windows.Security.Credentials;
using Windows.Storage;

namespace SyncComponent
{
    public sealed class GoogleContactSyncTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;
        private const string LastRunKey   = "BgSyncLastRun";
        private const int    CooldownSecs = 120;

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            try
            {
                // Cooldown to prevent re-entrancy
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey(LastRunKey))
                {
                    long lastTicks = (long)settings.Values[LastRunKey];
                    if ((DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc))
                        .TotalSeconds < CooldownSecs)
                        return;
                }
                settings.Values[LastRunKey] = DateTime.UtcNow.Ticks;
                await DoSyncAsync();
            }
            catch { }
            finally { _deferral.Complete(); }
        }

        private async Task DoSyncAsync()
        {
            // Load credentials
            string clientId, clientSecret, refreshToken;
            if (!LoadCredentials(out clientId, out clientSecret, out refreshToken))
                return;

            string accessToken = await GetAccessTokenAsync(
                refreshToken, clientId, clientSecret);
            if (string.IsNullOrEmpty(accessToken)) return;

            // Load sync state
            var state = LoadSyncState();

            // Fetch Google contacts
            var googleContacts = await FetchGoogleContactsAsync(accessToken);
            var googleById     = new Dictionary<string, GContact>();
            foreach (var gc in googleContacts)
                if (!string.IsNullOrEmpty(gc.Id))
                    googleById[gc.Id] = gc;

            // Read phone contacts
            var store        = await GetOrCreateContactListAsync();
            var phoneContacts = await ReadPhoneContactsAsync(store);
            var phoneById    = new Dictionary<string, Contact>();
            foreach (var c in phoneContacts)
                if (!string.IsNullOrEmpty(c.RemoteId))
                    phoneById[c.RemoteId] = c;

            var newState = new Dictionary<string, SyncState>();

            // Phase 1: Process Google contacts
            foreach (var gc in googleContacts)
            {
                if (string.IsNullOrEmpty(gc.Id)) continue;

                bool hasState   = state.ContainsKey(gc.Id);
                var  saved      = hasState ? state[gc.Id] : null;
                bool gChanged   = !hasState || saved.ETag != gc.ETag;

                phoneById.TryGetValue(gc.Id, out Contact local);

                if (local == null)
                {
                    await UpsertContactAsync(store, gc);
                }
                else if (gChanged)
                {
                    string localHash    = ComputeHash(local);
                    bool   localChanged = hasState &&
                                         !string.IsNullOrEmpty(saved.LocalHash) &&
                                         saved.LocalHash != localHash;

                    if (!localChanged)
                    {
                        await UpsertContactAsync(store, gc);
                    }
                    else
                    {
                        // Conflict — compare timestamps
                        bool gNewer = string.Compare(gc.UpdateTime,
                            saved?.UpdateTime ?? "",
                            StringComparison.Ordinal) > 0;

                        if (gNewer)
                            await UpsertContactAsync(store, gc);
                        else
                            await UpdateGoogleContactAsync(
                                accessToken, local, saved?.ETag ?? "*");
                    }
                }
                else if (local != null)
                {
                    string localHash    = ComputeHash(local);
                    bool   localChanged = hasState &&
                                         !string.IsNullOrEmpty(saved.LocalHash) &&
                                         saved.LocalHash != localHash;
                    if (localChanged)
                        await UpdateGoogleContactAsync(
                            accessToken, local, gc.ETag);
                }

                // Refresh local contact for hash
                var refreshed = await FindByRemoteIdAsync(store, gc.Id);
                newState[gc.Id] = new SyncState
                {
                    ETag       = gc.ETag,
                    UpdateTime = gc.UpdateTime,
                    LocalHash  = refreshed != null ? ComputeHash(refreshed) : ""
                };
            }

            // Phase 2: New local contacts
            foreach (var c in phoneContacts)
            {
                if (string.IsNullOrEmpty(c.RemoteId))
                {
                    string newId = await CreateGoogleContactAsync(accessToken, c);
                    if (!string.IsNullOrEmpty(newId))
                    {
                        c.RemoteId = newId;
                        await store.SaveContactAsync(c);
                        newState[newId] = new SyncState
                        {
                            LocalHash = ComputeHash(c)
                        };
                    }
                    continue;
                }
                if (!googleById.ContainsKey(c.RemoteId) &&
                    state.ContainsKey(c.RemoteId))
                {
                    await store.DeleteContactAsync(c);
                }
            }

            SaveSyncState(newState);
            ApplicationData.Current.LocalSettings.Values["LastBgSync"] =
                DateTime.Now.ToString("dd MMM yyyy HH:mm");
        }

        // ================================================================
        // CONTACT STORE
        // ================================================================
        private async Task<ContactList> GetOrCreateContactListAsync()
        {
            var cs    = await ContactManager.RequestStoreAsync(
                ContactStoreAccessType.AppContactsReadWrite);
            var lists = await cs.FindContactListsAsync();
            foreach (var l in lists)
                if (l.DisplayName == "Google Contacts") return l;
            var nl = await cs.CreateContactListAsync("Google Contacts");
            nl.OtherAppReadAccess  = ContactListOtherAppReadAccess.Full;
            nl.OtherAppWriteAccess = ContactListOtherAppWriteAccess.SystemOnly;
            await nl.SaveAsync();
            return nl;
        }

        private async Task<List<Contact>> ReadPhoneContactsAsync(ContactList list)
        {
            var result = new List<Contact>();
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts) result.Add(c);
                batch = await reader.ReadBatchAsync();
            }
            return result;
        }

        private async Task<Contact> FindByRemoteIdAsync(
            ContactList list, string remoteId)
        {
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                    if (c.RemoteId == remoteId) return c;
                batch = await reader.ReadBatchAsync();
            }
            return null;
        }

        private async Task UpsertContactAsync(ContactList list, GContact gc)
        {
            string searchName = ((gc.FirstName ?? "") + " " + (gc.LastName ?? ""))
                .Trim().ToLowerInvariant();
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                {
                    if ((!string.IsNullOrEmpty(gc.Id) && c.RemoteId == gc.Id) ||
                        (!string.IsNullOrEmpty(searchName) &&
                         (c.FirstName + " " + c.LastName)
                         .Trim().ToLowerInvariant() == searchName))
                    {
                        await list.DeleteContactAsync(c);
                        break;
                    }
                }
                batch = await reader.ReadBatchAsync();
            }
            await list.SaveContactAsync(ToUwpContact(gc));
        }

        private Contact ToUwpContact(GContact gc)
        {
            var c = new Contact
            {
                FirstName  = gc.FirstName ?? "",
                LastName   = gc.LastName  ?? "",
                MiddleName = "",
                Nickname   = gc.Nickname  ?? "",
                Notes      = gc.Notes     ?? "",
                RemoteId   = gc.Id        ?? ""
            };
            foreach (var p in gc.Phones)
                c.Phones.Add(new ContactPhone
                {
                    Number      = p.Number,
                    Description = p.Type.Contains("home") ? "Home" :
                                  p.Type.Contains("work") ? "Work" : "Mobile"
                });
            foreach (var e in gc.Emails)
                c.Emails.Add(new ContactEmail
                {
                    Address = e.Address,
                    Kind    = e.Type.Contains("work")
                        ? ContactEmailKind.Work : ContactEmailKind.Personal
                });
            if (gc.Org != null)
                c.JobInfo.Add(new ContactJobInfo
                {
                    CompanyName = gc.Org.Name,
                    Title       = gc.Org.Title
                });
            if (gc.Birthday != null && gc.Birthday.Month >= 1 &&
                gc.Birthday.Day >= 1)
                c.ImportantDates.Add(new ContactDate
                {
                    Kind  = ContactDateKind.Birthday,
                    Year  = gc.Birthday.Year,
                    Month = gc.Birthday.Month,
                    Day   = gc.Birthday.Day
                });
            return c;
        }

        // ================================================================
        // GOOGLE API
        // ================================================================
        private async Task<string> GetAccessTokenAsync(
            string refresh, string clientId, string clientSecret)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    string body =
                        "client_id="     + Uri.EscapeDataString(clientId) +
                        "&client_secret="+ Uri.EscapeDataString(clientSecret) +
                        "&refresh_token="+ Uri.EscapeDataString(refresh) +
                        "&grant_type=refresh_token";
                    var resp = await http.PostAsync(
                        "https://oauth2.googleapis.com/token",
                        new StringContent(body, Encoding.UTF8,
                            "application/x-www-form-urlencoded"));
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = JsonObject.Parse(
                        await resp.Content.ReadAsStringAsync());
                    return json.GetNamedString("access_token");
                }
            }
            catch { return null; }
        }

        private async Task<List<GContact>> FetchGoogleContactsAsync(
            string accessToken)
        {
            var list = new List<GContact>();
            using (var http = BuildHttp(accessToken))
            {
                string next = "";
                while (true)
                {
                    string url =
                        "https://people.googleapis.com/v1/people/me/connections" +
                        "?personFields=names,nicknames,phoneNumbers,emailAddresses," +
                        "birthdays,organizations,metadata&pageSize=1000";
                    if (!string.IsNullOrEmpty(next))
                        url += "&pageToken=" + Uri.EscapeDataString(next);

                    var resp = await http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) break;

                    var json = JsonObject.Parse(
                        await resp.Content.ReadAsStringAsync());

                    if (json.ContainsKey("connections"))
                        foreach (var item in json.GetNamedArray("connections"))
                            ParseContact(item.GetObject(), list);

                    if (json.ContainsKey("nextPageToken"))
                        next = json.GetNamedString("nextPageToken");
                    else break;
                }
            }
            return list;
        }

        private void ParseContact(JsonObject p, List<GContact> list)
        {
            var gc = new GContact();
            if (p.ContainsKey("resourceName")) gc.Id   = p.GetNamedString("resourceName");
            if (p.ContainsKey("etag"))         gc.ETag = p.GetNamedString("etag");

            if (p.ContainsKey("metadata"))
            {
                var meta = p.GetNamedObject("metadata");
                if (meta.ContainsKey("sources"))
                    foreach (var src in meta.GetNamedArray("sources"))
                    {
                        var s = src.GetObject();
                        if (s.ContainsKey("updateTime"))
                        {
                            string t = s.GetNamedString("updateTime");
                            if (string.Compare(t, gc.UpdateTime,
                                StringComparison.Ordinal) > 0)
                                gc.UpdateTime = t;
                        }
                    }
            }

            if (p.ContainsKey("names"))
            {
                var n = p.GetNamedArray("names")[0].GetObject();
                if (n.ContainsKey("givenName"))  gc.FirstName = n.GetNamedString("givenName");
                if (n.ContainsKey("familyName")) gc.LastName  = n.GetNamedString("familyName");
            }
            if (p.ContainsKey("nicknames"))
            {
                var arr = p.GetNamedArray("nicknames");
                if (arr.Count > 0 && arr[0].GetObject().ContainsKey("value"))
                    gc.Nickname = arr[0].GetObject().GetNamedString("value");
            }
            if (p.ContainsKey("phoneNumbers"))
                foreach (var item in p.GetNamedArray("phoneNumbers"))
                {
                    var o = item.GetObject();
                    if (o.ContainsKey("value"))
                        gc.Phones.Add(new GPhone
                        {
                            Number = o.GetNamedString("value"),
                            Type   = o.ContainsKey("type")
                                ? o.GetNamedString("type").ToLower() : "other"
                        });
                }
            if (p.ContainsKey("emailAddresses"))
                foreach (var item in p.GetNamedArray("emailAddresses"))
                {
                    var o = item.GetObject();
                    if (o.ContainsKey("value"))
                        gc.Emails.Add(new GEmail
                        {
                            Address = o.GetNamedString("value"),
                            Type    = o.ContainsKey("type")
                                ? o.GetNamedString("type").ToLower() : "other"
                        });
                }
            if (p.ContainsKey("organizations"))
            {
                var o = p.GetNamedArray("organizations")[0].GetObject();
                gc.Org = new GOrg
                {
                    Name  = o.ContainsKey("name")  ? o.GetNamedString("name")  : "",
                    Title = o.ContainsKey("title") ? o.GetNamedString("title") : ""
                };
            }
            if (p.ContainsKey("birthdays"))
            {
                var arr = p.GetNamedArray("birthdays");
                if (arr.Count > 0 && arr[0].GetObject().ContainsKey("date"))
                {
                    var d = arr[0].GetObject().GetNamedObject("date");
                    gc.Birthday = new GDate
                    {
                        Year  = d.ContainsKey("year")  ? (int?)((int)d.GetNamedNumber("year"))  : null,
                        Month = d.ContainsKey("month") ? (uint)d.GetNamedNumber("month") : 0,
                        Day   = d.ContainsKey("day")   ? (uint)d.GetNamedNumber("day")   : 0
                    };
                }
            }
            if (!string.IsNullOrEmpty(gc.FirstName) ||
                !string.IsNullOrEmpty(gc.LastName)  ||
                gc.Phones.Count > 0)
                list.Add(gc);
        }

        private async Task<bool> UpdateGoogleContactAsync(
            string accessToken, Contact c, string etag)
        {
            try
            {
                var person = BuildPersonJson(c);
                person.SetNamedValue("etag", JsonValue.CreateStringValue(etag ?? "*"));
                string resourceId = c.RemoteId;
                if (!resourceId.StartsWith("people/"))
                    resourceId = "people/" + resourceId;
                string url = "https://people.googleapis.com/v1/" + resourceId +
                    ":updateContact?updatePersonFields=names,nicknames," +
                    "phoneNumbers,emailAddresses,birthdays,organizations";
                using (var http = BuildHttp(accessToken))
                {
                    var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
                    {
                        Content = new StringContent(person.Stringify(),
                            Encoding.UTF8, "application/json")
                    };
                    var resp = await http.SendAsync(req);
                    return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private async Task<string> CreateGoogleContactAsync(
            string accessToken, Contact c)
        {
            try
            {
                var person = BuildPersonJson(c);
                using (var http = BuildHttp(accessToken))
                {
                    var resp = await http.PostAsync(
                        "https://people.googleapis.com/v1/people:createContact",
                        new StringContent(person.Stringify(),
                            Encoding.UTF8, "application/json"));
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = JsonObject.Parse(
                        await resp.Content.ReadAsStringAsync());
                    return json.ContainsKey("resourceName")
                        ? json.GetNamedString("resourceName") : null;
                }
            }
            catch { return null; }
        }

        private JsonObject BuildPersonJson(Contact c)
        {
            var p = new JsonObject();
            var names = new JsonArray();
            var n = new JsonObject();
            n.SetNamedValue("givenName",  JsonValue.CreateStringValue(c.FirstName ?? ""));
            n.SetNamedValue("familyName", JsonValue.CreateStringValue(c.LastName  ?? ""));
            names.Add(n); p.SetNamedValue("names", names);

            if (c.Phones.Count > 0)
            {
                var phones = new JsonArray();
                foreach (var ph in c.Phones)
                {
                    var o = new JsonObject();
                    o.SetNamedValue("value", JsonValue.CreateStringValue(ph.Number ?? ""));
                    o.SetNamedValue("type",  JsonValue.CreateStringValue(
                        ph.Description?.ToLower() ?? "mobile"));
                    phones.Add(o);
                }
                p.SetNamedValue("phoneNumbers", phones);
            }
            if (c.Emails.Count > 0)
            {
                var emails = new JsonArray();
                foreach (var e in c.Emails)
                {
                    var o = new JsonObject();
                    o.SetNamedValue("value", JsonValue.CreateStringValue(e.Address ?? ""));
                    o.SetNamedValue("type",  JsonValue.CreateStringValue(
                        e.Kind == ContactEmailKind.Work ? "work" : "home"));
                    emails.Add(o);
                }
                p.SetNamedValue("emailAddresses", emails);
            }
            return p;
        }

        private HttpClient BuildHttp(string accessToken)
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            return http;
        }

        // ================================================================
        // SYNC STATE
        // ================================================================
        private Dictionary<string, SyncState> LoadSyncState()
        {
            var result = new Dictionary<string, SyncState>();
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (!settings.Values.ContainsKey("SyncStateV1")) return result;
                string json = settings.Values["SyncStateV1"] as string;
                if (string.IsNullOrEmpty(json)) return result;
                var root = JsonObject.Parse(json);
                foreach (var key in root.Keys)
                {
                    var obj = root.GetNamedObject(key);
                    result[key] = new SyncState
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

        private void SaveSyncState(Dictionary<string, SyncState> state)
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
                ApplicationData.Current.LocalSettings.Values["SyncStateV1"] =
                    root.Stringify();
            }
            catch { }
        }

        // ================================================================
        // CREDENTIALS
        // ================================================================
        private bool LoadCredentials(out string clientId,
            out string clientSecret, out string refreshToken)
        {
            clientId = clientSecret = refreshToken = null;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                clientId     = settings.Values["ClientId"]     as string;
                clientSecret = settings.Values["ClientSecret"] as string;
                if (string.IsNullOrEmpty(clientId) ||
                    string.IsNullOrEmpty(clientSecret)) return false;

                var vault = new PasswordVault();
                var cred  = vault.FindAllByResource("GoogleContactSync")?[0];
                if (cred == null) return false;
                cred.RetrievePassword();
                refreshToken = cred.Password;
                return !string.IsNullOrEmpty(refreshToken);
            }
            catch { return false; }
        }

        // ================================================================
        // LOCAL HASH
        // ================================================================
        private string ComputeHash(Contact c)
        {
            var sb = new StringBuilder();
            sb.Append(Norm(c.FirstName)); sb.Append("|");
            sb.Append(Norm(c.LastName));  sb.Append("|");
            sb.Append(Norm(c.Nickname));  sb.Append("|");
            sb.Append(Norm(c.Notes));
            foreach (var e in c.Emails)
            { sb.Append("|E:"); sb.Append(Norm(e.Address)); }
            foreach (var p in c.Phones)
            { sb.Append("|P:"); sb.Append(NormPhone(p.Number)); }
            if (c.JobInfo.Count > 0)
            { sb.Append("|J:"); sb.Append(Norm(c.JobInfo[0].CompanyName)); }
            int hash = 17;
            foreach (char ch in sb.ToString()) hash = hash * 31 + ch;
            return hash.ToString();
        }

        private string Norm(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();

        private string NormPhone(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
                if (char.IsDigit(c) || c == '+') sb.Append(c);
            return sb.ToString();
        }

        // ================================================================
        // DATA CLASSES
        // ================================================================
        private class SyncState
        {
            public string ETag       { get; set; } = "";
            public string UpdateTime { get; set; } = "";
            public string LocalHash  { get; set; } = "";
        }
        private class GContact
        {
            public string Id         { get; set; } = "";
            public string ETag       { get; set; } = "";
            public string UpdateTime { get; set; } = "";
            public string FirstName  { get; set; } = "";
            public string LastName   { get; set; } = "";
            public string Nickname   { get; set; } = "";
            public string Notes      { get; set; } = "";
            public List<GPhone> Phones { get; set; } = new List<GPhone>();
            public List<GEmail> Emails { get; set; } = new List<GEmail>();
            public GOrg  Org      { get; set; }
            public GDate Birthday { get; set; }
        }
        private class GPhone { public string Number { get; set; } = ""; public string Type { get; set; } = "other"; }
        private class GEmail { public string Address { get; set; } = ""; public string Type { get; set; } = "other"; }
        private class GOrg   { public string Name { get; set; } = ""; public string Title { get; set; } = ""; }
        private class GDate  { public int? Year { get; set; } public uint Month { get; set; } public uint Day { get; set; } }
    }
}
