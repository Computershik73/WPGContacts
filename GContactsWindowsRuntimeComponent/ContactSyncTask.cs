using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Data.Json;
using Windows.Security.Credentials;

namespace SyncComponent
{
    public sealed class ContactSyncTask : IBackgroundTask
    {
        // Ваши ключи из Google Cloud Console (OAuth 2.0 Client IDs для UWP/Native App)
        private const string ClientId = "ВАШ_CLIENT_ID.apps.googleusercontent.com";
        private const string ClientSecret = "ВАШ_CLIENT_SECRET";

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Обязательно берем deferral для работы с асинхронным кодом в фоне
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();

            try
            {
                // 1. Достаем сохраненный REFRESH_TOKEN из хранилища 
                // (он был сохранен в главном приложении после прохождения OAuth 2.0)
                var vault = new PasswordVault();
                var credentials = vault.FindAllByResource("GoogleSyncApp").FirstOrDefault();

                if (credentials == null) return;

                credentials.RetrievePassword();
                string refreshToken = credentials.Password; // Храним refresh_token вместо пароля

                // 2. Получаем свежий Access Token от Google
                string accessToken = await GetAccessTokenAsync(refreshToken);
                if (string.IsNullOrEmpty(accessToken)) return;

                // 3. Скачиваем контакты из Google People API
                var googleContacts = await FetchGoogleContactsAsync(accessToken);
                if (googleContacts == null || googleContacts.Count == 0) return;

                // 4. Подключаемся к системной телефонной книге
                // СТАЛО (вызываем без параметра, чтобы старый SDK не ругался):
                var contactStore = await Windows.ApplicationModel.Contacts.ContactManager.
                var contactLists = await contactStore.FindContactListsAsync();

                var myContactList = contactLists.FirstOrDefault(l => l.DisplayName == "Контакты Google");
                if (myContactList == null) return;

                // 5. Синхронизируем (сохраняем) контакты в телефон
                foreach (var gc in googleContacts)
                {
                    var contact = new Contact
                    {
                        FirstName = gc.FirstName,
                        LastName = gc.LastName,
                        Notes = "Синхронизировано из Google People API"
                    };

                    if (!string.IsNullOrEmpty(gc.Phone))
                    {
                        contact.Phones.Add(new ContactPhone
                        {
                            Number = gc.Phone,
                            Kind = ContactPhoneKind.Mobile
                        });
                    }

                    // Сохраняем контакт в Windows 10 Mobile
                    await myContactList.SaveContactAsync(contact);
                }
            }
            catch (Exception ex)
            {
                // Логирование ошибки (например, в ApplicationData.Current.LocalSettings)
                System.Diagnostics.Debug.WriteLine($"Ошибка фоновой синхронизации: {ex.Message}");
            }
            finally
            {
                // Завершаем фоновую задачу
                deferral.Complete();
            }
        }

        // Обновление токена доступа через Refresh Token
        private async Task<string> GetAccessTokenAsync(string refreshToken)
        {
            using (var client = new HttpClient())
            {
                var content = new StringContent(
                    $"client_id={ClientId}&client_secret={ClientSecret}&refresh_token={refreshToken}&grant_type=refresh_token",
                    System.Text.Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                );

                var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    JsonObject json = JsonObject.Parse(jsonString);
                    return json.GetNamedString("access_token");
                }
            }
            return null;
        }

        // Загрузка контактов через Google People API
        private async Task<System.Collections.Generic.List<GoogleContact>> FetchGoogleContactsAsync(string accessToken)
        {
            var contactsList = new System.Collections.Generic.List<GoogleContact>();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                // Запрашиваем имена и номера телефонов
                string requestUri = "https://people.googleapis.com/v1/people/me/connections?personFields=names,phoneNumbers";

                var response = await client.GetAsync(requestUri);
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    JsonObject json = JsonObject.Parse(jsonString);

                    if (json.ContainsKey("connections"))
                    {
                        JsonArray connections = json.GetNamedArray("connections");
                        foreach (var item in connections)
                        {
                            var person = item.GetObject();
                            var parsedContact = new GoogleContact();

                            // Парсим имя
                            if (person.ContainsKey("names"))
                            {
                                var names = person.GetNamedArray("names");
                                if (names.Count > 0)
                                {
                                    var primaryName = names[0].GetObject();
                                    parsedContact.FirstName = primaryName.ContainsKey("givenName") ? primaryName.GetNamedString("givenName") : "";
                                    parsedContact.LastName = primaryName.ContainsKey("familyName") ? primaryName.GetNamedString("familyName") : "";
                                }
                            }

                            // Парсим телефон
                            if (person.ContainsKey("phoneNumbers"))
                            {
                                var phones = person.GetNamedArray("phoneNumbers");
                                if (phones.Count > 0)
                                {
                                    parsedContact.Phone = phones[0].GetObject().GetNamedString("value");
                                }
                            }

                            // Добавляем, если есть хотя бы имя или телефон
                            if (!string.IsNullOrEmpty(parsedContact.FirstName) || !string.IsNullOrEmpty(parsedContact.Phone))
                            {
                                contactsList.Add(parsedContact);
                            }
                        }
                    }
                }
            }
            return contactsList;
        }
    }

    // Вспомогательный класс для хранения распарсенных данных
    internal class GoogleContact
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Phone { get; set; } = "";
    }
}