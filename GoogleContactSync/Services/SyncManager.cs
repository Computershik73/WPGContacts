// Services/SyncManager.cs
// Bidirectional sync between Google Contacts and W10M phone.
// Conflict resolution: compare Google updateTime vs stored updateTime.
// If Google updateTime changed → Google is newer → download.
// If local hash changed but Google updateTime unchanged → local is newer → upload.
// If both changed → Google wins (safe default).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;
using Windows.Storage;
using GoogleContactSync.Helpers;
using GoogleContactSync.Models;

namespace GoogleContactSync.Services
{
    public class SyncManager
    {
        private readonly GoogleApiService  _api;
        private readonly ContactStoreService _store;

        public SyncManager(string clientId, string clientSecret)
        {
            _api   = new GoogleApiService(clientId, clientSecret);
            _store = new ContactStoreService();
        }

        // ================================================================
        // MAIN SYNC — bidirectional with conflict resolution
        // ================================================================
        public async Task<SyncResult> SyncAsync(IProgress<string> progress = null)
        {
            var result = new SyncResult();

            progress?.Report("Loading sync state...");
            var state = SyncStateStorage.Load();

            progress?.Report("Fetching contacts from Google...");
            var googleContacts = await _api.FetchAllContactsAsync(progress);
            var googleById     = new Dictionary<string, GoogleContact>();
            foreach (var gc in googleContacts)
                if (!string.IsNullOrEmpty(gc.Id))
                    googleById[gc.Id] = gc;

            progress?.Report("Reading contacts from phone...");
            var phoneContacts = await _store.ReadAllContactsAsync(null);
            var phoneById     = new Dictionary<string, Contact>();
            foreach (var c in phoneContacts)
                if (!string.IsNullOrEmpty(c.RemoteId))
                    phoneById[c.RemoteId] = c;

            var newState = new Dictionary<string, ContactSyncState>();

            // ============================================================
            // PHASE 1: Process all Google contacts
            // ============================================================
            progress?.Report("Syncing " + googleContacts.Count + " Google contacts...");

            foreach (var gc in googleContacts)
            {
                if (string.IsNullOrEmpty(gc.Id)) continue;

                bool hasState    = state.ContainsKey(gc.Id);
                var  savedState  = hasState ? state[gc.Id] : null;

                bool googleChanged = !hasState ||
                                     savedState.ETag != gc.ETag;

                phoneById.TryGetValue(gc.Id, out Contact localContact);
                bool existsLocally = localContact != null;

                if (!existsLocally)
                {
                    // New from Google — download
                    await _store.UpsertContactAsync(gc);
                    result.Downloaded++;
                }
                else if (googleChanged)
                {
                    // Google changed — check local too
                    string currentLocalHash = ComputeLocalHash(localContact);
                    bool   localChanged     = hasState &&
                                             savedState.LocalHash != currentLocalHash;

                    if (!localChanged)
                    {
                        // Only Google changed → download
                        await _store.UpsertContactAsync(gc);
                        result.Downloaded++;
                    }
                    else
                    {
                        // Both changed → compare timestamps
                        // If Google updateTime is newer than stored → Google wins
                        bool googleNewer = string.Compare(
                            gc.UpdateTime,
                            savedState?.UpdateTime ?? "",
                            StringComparison.Ordinal) > 0;

                        if (googleNewer)
                        {
                            await _store.UpsertContactAsync(gc);
                            result.Downloaded++;
                            result.ConflictsGoogleWon++;
                        }
                        else
                        {
                            // Local is newer → upload
                            bool ok = await _api.UpdateContactAsync(
                                localContact, savedState?.ETag ?? "*", null);
                            if (ok)
                            {
                                result.Uploaded++;
                                result.ConflictsLocalWon++;
                            }
                        }
                    }
                }
                else if (existsLocally)
                {
                    // Google unchanged — check if local changed
                    string currentLocalHash = ComputeLocalHash(localContact);
                    bool   localChanged     = hasState &&
                                             !string.IsNullOrEmpty(savedState.LocalHash) &&
                                             savedState.LocalHash != currentLocalHash;

                    if (localChanged)
                    {
                        // Local changed, Google didn't → upload
                        bool ok = await _api.UpdateContactAsync(
                            localContact, gc.ETag, null);
                        if (ok) result.Uploaded++;
                    }
                }

                // Refresh local contact after possible upsert
                var updatedPhone = await _store.FindContactByRemoteIdAsync(gc.Id);
                string localHash = updatedPhone != null
                    ? ComputeLocalHash(updatedPhone) : "";

                newState[gc.Id] = new ContactSyncState
                {
                    ETag       = gc.ETag,
                    UpdateTime = gc.UpdateTime,
                    LocalHash  = localHash
                };
            }

            // ============================================================
            // PHASE 2: Local contacts not in Google
            // ============================================================
            foreach (var c in phoneContacts)
            {
                if (string.IsNullOrEmpty(c.RemoteId))
                {
                    // New local contact → upload to Google
                    string newId = await _api.CreateContactAsync(c, null);
                    if (newId != null)
                    {
                        await _store.SaveRemoteIdAsync(c.Id, newId);
                        result.Uploaded++;

                        // Re-read to get updated contact
                        var updated = await _store.FindContactByRemoteIdAsync(newId);
                        newState[newId] = new ContactSyncState
                        {
                            ETag      = "",
                            UpdateTime = "",
                            LocalHash  = updated != null
                                ? ComputeLocalHash(updated) : ""
                        };
                    }
                    continue;
                }

                if (!googleById.ContainsKey(c.RemoteId) &&
                    state.ContainsKey(c.RemoteId))
                {
                    // Was synced before but deleted from Google → delete locally
                    await _store.DeleteContactAsync(c.RemoteId);
                    result.Deleted++;
                    // Don't add to newState — contact is gone
                }
            }

            // ============================================================
            // PHASE 3: Save new state
            // ============================================================
            SyncStateStorage.Save(newState);

            // Log timestamp
            ApplicationData.Current.LocalSettings.Values["LastBgSync"] =
                DateTime.Now.ToString("dd MMM yyyy HH:mm");

            progress?.Report(string.Format(
                "Done. ↓{0} ↑{1} ✗{2} conflicts({3}G/{4}L)",
                result.Downloaded, result.Uploaded, result.Deleted,
                result.ConflictsGoogleWon, result.ConflictsLocalWon));

            return result;
        }

        // ================================================================
        // LOCAL HASH — stable fields only, no labels
        // ================================================================
        public static string ComputeLocalHash(Contact c)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Norm(c.FirstName));  sb.Append("|");
            sb.Append(Norm(c.LastName));   sb.Append("|");
            sb.Append(Norm(c.Nickname));   sb.Append("|");
            sb.Append(Norm(c.Notes));

            foreach (var e in c.Emails)
            { sb.Append("|E:"); sb.Append(Norm(e.Address)); }

            foreach (var p in c.Phones)
            { sb.Append("|P:"); sb.Append(NormPhone(p.Number)); }

            foreach (var a in c.Addresses)
            {
                sb.Append("|A:");
                sb.Append(Norm(a.StreetAddress));
                sb.Append(Norm(a.Locality));
                sb.Append(Norm(a.PostalCode));
                sb.Append(Norm(a.Country));
            }

            if (c.JobInfo.Count > 0)
            {
                sb.Append("|J:");
                sb.Append(Norm(c.JobInfo[0].CompanyName));
                sb.Append(Norm(c.JobInfo[0].Title));
            }

            foreach (var d in c.ImportantDates)
            {
                if (d.Kind == ContactDateKind.Birthday)
                {
                    sb.Append("|B:");
                    sb.Append(d.Year.HasValue ? d.Year.Value.ToString() : "");
                    sb.Append("-"); sb.Append((int)d.Month);
                    sb.Append("-"); sb.Append((int)d.Day);
                    break;
                }
            }

            string raw = sb.ToString();
            int hash = 17;
            foreach (char ch in raw) hash = hash * 31 + ch;
            return hash.ToString();
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim().ToLowerInvariant();
        }

        private static string NormPhone(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
                if (char.IsDigit(c) || c == '+') sb.Append(c);
            return sb.ToString();
        }
    }

    public class SyncResult
    {
        public int Downloaded        { get; set; }
        public int Uploaded          { get; set; }
        public int Deleted           { get; set; }
        public int ConflictsGoogleWon { get; set; }
        public int ConflictsLocalWon  { get; set; }

        public override string ToString()
        {
            return string.Format(
                "Downloaded: {0}, Uploaded: {1}, Deleted: {2}",
                Downloaded, Uploaded, Deleted);
        }
    }
}
