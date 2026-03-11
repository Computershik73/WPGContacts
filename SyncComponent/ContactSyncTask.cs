using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Security.Credentials;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using System.IO;

namespace SyncComponent
{
    public sealed class ContactSyncTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            try
            {
                var syncManager = new SyncManager();
                // В фоне прогресс нам слушать не нужно
                await syncManager.SyncNowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ФОН] Ошибка: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    internal class SyncState
    {
        public Dictionary<string, string> Etags = new Dictionary<string, string>();
        public Dictionary<string, string> Hashes = new Dictionary<string, string>();
    }

    public sealed class SyncManager
    {
        // Теперь метод поддерживает отправку прогресса (строки)
        public IAsyncActionWithProgress<string> SyncNowAsync()
        {
            return AsyncInfo.Run<string>((token, progress) => ExecuteSyncAsync(progress));
        }

        private async Task ExecuteSyncAsync(IProgress<string> progress)
        {
            System.Diagnostics.Debug.WriteLine("=== НАЧАЛО ДВУСТОРОННЕЙ СИНХРОНИЗАЦИИ ===");
            progress?.Report("Инициализация: проверка ключей и токенов...");

            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string clientId = localSettings.Values["GoogleClientId"] as string;
            string clientSecret = localSettings.Values["GoogleClientSecret"] as string;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return;

            var vault = new PasswordVault();
            var credentials = vault.FindAllByResource("GoogleSyncApp").FirstOrDefault();
            if (credentials == null) return;
            credentials.RetrievePassword();
            string refreshToken = credentials.Password;

            string accessToken = await GetAccessTokenAsync(refreshToken, clientId, clientSecret);
            if (string.IsNullOrEmpty(accessToken)) return;

            var state = await LoadSyncStateAsync();
            var newEtags = new Dictionary<string, string>();
            var newHashes = new Dictionary<string, string>();

            progress?.Report("Скачивание контактов из Google...");
            var googleContacts = await FetchGoogleContactsAsync(accessToken);
            if (googleContacts == null) return;

            progress?.Report("Подключение к локальной телефонной книге...");
            var contactStore = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AppContactsReadWrite);
            var contactLists = await contactStore.FindContactListsAsync();
            var myContactList = contactLists.FirstOrDefault(l => l.DisplayName == "Контакты Google");

            if (myContactList == null)
            {
                myContactList = await contactStore.CreateContactListAsync("Контакты Google");
                myContactList.OtherAppReadAccess = ContactListOtherAppReadAccess.Full;
                await myContactList.SaveAsync();
            }

            var syncedIds = await LoadSyncedIdsAsync();

            var existingContacts = new List<Contact>();
            var contactReader = myContactList.GetContactReader();
            var batch = await contactReader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                existingContacts.AddRange(batch.Contacts);
                batch = await contactReader.ReadBatchAsync();
            }

            int uploadedCount = 0; int downloadedCount = 0;
            int localDeletedCount = 0; int cloudDeletedCount = 0; int linkedCount = 0;

            // === ФАЗА 1: УДАЛЕНИЕ ИЗ GOOGLE ===
            int stepCounter = 0;
            var googleContactsList = googleContacts.ToList();
            foreach (var gc in googleContactsList)
            {
                stepCounter++;
                progress?.Report($"Сверка удалений в Google... {stepCounter}/{googleContactsList.Count}");

                var localMatch = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);
                if (localMatch == null && state.Etags.ContainsKey(gc.Id))
                {
                    bool deleted = await DeleteGoogleContactAsync(accessToken, gc.Id);
                    if (deleted)
                    {
                        googleContacts.Remove(gc);
                        cloudDeletedCount++;
                        state.Etags.Remove(gc.Id);
                        state.Hashes.Remove(gc.Id);
                    }
                }
            }

            // === ФАЗА 2: УДАЛЕНИЕ ИЗ ТЕЛЕФОНА ===
            var toDeleteLocally = existingContacts.Where(c => !string.IsNullOrEmpty(c.RemoteId) && !googleContacts.Any(gc => gc.Id == c.RemoteId)).ToList();
            stepCounter = 0;
            foreach (var lc in toDeleteLocally)
            {
                stepCounter++;
                progress?.Report($"Очистка удаленных из телефона... {stepCounter}/{toDeleteLocally.Count}");
                await myContactList.DeleteContactAsync(lc);
                existingContacts.Remove(lc);
                localDeletedCount++;
            }

            // === ГЛАВНЫЙ ЦИКЛ СИНХРОНИЗАЦИИ И РАЗРЕШЕНИЯ КОНФЛИКТОВ ===
            stepCounter = 0;
            foreach (var gc in googleContacts)
            {
                stepCounter++;
                progress?.Report($"Синхронизация контактов... {stepCounter}/{googleContacts.Count}");

                var lc = existingContacts.FirstOrDefault(c => c.RemoteId == gc.Id);
                string googleHash = CalculateHash(gc);

                if (lc != null) // Контакт есть и там, и там. Проверяем, КТО изменился.
                {
                    string localHash = CalculateHashLocal(lc);
                    string lastHash = state.Hashes.ContainsKey(gc.Id) ? state.Hashes[gc.Id] : "";
                    string lastEtag = state.Etags.ContainsKey(gc.Id) ? state.Etags[gc.Id] : "";

                    bool cloudChanged = !string.IsNullOrEmpty(lastEtag) && gc.ETag != lastEtag;
                    bool localChanged = !string.IsNullOrEmpty(lastHash) && localHash != lastHash;

                    if (cloudChanged)
                    {
                        await ApplyGoogleDataToLocalAsync(lc, gc, myContactList);
                        newHashes[gc.Id] = googleHash;
                        newEtags[gc.Id] = gc.ETag;
                        downloadedCount++;
                    }
                    else if (localChanged)
                    {
                        bool ok = await UpdateGoogleContactAsync(accessToken, lc, gc.ETag);
                        if (ok)
                        {
                            newHashes[gc.Id] = localHash;
                            newEtags[gc.Id] = gc.ETag;
                            uploadedCount++;
                        }
                        else
                        {
                            newHashes[gc.Id] = lastHash;
                            newEtags[gc.Id] = lastEtag;
                        }
                    }
                    else
                    {
                        newHashes[gc.Id] = googleHash;
                        newEtags[gc.Id] = gc.ETag;
                    }
                }
                else
                {
                    // НОВЫЙ КОНТАКТ ИЗ ОБЛАКА
                    var contact = new Contact { RemoteId = gc.Id };
                    await ApplyGoogleDataToLocalAsync(contact, gc, myContactList);

                    newEtags[gc.Id] = gc.ETag;
                    newHashes[gc.Id] = googleHash;
                    downloadedCount++;
                }
            }

            // === ВЫГРУЗКА НОВЫХ ЛОКАЛЬНЫХ ===
            var newLocals = existingContacts.Where(c => string.IsNullOrEmpty(c.RemoteId)).ToList();
            stepCounter = 0;
            foreach (var lc in newLocals)
            {
                stepCounter++;
                progress?.Report($"Выгрузка новых контактов в Google... {stepCounter}/{newLocals.Count}");

                string newId = await CreateGoogleContactAsync(accessToken, lc);
                if (!string.IsNullOrEmpty(newId))
                {
                    lc.RemoteId = newId;
                    await myContactList.SaveContactAsync(lc);
                    newHashes[newId] = CalculateHashLocal(lc);
                    uploadedCount++;
                }
            }

            progress?.Report("Сохранение состояния синхронизации...");
            await SaveSyncStateAsync(newEtags, newHashes);
            var newSyncedIds = existingContacts.Select(c => c.RemoteId).Where(id => !string.IsNullOrEmpty(id));
            await SaveSyncedIdsAsync(newSyncedIds);

            progress?.Report($"Готово! Скачано: {downloadedCount}, Обновлено(вверх): {uploadedCount}");
            System.Diagnostics.Debug.WriteLine($"=== ЗАВЕРШЕНО ===");
        }

        // Применяет распарсенные данные из Google к локальному объекту Contact
        private async Task ApplyGoogleDataToLocalAsync(Contact lc, GoogleContact gc, ContactList list)
        {
            lc.FirstName = gc.FirstName;
            lc.LastName = gc.LastName;

            lc.Phones.Clear();
            foreach (var p in gc.Phones) lc.Phones.Add(new ContactPhone { Number = p, Kind = ContactPhoneKind.Mobile });

            lc.Emails.Clear();
            foreach (var e in gc.Emails) lc.Emails.Add(new ContactEmail { Address = e, Kind = ContactEmailKind.Personal });

            lc.Addresses.Clear();
            foreach (var a in gc.Addresses) lc.Addresses.Add(new ContactAddress { StreetAddress = a, Kind = ContactAddressKind.Home });

            lc.Websites.Clear();
            foreach (var w in gc.Urls)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                Uri result;
                // Добавьте проверку Scheme (http/https), WinRT это любит
                if (Uri.TryCreate(w, UriKind.Absolute, out result) && (result.Scheme == "http" || result.Scheme == "https"))
                {
                    lc.Websites.Add(new ContactWebsite { Uri = result });
                }
            }

            lc.ImportantDates.Clear();
            if (gc.Birthday != null)
            {
                uint m = gc.Birthday.Month;
                uint d = gc.Birthday.Day;

                if (m >= 1 && m <= 12 && d >= 1 && d <= 31)
                {
                    lc.ImportantDates.Add(new ContactDate
                    {
                        Year = gc.Birthday.Year,
                        Month = gc.Birthday.Month,
                        Day = gc.Birthday.Day,
                        Kind = ContactDateKind.Birthday
                    });
                }
            }

            // Скачиваем фото, если есть URL
            if (!string.IsNullOrEmpty(gc.PhotoUrl) && !gc.PhotoUrl.Contains("default-user"))
            {
                try
                {
                    using (var hc = new HttpClient())
                    {
                        var stream = await hc.GetStreamAsync(gc.PhotoUrl);
                        var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        await RandomAccessStream.CopyAsync(stream.AsInputStream(), memStream.GetOutputStreamAt(0));
                        lc.SourceDisplayPicture = RandomAccessStreamReference.CreateFromStream(memStream);
                    }
                }
                catch { } // Игнорируем ошибки скачивания фото
            }

            await list.SaveContactAsync(lc);
        }

        private string CleanString(string input) => (input ?? "").Trim().ToLower();

        private string CalculateHash(GoogleContact gc)
        {
            string bday = gc.Birthday != null ? $"{gc.Birthday.Year}-{gc.Birthday.Month}-{gc.Birthday.Day}" : "";
            string raw = $"{CleanString(gc.FirstName)}|{CleanString(gc.LastName)}|" +
                         $"{string.Join(",", gc.Phones.Select(p => CleanString(p).Replace(" ", "").Replace("-", "")).OrderBy(p => p))}|" +
                         $"{string.Join(",", gc.Emails.Select(CleanString).OrderBy(e => e))}|" +
                         $"{string.Join(",", gc.Addresses.Select(CleanString).OrderBy(a => a))}|" +
                         $"{string.Join(",", gc.Urls.Select(CleanString).OrderBy(u => u))}|{bday}";
            return ComputeSha1(raw);
        }

        private string CalculateHashLocal(Contact lc)
        {
            string bday = "";
            var bDate = lc.ImportantDates.FirstOrDefault(d => d.Kind == ContactDateKind.Birthday);
            if (bDate != null) bday = $"{bDate.Year}-{bDate.Month}-{bDate.Day}";

            string raw = $"{CleanString(lc.FirstName)}|{CleanString(lc.LastName)}|" +
                         $"{string.Join(",", lc.Phones.Select(p => CleanString(p.Number).Replace(" ", "").Replace("-", "")).OrderBy(p => p))}|" +
                         $"{string.Join(",", lc.Emails.Select(e => CleanString(e.Address)).OrderBy(e => e))}|" +
                         $"{string.Join(",", lc.Addresses.Select(a => CleanString(a.StreetAddress)).OrderBy(a => a))}|" +
                         $"{string.Join(",", lc.Websites.Select(w => CleanString(w.Uri?.ToString())).OrderBy(u => u))}|{bday}";
            return ComputeSha1(raw);
        }

        private string ComputeSha1(string raw)
        {
            var buffer = Windows.Security.Cryptography.CryptographicBuffer.ConvertStringToBinary(raw, Windows.Security.Cryptography.BinaryStringEncoding.Utf8);
            var hashAlg = Windows.Security.Cryptography.Core.HashAlgorithmProvider.OpenAlgorithm(Windows.Security.Cryptography.Core.HashAlgorithmNames.Sha1);
            return Windows.Security.Cryptography.CryptographicBuffer.EncodeToHexString(hashAlg.HashData(buffer));
        }

        private async Task<bool> UpdateGoogleContactAsync(string accessToken, Contact localContact, string etag)
        {
            try
            {
                JsonObject person = BuildGoogleContactJson(localContact);
                person.SetNamedValue("etag", JsonValue.CreateStringValue(etag));

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    string resourceId = localContact.RemoteId;
                    if (!resourceId.StartsWith("people/")) resourceId = "people/" + resourceId;

                    string url = $"https://people.googleapis.com/v1/{resourceId}:updateContact?updatePersonFields=names,phoneNumbers,emailAddresses,addresses,urls,birthdays";

                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
                    {
                        Content = new StringContent(person.Stringify(), System.Text.Encoding.UTF8, "application/json")
                    };

                    var response = await client.SendAsync(request);
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private async Task<string> CreateGoogleContactAsync(string accessToken, Contact localContact)
        {
            try
            {
                JsonObject person = BuildGoogleContactJson(localContact);
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var content = new StringContent(person.Stringify(), System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync("https://people.googleapis.com/v1/people:createContact", content);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseJson = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                        if (responseJson.ContainsKey("resourceName")) return responseJson.GetNamedString("resourceName");
                    }
                }
            }
            catch { }
            return null;
        }

        private JsonObject BuildGoogleContactJson(Contact lc)
        {
            JsonObject person = new JsonObject();

            // Имена
            JsonArray names = new JsonArray();
            JsonObject nameObj = new JsonObject();
            nameObj.SetNamedValue("givenName", JsonValue.CreateStringValue(lc.FirstName ?? ""));
            nameObj.SetNamedValue("familyName", JsonValue.CreateStringValue(lc.LastName ?? ""));
            names.Add(nameObj);
            person.SetNamedValue("names", names);

            // Телефоны
            if (lc.Phones.Count > 0)
            {
                JsonArray phones = new JsonArray();
                foreach (var p in lc.Phones)
                    if (!string.IsNullOrEmpty(p.Number)) phones.Add(new JsonObject { { "value", JsonValue.CreateStringValue(p.Number) } });
                person.SetNamedValue("phoneNumbers", phones);
            }

            // Emails
            if (lc.Emails.Count > 0)
            {
                JsonArray emails = new JsonArray();
                foreach (var e in lc.Emails)
                    if (!string.IsNullOrEmpty(e.Address)) emails.Add(new JsonObject { { "value", JsonValue.CreateStringValue(e.Address) } });
                person.SetNamedValue("emailAddresses", emails);
            }

            // Адреса
            if (lc.Addresses.Count > 0)
            {
                JsonArray addresses = new JsonArray();
                foreach (var a in lc.Addresses)
                    if (!string.IsNullOrEmpty(a.StreetAddress)) addresses.Add(new JsonObject { { "streetAddress", JsonValue.CreateStringValue(a.StreetAddress) } });
                person.SetNamedValue("addresses", addresses);
            }

            // URLs (Соцсети)
            if (lc.Websites.Count > 0)
            {
                JsonArray urls = new JsonArray();
                foreach (var w in lc.Websites)
                    if (w.Uri != null) urls.Add(new JsonObject { { "value", JsonValue.CreateStringValue(w.Uri.ToString()) } });
                person.SetNamedValue("urls", urls);
            }

            // Дни рождения
            var bDate = lc.ImportantDates.FirstOrDefault(d => d.Kind == ContactDateKind.Birthday);
            if (bDate != null)
            {
                JsonArray birthdays = new JsonArray();
                JsonObject dateObj = new JsonObject();
                JsonObject dateInner = new JsonObject();
                if (bDate.Year != null) dateInner.SetNamedValue("year", JsonValue.CreateNumberValue((double)bDate.Year));
                if (bDate.Month != null) dateInner.SetNamedValue("month", JsonValue.CreateNumberValue((double)bDate.Month));
                if (bDate.Day != null) dateInner.SetNamedValue("day", JsonValue.CreateNumberValue((double)bDate.Day));
                dateObj.SetNamedValue("date", dateInner);
                birthdays.Add(dateObj);
                person.SetNamedValue("birthdays", birthdays);
            }

            return person;
        }

        private async Task<List<GoogleContact>> FetchGoogleContactsAsync(string accessToken)
        {
            var contactsList = new List<GoogleContact>();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                string nextPageToken = "";
                bool hasMorePages = true;

                while (hasMorePages)
                {
                    string requestUri = "https://people.googleapis.com/v1/people/me/connections?personFields=names,phoneNumbers,emailAddresses,addresses,urls,birthdays,photos&pageSize=1000";
                    if (!string.IsNullOrEmpty(nextPageToken)) requestUri += $"&pageToken={nextPageToken}";

                    var response = await client.GetAsync(requestUri);
                    if (response.IsSuccessStatusCode)
                    {
                        JsonObject json = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                        if (json.ContainsKey("connections"))
                        {
                            JsonArray connections = json.GetNamedArray("connections");
                            foreach (var item in connections)
                            {
                                var person = item.GetObject();
                                var parsedContact = new GoogleContact();

                                if (person.ContainsKey("resourceName")) parsedContact.Id = person.GetNamedString("resourceName");
                                if (person.ContainsKey("etag")) parsedContact.ETag = person.GetNamedString("etag");

                                if (person.ContainsKey("names"))
                                {
                                    var names = person.GetNamedArray("names");
                                    if (names.Count > 0)
                                    {
                                        var primaryName = names[0].GetObject();
                                        if (primaryName.ContainsKey("givenName")) parsedContact.FirstName = primaryName.GetNamedString("givenName");
                                        if (primaryName.ContainsKey("familyName")) parsedContact.LastName = primaryName.GetNamedString("familyName");
                                    }
                                }

                                if (person.ContainsKey("phoneNumbers"))
                                {
                                    foreach (var pItem in person.GetNamedArray("phoneNumbers"))
                                        if (pItem.GetObject().ContainsKey("value")) parsedContact.Phones.Add(pItem.GetObject().GetNamedString("value"));
                                }

                                if (person.ContainsKey("emailAddresses"))
                                {
                                    foreach (var eItem in person.GetNamedArray("emailAddresses"))
                                        if (eItem.GetObject().ContainsKey("value")) parsedContact.Emails.Add(eItem.GetObject().GetNamedString("value"));
                                }

                                if (person.ContainsKey("addresses"))
                                {
                                    foreach (var aItem in person.GetNamedArray("addresses"))
                                    {
                                        var addrObj = aItem.GetObject();
                                        if (addrObj.ContainsKey("formattedValue")) parsedContact.Addresses.Add(addrObj.GetNamedString("formattedValue"));
                                        else if (addrObj.ContainsKey("streetAddress")) parsedContact.Addresses.Add(addrObj.GetNamedString("streetAddress"));
                                    }
                                }

                                if (person.ContainsKey("urls"))
                                {
                                    foreach (var uItem in person.GetNamedArray("urls"))
                                        if (uItem.GetObject().ContainsKey("value")) parsedContact.Urls.Add(uItem.GetObject().GetNamedString("value"));
                                }

                                if (person.ContainsKey("birthdays"))
                                {
                                    var bdays = person.GetNamedArray("birthdays");
                                    if (bdays.Count > 0 && bdays[0].GetObject().ContainsKey("date"))
                                    {
                                        var dateObj = bdays[0].GetObject().GetNamedObject("date");
                                        parsedContact.Birthday = new GDate();
                                        if (dateObj.ContainsKey("year")) parsedContact.Birthday.Year = (int)dateObj.GetNamedNumber("year");
                                        if (dateObj.ContainsKey("month")) parsedContact.Birthday.Month = (uint)dateObj.GetNamedNumber("month");
                                        if (dateObj.ContainsKey("day")) parsedContact.Birthday.Day = (uint)dateObj.GetNamedNumber("day");
                                    }
                                }

                                if (person.ContainsKey("photos"))
                                {
                                    var photos = person.GetNamedArray("photos");
                                    if (photos.Count > 0 && photos[0].GetObject().ContainsKey("url"))
                                        parsedContact.PhotoUrl = photos[0].GetObject().GetNamedString("url");
                                }

                                if (!string.IsNullOrEmpty(parsedContact.FirstName) || parsedContact.Phones.Count > 0 || parsedContact.Emails.Count > 0)
                                {
                                    contactsList.Add(parsedContact);
                                }
                            }
                        }

                        if (json.ContainsKey("nextPageToken")) nextPageToken = json.GetNamedString("nextPageToken");
                        else hasMorePages = false;
                    }
                    else hasMorePages = false;
                }
            }
            return contactsList;
        }

        // --- Вспомогательные системные методы остаются без изменений ---
        private async Task<string> GetAccessTokenAsync(string refreshToken, string clientId, string clientSecret)
        {
            using (var client = new HttpClient())
            {
                string requestBody = $"client_id={clientId}&client_secret={clientSecret}&refresh_token={refreshToken}&grant_type=refresh_token";
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (response.IsSuccessStatusCode)
                {
                    JsonObject json = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                    return json.GetNamedString("access_token");
                }
            }
            return null;
        }

        private async Task<bool> DeleteGoogleContactAsync(string accessToken, string resourceName)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var response = await client.DeleteAsync($"https://people.googleapis.com/v1/{resourceName}:deleteContact");
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private async Task SaveSyncStateAsync(Dictionary<string, string> etags, Dictionary<string, string> hashes)
        {
            JsonObject root = new JsonObject();
            JsonObject etagsJson = new JsonObject(); JsonObject hashesJson = new JsonObject();
            foreach (var kvp in etags) etagsJson.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));
            foreach (var kvp in hashes) hashesJson.SetNamedValue(kvp.Key, JsonValue.CreateStringValue(kvp.Value));
            root.SetNamedValue("etags", etagsJson); root.SetNamedValue("hashes", hashesJson);
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync("sync_state.json", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, root.Stringify());
        }

        private async Task<SyncState> LoadSyncStateAsync()
        {
            var state = new SyncState();
            var item = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync("sync_state.json");
            if (item != null)
            {

                var file = item as Windows.Storage.StorageFile;
                if (file != null)
                {
                    try
                    {
                        string jsonString = await Windows.Storage.FileIO.ReadTextAsync(file);
                        if (!string.IsNullOrEmpty(jsonString))
                        {
                            JsonObject root = JsonObject.Parse(jsonString);
                            if (root.ContainsKey("etags"))
                            {
                                var etagsObj = root.GetNamedObject("etags");
                                foreach (var key in etagsObj.Keys) state.Etags[key] = etagsObj.GetNamedString(key);
                            }
                            if (root.ContainsKey("hashes"))
                            {
                                var hashesObj = root.GetNamedObject("hashes");
                                foreach (var key in hashesObj.Keys) state.Hashes[key] = hashesObj.GetNamedString(key);
                            }
                        }
                    }
                    catch { }
                }


            }
            return state;
        }

        private async Task SaveSyncedIdsAsync(IEnumerable<string> ids)
        {
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync("synced_ids.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, string.Join(",", ids));
        }

        private async Task<HashSet<string>> LoadSyncedIdsAsync()
        {
            try
            {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.GetFileAsync("synced_ids.txt");
                string content = await Windows.Storage.FileIO.ReadTextAsync(file);
                return new HashSet<string>(content.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            
            catch { }
            return new HashSet<string>();
        }
}

// Расширенный вспомогательный класс для новых полей
internal class GoogleContact
{
    public string Id { get; set; } = "";
    public string ETag { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public List<string> Phones { get; set; } = new List<string>();
    public List<string> Emails { get; set; } = new List<string>();
    public List<string> Addresses { get; set; } = new List<string>();
    public List<string> Urls { get; set; } = new List<string>();
    public GDate Birthday { get; set; } = null;
    public string PhotoUrl { get; set; } = "";
}

internal class GDate
{
    public int? Year { get; set; }
    public uint Month { get; set; }
    public uint Day { get; set; }
}
}