using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Data.Json;
using GoogleContactSync.Helpers;
using GoogleContactSync.Services;

namespace GoogleContactSync
{
    public sealed partial class MainPage : Page
    {
        private const string TaskName       = "GoogleContactSyncTask";
        private const string TaskEntryPoint = "SyncComponent.GoogleContactSyncTask";

        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey("ClientId"))
                    TxtClientId.Text = settings.Values["ClientId"].ToString();
                if (settings.Values.ContainsKey("ClientSecret"))
                    TxtClientSecret.Password = settings.Values["ClientSecret"].ToString();

                if (CredentialStorage.HasToken())
                    ShowLoggedInPanel();
                else
                    ShowLoginPanel();

                UpdateBgStatus();
            }
            catch (Exception ex)
            {
                ShowError("Startup error: " + ex.Message);
            }
        }

        private async void UpdateProgress(string msg)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                TxtProgress.Text = msg);
        }

        // ================================================================
        // LOAD client_secret.json
        // ================================================================
        private async void BtnLoadJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                picker.FileTypeFilter.Add(".json");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                string text = await FileIO.ReadTextAsync(file);
                var root    = JsonObject.Parse(text);

                // Handle both "installed" and "web" credential types
                JsonObject creds = null;
                if (root.ContainsKey("installed"))
                    creds = root.GetNamedObject("installed");
                else if (root.ContainsKey("web"))
                    creds = root.GetNamedObject("web");

                if (creds == null)
                {
                    ShowError("Unrecognized JSON. Expected 'installed' or 'web' key.");
                    return;
                }

                if (creds.ContainsKey("client_id"))
                    TxtClientId.Text = creds.GetNamedString("client_id");
                if (creds.ContainsKey("client_secret"))
                    TxtClientSecret.Password = creds.GetNamedString("client_secret");

                // Save to settings
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["ClientId"]     = TxtClientId.Text;
                settings.Values["ClientSecret"] = TxtClientSecret.Password;

                HideAllBanners();
            }
            catch (Exception ex)
            {
                ShowError("Failed to load JSON: " + ex.Message);
            }
        }

        // ================================================================
        // SIGN IN — Step 1
        // ================================================================
        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string clientId     = TxtClientId.Text.Trim();
            string clientSecret = TxtClientSecret.Password.Trim();

            if (string.IsNullOrEmpty(clientId) ||
                string.IsNullOrEmpty(clientSecret))
            {
                ShowError("Please enter Client ID and Client Secret.");
                return;
            }

            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["ClientId"]     = clientId;
            settings.Values["ClientSecret"] = clientSecret;

            BtnLogin.IsEnabled = false;
            HideAllBanners();

            try
            {
                var api    = new GoogleApiService(clientId, clientSecret);
                var result = await api.RequestDeviceCodeAsync();

                TxtVerificationUrl.Text = result.VerificationUrl;
                TxtUserCode.Text        = result.UserCode;

                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(result.UserCode);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

                PanelLogin.Visibility = Visibility.Collapsed;
                PanelCode.Visibility  = Visibility.Visible;

                PollForTokenAsync(result.DeviceCode, result.Interval,
                    clientId, clientSecret);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                BtnLogin.IsEnabled = true;
            }
        }

        // ================================================================
        // SIGN IN — Step 2: poll
        // ================================================================
        private async void PollForTokenAsync(string deviceCode, int interval,
            string clientId, string clientSecret)
        {
            var api = new GoogleApiService(clientId, clientSecret);
            try
            {
                while (true)
                {
                    await Task.Delay(interval * 1000);
                    string refreshToken = await api.PollForTokenAsync(deviceCode);
                    if (refreshToken != null)
                    {
                        CredentialStorage.SaveToken(refreshToken);
                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                            async () =>
                            {
                                ShowLoggedInPanel();
                                try { await RegisterBackgroundTaskAsync(); } catch { }
                                UpdateBgStatus();
                            });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    PanelCode.Visibility  = Visibility.Collapsed;
                    PanelLogin.Visibility = Visibility.Visible;
                    BtnLogin.IsEnabled    = true;
                    ShowError("Authorization failed: " + ex.Message);
                });
            }
        }

        // ================================================================
        // GOOGLE → PHONE (manual, one-direction)
        // ================================================================
        private async void BtnGoogleToPhone_Click(object sender, RoutedEventArgs e)
        {
            SetUiBusy(true);
            HideAllBanners();

            try
            {
                IProgress<string> progress = new Progress<string>(
                    msg => UpdateProgress(msg));

                string clientId     = GetSaved("ClientId");
                string clientSecret = GetSaved("ClientSecret");

                var api   = new GoogleApiService(clientId, clientSecret);
                var store = new ContactStoreService();

                var savedEtags = ContactHashStorage.LoadAll();
                bool isFirst   = savedEtags.Count == 0;

                if (isFirst)
                {
                    progress.Report("Fetching all contacts from Google...");
                    var contacts = await api.FetchAllContactsAsync(progress);

                    if (contacts.Count == 0)
                    {
                        ShowError("No contacts found in Google account.");
                        return;
                    }

                    int count = await store.SyncAllAsync(contacts, progress);
                    ShowSuccess(count + " contacts synced from Google.");
                }
                else
                {
                    progress.Report("Checking for changes on Google...");
                    var contacts    = await api.FetchAllContactsAsync(progress);
                    var serverEtags = new Dictionary<string, string>();
                    foreach (var c in contacts)
                        if (!string.IsNullOrEmpty(c.Id))
                            serverEtags[c.Id] = c.ETag;

                    int updated = 0;
                    int deleted = 0;

                    foreach (var gc in contacts)
                    {
                        if (!savedEtags.ContainsKey(gc.Id) ||
                            savedEtags[gc.Id] != gc.ETag)
                        {
                            await store.UpsertContactAsync(gc);
                            updated++;
                        }
                    }

                    foreach (var id in savedEtags.Keys)
                    {
                        if (!serverEtags.ContainsKey(id))
                        {
                            await store.DeleteContactAsync(id);
                            deleted++;
                        }
                    }

                    ContactHashStorage.SaveAll(serverEtags);

                    if (updated == 0 && deleted == 0)
                        ShowSuccess("All contacts up to date.");
                    else
                        ShowSuccess(updated + " updated, " + deleted + " deleted from phone.");
                }

                TxtLastSync.Text       = "Last sync: " + DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                TxtLastSync.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { SetUiBusy(false); }
        }

        // ================================================================
        // PHONE → GOOGLE (manual, one-direction)
        // ================================================================
        private async void BtnPhoneToGoogle_Click(object sender, RoutedEventArgs e)
        {
            SetUiBusy(true);
            HideAllBanners();

            try
            {
                IProgress<string> progress = new Progress<string>(
                    msg => UpdateProgress(msg));

                string clientId     = GetSaved("ClientId");
                string clientSecret = GetSaved("ClientSecret");

                var api   = new GoogleApiService(clientId, clientSecret);
                var store = new ContactStoreService();

                var phoneContacts = await store.ReadAllContactsAsync(progress);
                if (phoneContacts.Count == 0)
                {
                    ShowError("No contacts on phone. Run Google → Phone first.");
                    return;
                }

                var savedEtags = ContactHashStorage.LoadAll();
                var toUpload   = new List<Windows.ApplicationModel.Contacts.Contact>();

                foreach (var c in phoneContacts)
                    if (string.IsNullOrEmpty(c.RemoteId) ||
                        !savedEtags.ContainsKey(c.RemoteId))
                        toUpload.Add(c);

                if (toUpload.Count == 0)
                {
                    ShowSuccess("No new contacts to upload.");
                    return;
                }

                progress.Report("Uploading " + toUpload.Count + " contacts...");

                int uploaded = 0;
                int failed   = 0;

                foreach (var c in toUpload)
                {
                    if (string.IsNullOrEmpty(c.RemoteId))
                    {
                        string newId = await api.CreateContactAsync(c, progress);
                        if (newId != null)
                        {
                            await store.SaveRemoteIdAsync(c.Id, newId);
                            uploaded++;
                        }
                        else failed++;
                    }
                    else
                    {
                        bool ok = await api.UpdateContactAsync(
                            c, savedEtags.ContainsKey(c.RemoteId)
                                ? savedEtags[c.RemoteId] : "*", progress);
                        if (ok) uploaded++;
                        else    failed++;
                    }
                }

                string msg2 = uploaded + " contacts uploaded to Google.";
                if (failed > 0) msg2 += " " + failed + " failed.";
                ShowSuccess(msg2);

                TxtLastSync.Text       = "Last upload: " + DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                TxtLastSync.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { SetUiBusy(false); }
        }

        // ================================================================
        // BIDIRECTIONAL SYNC (uses SyncManager + conflict resolution)
        // ================================================================
        private async void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            SetUiBusy(true);
            HideAllBanners();

            try
            {
                IProgress<string> progress = new Progress<string>(
                    msg => UpdateProgress(msg));

                string clientId     = GetSaved("ClientId");
                string clientSecret = GetSaved("ClientSecret");

                var manager = new SyncManager(clientId, clientSecret);
                var result  = await manager.SyncAsync(progress);

                string summary = result.ToString();
                if (result.ConflictsGoogleWon + result.ConflictsLocalWon > 0)
                    summary += string.Format("\nConflicts: {0} Google won, {1} local won",
                        result.ConflictsGoogleWon, result.ConflictsLocalWon);
                ShowSuccess(summary);

                TxtLastSync.Text       = "Last sync: " + DateTime.Now.ToString("dd MMM yyyy  HH:mm");
                TxtLastSync.Visibility = Visibility.Visible;
                UpdateBgStatus();
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { SetUiBusy(false); }
        }

        // ================================================================
        // SIGN OUT
        // ================================================================
        private void BtnSignOut_Click(object sender, RoutedEventArgs e)
        {
            UnregisterBackgroundTask();
            CredentialStorage.DeleteToken();
            SyncStateStorage.Clear();
            ContactHashStorage.Clear();
            ShowLoginPanel();
            HideAllBanners();
            TxtLastSync.Visibility = Visibility.Collapsed;
            UpdateBgStatus();
        }

        // ================================================================
        // BACKGROUND TASK
        // ================================================================
        private async Task RegisterBackgroundTaskAsync()
        {
            foreach (var t in BackgroundTaskRegistration.AllTasks)
                if (t.Value.Name == TaskName) return;

            var status = await BackgroundExecutionManager.RequestAccessAsync();
            if (status == BackgroundAccessStatus.DeniedByUser ||
                status == BackgroundAccessStatus.DeniedBySystemPolicy) return;

            var builder = new BackgroundTaskBuilder
            {
                Name               = TaskName,
                TaskEntryPoint     = TaskEntryPoint,
                IsNetworkRequested = true
            };
            builder.SetTrigger(new TimeTrigger(15, false));
            builder.AddCondition(
                new SystemCondition(SystemConditionType.InternetAvailable));
            builder.Register();
        }

        private async void BtnRegisterBgTask_Click(object sender, RoutedEventArgs e)
        {
            BtnRegisterBgTask.IsEnabled = false;
            try
            {
                await RegisterBackgroundTaskAsync();
                UpdateBgStatus();
            }
            catch (Exception ex)
            {
                ShowError("Could not register auto sync: " + ex.Message);
            }
            finally { BtnRegisterBgTask.IsEnabled = true; }
        }

        private void UnregisterBackgroundTask()
        {
            foreach (var t in BackgroundTaskRegistration.AllTasks)
                if (t.Value.Name == TaskName)
                { t.Value.Unregister(true); return; }
        }

        private void UpdateBgStatus()
        {
            try
            {
                bool reg = false;
                foreach (var t in BackgroundTaskRegistration.AllTasks)
                    if (t.Value.Name == TaskName) { reg = true; break; }

                TxtBgStatus.Text = reg ? "Every 15 min" : "Not registered";
                TxtBgStatus.Foreground =
                    new Windows.UI.Xaml.Media.SolidColorBrush(
                        reg ? Windows.UI.Colors.Green : Windows.UI.Colors.Gray);

                BtnRegisterBgTask.Visibility = reg
                    ? Visibility.Collapsed : Visibility.Visible;

                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey("LastBgSync"))
                {
                    TxtLastBgSync.Text       = "Last: " + settings.Values["LastBgSync"];
                    TxtLastBgSync.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        // ================================================================
        // UI helpers
        // ================================================================
        private void ShowLoginPanel()
        {
            PanelLogin.Visibility    = Visibility.Visible;
            PanelCode.Visibility     = Visibility.Collapsed;
            PanelLoggedIn.Visibility = Visibility.Collapsed;
            BtnLogin.IsEnabled       = true;
        }

        private void ShowLoggedInPanel()
        {
            PanelLogin.Visibility    = Visibility.Collapsed;
            PanelCode.Visibility     = Visibility.Collapsed;
            PanelLoggedIn.Visibility = Visibility.Visible;
            TxtAccountInfo.Text      = "Google account connected";
        }

        private void SetUiBusy(bool busy)
        {
            BtnGoogleToPhone.IsEnabled = !busy;
            BtnPhoneToGoogle.IsEnabled = !busy;
            BtnSync.IsEnabled          = !busy;
            BtnSignOut.IsEnabled       = !busy;
            PanelProgress.Visibility   = busy
                ? Visibility.Visible : Visibility.Collapsed;
            if (!busy) TxtProgress.Text = string.Empty;
        }

        private void ShowSuccess(string msg)
        {
            TxtSuccess.Text          = msg;
            BannerSuccess.Visibility = Visibility.Visible;
            BannerError.Visibility   = Visibility.Collapsed;
        }

        private void ShowError(string msg)
        {
            TxtError.Text            = msg;
            BannerError.Visibility   = Visibility.Visible;
            BannerSuccess.Visibility = Visibility.Collapsed;
        }

        private void HideAllBanners()
        {
            BannerError.Visibility   = Visibility.Collapsed;
            BannerSuccess.Visibility = Visibility.Collapsed;
        }

        private string GetSaved(string key)
        {
            var s = ApplicationData.Current.LocalSettings;
            return s.Values.ContainsKey(key) ? s.Values[key].ToString() : "";
        }
    }
}
