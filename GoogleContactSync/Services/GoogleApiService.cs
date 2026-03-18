// Services/GoogleApiService.cs
// Google People API + OAuth2 Device Flow.
// Handles authentication and all contact CRUD operations.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using GoogleContactSync.Models;

namespace GoogleContactSync.Services
{
    public class DeviceCodeResult
    {
        public string DeviceCode      { get; set; }
        public string UserCode        { get; set; }
        public string VerificationUrl { get; set; }
        public int    Interval        { get; set; }
    }

    public class GoogleApiService
    {
        private const string TokenUrl      = "https://oauth2.googleapis.com/token";
        private const string DeviceCodeUrl = "https://oauth2.googleapis.com/device/code";
        private const string PeopleUrl     = "https://people.googleapis.com/v1";
        private const string Scope         = "https://www.googleapis.com/auth/contacts";

        private readonly string _clientId;
        private readonly string _clientSecret;

        public GoogleApiService(string clientId, string clientSecret)
        {
            _clientId     = clientId;
            _clientSecret = clientSecret;
        }

        // ================================================================
        // OAUTH2 — Step 1: request device code
        // ================================================================
        public async Task<DeviceCodeResult> RequestDeviceCodeAsync()
        {
            using (var http = new HttpClient())
            {
                string body = "client_id=" + Uri.EscapeDataString(_clientId) +
                              "&scope=" + Uri.EscapeDataString(Scope);
                var response = await http.PostAsync(DeviceCodeUrl,
                    new StringContent(body, Encoding.UTF8,
                        "application/x-www-form-urlencoded"));

                if (!response.IsSuccessStatusCode)
                    throw new Exception("Failed to get device code: " +
                        (int)response.StatusCode);

                var json = JsonObject.Parse(
                    await response.Content.ReadAsStringAsync());

                return new DeviceCodeResult
                {
                    DeviceCode      = json.GetNamedString("device_code"),
                    UserCode        = json.GetNamedString("user_code"),
                    VerificationUrl = json.GetNamedString("verification_url"),
                    Interval        = (int)json.GetNamedNumber("interval", 5)
                };
            }
        }

        // ================================================================
        // OAUTH2 — Step 2: poll for token
        // Returns refresh token on success, null if still pending
        // Throws on real errors
        // ================================================================
        public async Task<string> PollForTokenAsync(string deviceCode)
        {
            using (var http = new HttpClient())
            {
                string body =
                    "client_id="     + Uri.EscapeDataString(_clientId) +
                    "&client_secret="+ Uri.EscapeDataString(_clientSecret) +
                    "&device_code="  + Uri.EscapeDataString(deviceCode) +
                    "&grant_type=urn:ietf:params:oauth:grant-type:device_code";

                var response = await http.PostAsync(TokenUrl,
                    new StringContent(body, Encoding.UTF8,
                        "application/x-www-form-urlencoded"));

                var json = JsonObject.Parse(
                    await response.Content.ReadAsStringAsync());

                if (response.IsSuccessStatusCode)
                    return json.GetNamedString("refresh_token");

                string error = json.ContainsKey("error")
                    ? json.GetNamedString("error") : "";

                if (error == "authorization_pending" || error == "slow_down")
                    return null; // still waiting

                throw new Exception("Auth error: " + error);
            }
        }

        // ================================================================
        // OAUTH2 — Refresh access token
        // ================================================================
        public async Task<string> GetAccessTokenAsync(string refreshToken)
        {
            using (var http = new HttpClient())
            {
                string body =
                    "client_id="     + Uri.EscapeDataString(_clientId) +
                    "&client_secret="+ Uri.EscapeDataString(_clientSecret) +
                    "&refresh_token="+ Uri.EscapeDataString(refreshToken) +
                    "&grant_type=refresh_token";

                var response = await http.PostAsync(TokenUrl,
                    new StringContent(body, Encoding.UTF8,
                        "application/x-www-form-urlencoded"));

                if (!response.IsSuccessStatusCode)
                    throw new Exception("Failed to refresh token: " +
                        (int)response.StatusCode);

                var json = JsonObject.Parse(
                    await response.Content.ReadAsStringAsync());
                return json.GetNamedString("access_token");
            }
        }

        // ================================================================
        // FETCH ALL contacts from Google
        // ================================================================
        public async Task<List<GoogleContact>> FetchAllContactsAsync(
            IProgress<string> progress = null)
        {
            string refreshToken = Helpers.CredentialStorage.LoadToken();
            string accessToken  = await GetAccessTokenAsync(refreshToken);

            var list           = new List<GoogleContact>();
            string nextToken   = "";
            int    page        = 0;

            using (var http = BuildHttpClient(accessToken))
            {
                while (true)
                {
                    page++;
                    string url = PeopleUrl +
                        "/people/me/connections" +
                        "?personFields=names,nicknames,phoneNumbers,emailAddresses," +
                        "addresses,urls,birthdays,photos,organizations,biographies,metadata" +
                        "&pageSize=1000";
                    if (!string.IsNullOrEmpty(nextToken))
                        url += "&pageToken=" + Uri.EscapeDataString(nextToken);

                    progress?.Report("Fetching contacts page " + page + "...");

                    var response = await http.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("People API error: " +
                            (int)response.StatusCode);

                    var json = JsonObject.Parse(
                        await response.Content.ReadAsStringAsync());

                    if (json.ContainsKey("connections"))
                    {
                        foreach (var item in json.GetNamedArray("connections"))
                            ParseContact(item.GetObject(), list);
                    }

                    if (json.ContainsKey("nextPageToken"))
                        nextToken = json.GetNamedString("nextPageToken");
                    else
                        break;
                }
            }

            progress?.Report("Fetched " + list.Count + " contacts from Google.");
            return list;
        }

        // ================================================================
        // CREATE contact on Google
        // Returns resourceName (id) on success, null on failure
        // ================================================================
        public async Task<string> CreateContactAsync(
            Windows.ApplicationModel.Contacts.Contact c,
            IProgress<string> progress = null)
        {
            try
            {
                string refreshToken = Helpers.CredentialStorage.LoadToken();
                string accessToken  = await GetAccessTokenAsync(refreshToken);

                var json = BuildPersonJson(c);
                using (var http = BuildHttpClient(accessToken))
                {
                    var response = await http.PostAsync(
                        PeopleUrl + "/people:createContact",
                        new StringContent(json.Stringify(),
                            Encoding.UTF8, "application/json"));

                    if (!response.IsSuccessStatusCode) return null;

                    var result = JsonObject.Parse(
                        await response.Content.ReadAsStringAsync());
                    return result.ContainsKey("resourceName")
                        ? result.GetNamedString("resourceName")
                        : null;
                }
            }
            catch { return null; }
        }

        // ================================================================
        // UPDATE contact on Google
        // ================================================================
        public async Task<bool> UpdateContactAsync(
            Windows.ApplicationModel.Contacts.Contact c,
            string etag,
            IProgress<string> progress = null)
        {
            try
            {
                string refreshToken = Helpers.CredentialStorage.LoadToken();
                string accessToken  = await GetAccessTokenAsync(refreshToken);

                var json = BuildPersonJson(c);
                json.SetNamedValue("etag", JsonValue.CreateStringValue(etag ?? "*"));

                string resourceId = c.RemoteId;
                if (!resourceId.StartsWith("people/"))
                    resourceId = "people/" + resourceId;

                string url = PeopleUrl + "/" + resourceId +
                    ":updateContact?updatePersonFields=" +
                    "names,nicknames,phoneNumbers,emailAddresses," +
                    "addresses,urls,birthdays,organizations,biographies";

                using (var http = BuildHttpClient(accessToken))
                {
                    var req = new HttpRequestMessage(
                        new HttpMethod("PATCH"), url)
                    {
                        Content = new StringContent(json.Stringify(),
                            Encoding.UTF8, "application/json")
                    };
                    var response = await http.SendAsync(req);
                    return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        // ================================================================
        // PRIVATE helpers
        // ================================================================
        private HttpClient BuildHttpClient(string accessToken)
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            return http;
        }

        private void ParseContact(JsonObject person, List<GoogleContact> list)
        {
            var gc = new GoogleContact();

            if (person.ContainsKey("resourceName"))
                gc.Id = person.GetNamedString("resourceName");
            if (person.ContainsKey("etag"))
                gc.ETag = person.GetNamedString("etag");

            // Extract latest updateTime from metadata sources
            if (person.ContainsKey("metadata"))
            {
                var meta = person.GetNamedObject("metadata");
                if (meta.ContainsKey("sources"))
                {
                    foreach (var src in meta.GetNamedArray("sources"))
                    {
                        var s = src.GetObject();
                        if (s.ContainsKey("updateTime"))
                        {
                            string t = s.GetNamedString("updateTime");
                            if (string.Compare(t, gc.UpdateTime,
                                System.StringComparison.Ordinal) > 0)
                                gc.UpdateTime = t;
                        }
                    }
                }
            }

            if (person.ContainsKey("names"))
            {
                var name = person.GetNamedArray("names")[0].GetObject();
                if (name.ContainsKey("givenName"))
                    gc.FirstName = name.GetNamedString("givenName");
                if (name.ContainsKey("familyName"))
                    gc.LastName = name.GetNamedString("familyName");
            }

            if (person.ContainsKey("nicknames"))
            {
                var nick = person.GetNamedArray("nicknames");
                if (nick.Count > 0 && nick[0].GetObject().ContainsKey("value"))
                    gc.Nickname = nick[0].GetObject().GetNamedString("value");
            }

            if (person.ContainsKey("biographies"))
            {
                var bios = person.GetNamedArray("biographies");
                if (bios.Count > 0 && bios[0].GetObject().ContainsKey("value"))
                    gc.Notes = bios[0].GetObject().GetNamedString("value");
            }

            if (person.ContainsKey("organizations"))
            {
                foreach (var o in person.GetNamedArray("organizations"))
                {
                    var obj = o.GetObject();
                    var org = new GOrg();
                    if (obj.ContainsKey("name"))  org.Name  = obj.GetNamedString("name");
                    if (obj.ContainsKey("title")) org.Title = obj.GetNamedString("title");
                    if (!string.IsNullOrEmpty(org.Name) ||
                        !string.IsNullOrEmpty(org.Title))
                        gc.Organizations.Add(org);
                }
            }

            if (person.ContainsKey("phoneNumbers"))
            {
                foreach (var p in person.GetNamedArray("phoneNumbers"))
                {
                    var obj = p.GetObject();
                    if (obj.ContainsKey("value"))
                        gc.Phones.Add(new GPhone
                        {
                            Number = obj.GetNamedString("value"),
                            Type   = obj.ContainsKey("type")
                                ? obj.GetNamedString("type").ToLower() : "other"
                        });
                }
            }

            if (person.ContainsKey("emailAddresses"))
            {
                foreach (var e in person.GetNamedArray("emailAddresses"))
                {
                    var obj = e.GetObject();
                    if (obj.ContainsKey("value"))
                        gc.Emails.Add(new GEmail
                        {
                            Address = obj.GetNamedString("value"),
                            Type    = obj.ContainsKey("type")
                                ? obj.GetNamedString("type").ToLower() : "other"
                        });
                }
            }

            if (person.ContainsKey("addresses"))
            {
                foreach (var a in person.GetNamedArray("addresses"))
                {
                    var obj  = a.GetObject();
                    var addr = new GAddress();
                    if (obj.ContainsKey("type"))          addr.Type       = obj.GetNamedString("type").ToLower();
                    if (obj.ContainsKey("streetAddress")) addr.Street     = obj.GetNamedString("streetAddress");
                    if (obj.ContainsKey("city"))          addr.City       = obj.GetNamedString("city");
                    if (obj.ContainsKey("region"))        addr.Region     = obj.GetNamedString("region");
                    if (obj.ContainsKey("postalCode"))    addr.PostalCode = obj.GetNamedString("postalCode");
                    if (obj.ContainsKey("country"))       addr.Country    = obj.GetNamedString("country");
                    if (!string.IsNullOrEmpty(addr.Street) ||
                        !string.IsNullOrEmpty(addr.City))
                        gc.Addresses.Add(addr);
                }
            }

            if (person.ContainsKey("urls"))
            {
                foreach (var u in person.GetNamedArray("urls"))
                {
                    var obj = u.GetObject();
                    if (obj.ContainsKey("value"))
                        gc.Urls.Add(new GUrl
                        {
                            Value = obj.GetNamedString("value"),
                            Type  = obj.ContainsKey("type")
                                ? obj.GetNamedString("type").ToLower() : "other"
                        });
                }
            }

            if (person.ContainsKey("birthdays"))
            {
                var bdays = person.GetNamedArray("birthdays");
                if (bdays.Count > 0 &&
                    bdays[0].GetObject().ContainsKey("date"))
                {
                    var d = bdays[0].GetObject().GetNamedObject("date");
                    gc.Birthday = new GDate();
                    if (d.ContainsKey("year"))  gc.Birthday.Year  = (int)d.GetNamedNumber("year");
                    if (d.ContainsKey("month")) gc.Birthday.Month = (uint)d.GetNamedNumber("month");
                    if (d.ContainsKey("day"))   gc.Birthday.Day   = (uint)d.GetNamedNumber("day");
                }
            }

            if (person.ContainsKey("photos"))
            {
                var photos = person.GetNamedArray("photos");
                if (photos.Count > 0 &&
                    photos[0].GetObject().ContainsKey("url"))
                    gc.PhotoUrl = photos[0].GetObject().GetNamedString("url");
            }

            if (!string.IsNullOrEmpty(gc.FirstName) ||
                !string.IsNullOrEmpty(gc.LastName)  ||
                gc.Phones.Count > 0 ||
                gc.Emails.Count > 0)
                list.Add(gc);
        }

        private JsonObject BuildPersonJson(
            Windows.ApplicationModel.Contacts.Contact c)
        {
            var person = new JsonObject();

            // Names
            var names    = new JsonArray();
            var nameObj  = new JsonObject();
            nameObj.SetNamedValue("givenName",
                JsonValue.CreateStringValue(c.FirstName ?? ""));
            nameObj.SetNamedValue("familyName",
                JsonValue.CreateStringValue(c.LastName ?? ""));
            names.Add(nameObj);
            person.SetNamedValue("names", names);

            // Nickname
            if (!string.IsNullOrEmpty(c.Nickname))
            {
                var nicks   = new JsonArray();
                var nickObj = new JsonObject();
                nickObj.SetNamedValue("value",
                    JsonValue.CreateStringValue(c.Nickname));
                nicks.Add(nickObj);
                person.SetNamedValue("nicknames", nicks);
            }

            // Notes
            if (!string.IsNullOrEmpty(c.Notes))
            {
                var bios   = new JsonArray();
                var bioObj = new JsonObject();
                bioObj.SetNamedValue("value",
                    JsonValue.CreateStringValue(c.Notes));
                bios.Add(bioObj);
                person.SetNamedValue("biographies", bios);
            }

            // Organizations
            if (c.JobInfo.Count > 0)
            {
                var orgs = new JsonArray();
                foreach (var job in c.JobInfo)
                {
                    var o = new JsonObject();
                    if (!string.IsNullOrEmpty(job.CompanyName))
                        o.SetNamedValue("name",
                            JsonValue.CreateStringValue(job.CompanyName));
                    if (!string.IsNullOrEmpty(job.Title))
                        o.SetNamedValue("title",
                            JsonValue.CreateStringValue(job.Title));
                    orgs.Add(o);
                }
                person.SetNamedValue("organizations", orgs);
            }

            // Phones
            if (c.Phones.Count > 0)
            {
                var phones = new JsonArray();
                foreach (var p in c.Phones)
                {
                    if (string.IsNullOrEmpty(p.Number)) continue;
                    string type = p.Description != null
                        ? p.Description.ToLower() : "mobile";
                    var o = new JsonObject();
                    o.SetNamedValue("value",
                        JsonValue.CreateStringValue(p.Number));
                    o.SetNamedValue("type",
                        JsonValue.CreateStringValue(type));
                    phones.Add(o);
                }
                person.SetNamedValue("phoneNumbers", phones);
            }

            // Emails
            if (c.Emails.Count > 0)
            {
                var emails = new JsonArray();
                foreach (var e in c.Emails)
                {
                    if (string.IsNullOrEmpty(e.Address)) continue;
                    string type = e.Kind ==
                        Windows.ApplicationModel.Contacts.ContactEmailKind.Work
                        ? "work" : "home";
                    var o = new JsonObject();
                    o.SetNamedValue("value",
                        JsonValue.CreateStringValue(e.Address));
                    o.SetNamedValue("type",
                        JsonValue.CreateStringValue(type));
                    emails.Add(o);
                }
                person.SetNamedValue("emailAddresses", emails);
            }

            // Addresses
            if (c.Addresses.Count > 0)
            {
                var addresses = new JsonArray();
                foreach (var a in c.Addresses)
                {
                    string type = a.Kind ==
                        Windows.ApplicationModel.Contacts.ContactAddressKind.Work
                        ? "work" : "home";
                    var o = new JsonObject();
                    o.SetNamedValue("type", JsonValue.CreateStringValue(type));
                    if (!string.IsNullOrEmpty(a.StreetAddress))
                        o.SetNamedValue("streetAddress",
                            JsonValue.CreateStringValue(a.StreetAddress));
                    if (!string.IsNullOrEmpty(a.Locality))
                        o.SetNamedValue("city",
                            JsonValue.CreateStringValue(a.Locality));
                    if (!string.IsNullOrEmpty(a.Region))
                        o.SetNamedValue("region",
                            JsonValue.CreateStringValue(a.Region));
                    if (!string.IsNullOrEmpty(a.PostalCode))
                        o.SetNamedValue("postalCode",
                            JsonValue.CreateStringValue(a.PostalCode));
                    if (!string.IsNullOrEmpty(a.Country))
                        o.SetNamedValue("country",
                            JsonValue.CreateStringValue(a.Country));
                    addresses.Add(o);
                }
                person.SetNamedValue("addresses", addresses);
            }

            // Websites
            if (c.Websites.Count > 0)
            {
                var urls = new JsonArray();
                foreach (var w in c.Websites)
                {
                    if (w.Uri == null) continue;
                    var o = new JsonObject();
                    o.SetNamedValue("value",
                        JsonValue.CreateStringValue(w.Uri.ToString()));
                    o.SetNamedValue("type",
                        JsonValue.CreateStringValue("other"));
                    urls.Add(o);
                }
                person.SetNamedValue("urls", urls);
            }

            // Birthday
            var bday = null as
                Windows.ApplicationModel.Contacts.ContactDate;
            foreach (var d in c.ImportantDates)
            {
                if (d.Kind ==
                    Windows.ApplicationModel.Contacts.ContactDateKind.Birthday)
                { bday = d; break; }
            }
            if (bday != null)
            {
                var birthdays = new JsonArray();
                var bdayObj   = new JsonObject();
                var dateObj   = new JsonObject();
                if (bday.Year.HasValue)
                    dateObj.SetNamedValue("year",
                        JsonValue.CreateNumberValue((double)bday.Year.Value));
                if (bday.Month.HasValue)
                    dateObj.SetNamedValue("month",
                        JsonValue.CreateNumberValue((double)bday.Month));
                if (bday.Day.HasValue)
                    dateObj.SetNamedValue("day",
                        JsonValue.CreateNumberValue((double)bday.Day));
                bdayObj.SetNamedValue("date", dateObj);
                birthdays.Add(bdayObj);
                person.SetNamedValue("birthdays", birthdays);
            }

            return person;
        }
    }
}
