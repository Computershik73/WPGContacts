// Services/ContactStoreService.cs
// Writes Google contacts to W10M ContactStore.
// Adapted from GmailCardDAVSync — uses Google People API models.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;
using Windows.Storage.Streams;
using System.IO;
using GoogleContactSync.Models;
using GoogleContactSync.Helpers;

namespace GoogleContactSync.Services
{
    public class ContactStoreService
    {
        private const string ListDisplayName = "Google Contacts";

        // ================================================================
        // FULL SYNC — clear list and rewrite all contacts
        // ================================================================
        public async Task<int> SyncAllAsync(
            List<GoogleContact> contacts,
            IProgress<string> progress = null)
        {
            var store = await GetStoreAsync();
            var list  = await GetOrCreateListAsync(store);

            progress?.Report("Clearing old contacts...");
            await ClearListAsync(list);

            progress?.Report("Writing " + contacts.Count + " contacts...");
            int saved   = 0;
            var hashMap = new Dictionary<string, string>();

            foreach (var gc in contacts)
            {
                try
                {
                    var uwp = await ToUwpContactAsync(gc);
                    await list.SaveContactAsync(uwp);

                    if (!string.IsNullOrEmpty(gc.Id))
                        hashMap[gc.Id] = gc.ETag;

                    saved++;
                }
                catch { }
            }

            // Save etags as hashes for change detection
            ContactHashStorage.SaveAll(hashMap);
            return saved;
        }

        // ================================================================
        // UPSERT — update or create single contact
        // Matches by RemoteId (resourceName) or first+last name
        // ================================================================
        public async Task UpsertContactAsync(GoogleContact gc)
        {
            var store = await GetStoreAsync();
            var list  = await GetOrCreateListAsync(store);

            string searchName = ((gc.FirstName ?? "") + " " + (gc.LastName ?? ""))
                .Trim().ToLowerInvariant();

            // Find and delete existing
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                {
                    bool byId   = !string.IsNullOrEmpty(gc.Id) && c.RemoteId == gc.Id;
                    bool byName = !string.IsNullOrEmpty(searchName) &&
                                   (c.FirstName + " " + c.LastName)
                                   .Trim().ToLowerInvariant() == searchName;
                    if (byId || byName)
                    {
                        await list.DeleteContactAsync(c);
                        break;
                    }
                }
                batch = await reader.ReadBatchAsync();
            }

            var uwp = await ToUwpContactAsync(gc);
            await list.SaveContactAsync(uwp);

            // Update hash
            if (!string.IsNullOrEmpty(gc.Id))
            {
                var hashes = ContactHashStorage.LoadAll();
                hashes[gc.Id] = gc.ETag;
                ContactHashStorage.SaveAll(hashes);
            }
        }

        // ================================================================
        // DELETE by resourceName
        // ================================================================
        public async Task DeleteContactAsync(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return;
            var store = await GetStoreAsync();
            var list  = await GetOrCreateListAsync(store);

            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                {
                    if (c.RemoteId == resourceName)
                    {
                        await list.DeleteContactAsync(c);
                        return;
                    }
                }
                batch = await reader.ReadBatchAsync();
            }
        }

        // ================================================================
        // READ ALL — for Phone→Google sync
        // ================================================================
        public async Task<List<Contact>> ReadAllContactsAsync(
            IProgress<string> progress = null)
        {
            var store  = await GetStoreAsync();
            var list   = await GetOrCreateListAsync(store);
            var result = new List<Contact>();

            progress?.Report("Reading contacts from phone...");

            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                    result.Add(c);
                batch = await reader.ReadBatchAsync();
            }

            progress?.Report("Read " + result.Count + " contacts.");
            return result;
        }

        // ================================================================
        // SAVE RemoteId back after creating new contact on Google
        // ================================================================
        public async Task SaveRemoteIdAsync(string contactId, string resourceName)
        {
            if (string.IsNullOrEmpty(contactId) ||
                string.IsNullOrEmpty(resourceName)) return;
            try
            {
                var store = await GetStoreAsync();
                var list  = await GetOrCreateListAsync(store);
                var reader = list.GetContactReader();
                var batch  = await reader.ReadBatchAsync();
                while (batch.Contacts.Count > 0)
                {
                    foreach (var c in batch.Contacts)
                    {
                        if (c.Id == contactId)
                        {
                            c.RemoteId = resourceName;
                            await list.SaveContactAsync(c);
                            return;
                        }
                    }
                    batch = await reader.ReadBatchAsync();
                }
            }
            catch { }
        }

        // ================================================================
        // FIND contact by RemoteId — used by SyncManager after upsert
        // ================================================================
        public async Task<Contact> FindContactByRemoteIdAsync(string remoteId)
        {
            if (string.IsNullOrEmpty(remoteId)) return null;
            try
            {
                var store  = await GetStoreAsync();
                var list   = await GetOrCreateListAsync(store);
                var reader = list.GetContactReader();
                var batch  = await reader.ReadBatchAsync();
                while (batch.Contacts.Count > 0)
                {
                    foreach (var c in batch.Contacts)
                        if (c.RemoteId == remoteId) return c;
                    batch = await reader.ReadBatchAsync();
                }
            }
            catch { }
            return null;
        }

        // ================================================================
        // PRIVATE
        // ================================================================
        private async Task<ContactStore> GetStoreAsync()
        {
            var store = await ContactManager.RequestStoreAsync(
                ContactStoreAccessType.AppContactsReadWrite);
            if (store == null)
                throw new Exception(
                    "Could not open ContactStore.\n" +
                    "Please grant Contacts permission in Settings.");
            return store;
        }

        private async Task<ContactList> GetOrCreateListAsync(ContactStore store)
        {
            var lists = await store.FindContactListsAsync();
            foreach (var l in lists)
                if (l.DisplayName == ListDisplayName)
                    return l;

            var newList = await store.CreateContactListAsync(ListDisplayName);
            newList.OtherAppReadAccess  = ContactListOtherAppReadAccess.Full;
            newList.OtherAppWriteAccess = ContactListOtherAppWriteAccess.SystemOnly;
            await newList.SaveAsync();
            return newList;
        }

        private async Task ClearListAsync(ContactList list)
        {
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                    await list.DeleteContactAsync(c);
                batch = await reader.ReadBatchAsync();
            }
        }

        private async Task<Contact> ToUwpContactAsync(GoogleContact gc)
        {
            var c = new Contact
            {
                FirstName  = gc.FirstName  ?? "",
                LastName   = gc.LastName   ?? "",
                MiddleName = "",
                Nickname   = gc.Nickname   ?? "",
                Notes      = gc.Notes      ?? "",
                RemoteId   = gc.Id         ?? ""
            };

            foreach (var p in gc.Phones)
                c.Phones.Add(new ContactPhone
                {
                    Number      = p.Number ?? "",
                    Description = PhoneTypeToDescription(p.Type)
                });

            foreach (var e in gc.Emails)
                c.Emails.Add(new ContactEmail
                {
                    Address = e.Address ?? "",
                    Kind    = e.Type.Contains("work")
                        ? ContactEmailKind.Work
                        : ContactEmailKind.Personal
                });

            foreach (var a in gc.Addresses)
                c.Addresses.Add(new ContactAddress
                {
                    StreetAddress = a.Street     ?? "",
                    Locality      = a.City       ?? "",
                    Region        = a.Region     ?? "",
                    PostalCode    = a.PostalCode  ?? "",
                    Country       = a.Country    ?? "",
                    Kind          = a.Type.Contains("work")
                        ? ContactAddressKind.Work
                        : ContactAddressKind.Home
                });

            foreach (var w in gc.Urls)
            {
                if (string.IsNullOrEmpty(w.Value)) continue;
                try
                {
                    c.Websites.Add(new ContactWebsite
                        { Uri = new Uri(w.Value) });
                }
                catch { }
            }

            if (gc.Organizations.Count > 0)
                c.JobInfo.Add(new ContactJobInfo
                {
                    CompanyName = gc.Organizations[0].Name  ?? "",
                    Title       = gc.Organizations[0].Title ?? ""
                });

            if (gc.Birthday != null)
            {
                uint m = gc.Birthday.Month;
                uint d = gc.Birthday.Day;
                if (m >= 1 && m <= 12 && d >= 1 && d <= 31)
                    c.ImportantDates.Add(new ContactDate
                    {
                        Kind  = ContactDateKind.Birthday,
                        Year  = gc.Birthday.Year,
                        Month = m,
                        Day   = d
                    });
            }

            if (string.IsNullOrEmpty(c.FirstName) &&
                string.IsNullOrEmpty(c.LastName) &&
                c.Phones.Count > 0)
                c.Nickname = gc.Phones[0].Number;

            // Download photo if available
            if (!string.IsNullOrEmpty(gc.PhotoUrl) &&
                !gc.PhotoUrl.Contains("default-user"))
            {
                try
                {
                    using (var http = new HttpClient())
                    {
                        var stream    = await http.GetStreamAsync(gc.PhotoUrl);
                        var memStream = new InMemoryRandomAccessStream();
                        await Windows.Storage.Streams.RandomAccessStream.CopyAsync(
                            stream.AsInputStream(),
                            memStream.GetOutputStreamAt(0));
                        c.SourceDisplayPicture =
                            RandomAccessStreamReference.CreateFromStream(memStream);
                    }
                }
                catch { }
            }

            return c;
        }

        private string PhoneTypeToDescription(string type)
        {
            if (string.IsNullOrEmpty(type)) return "Mobile";
            string t = type.ToLowerInvariant();
            if (t.Contains("home"))   return "Home";
            if (t.Contains("work"))   return "Work";
            if (t.Contains("mobile") || t.Contains("cell")) return "Mobile";
            if (t.Contains("fax"))    return "Fax";
            if (t.Contains("pager"))  return "Pager";
            return "Mobile";
        }
    }
}
