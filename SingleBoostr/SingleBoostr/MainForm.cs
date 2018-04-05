using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Globalization;
using System.Net;
using System.Windows.Forms;
using System.Media;
using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using Steam4NET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SingleBoostr
{
    public partial class MainForm : Form
    {
        private Log _log;
        private Log _logChat;
        private SteamWeb _steamWeb;
        private DonateForm _donation;
        private SettingsForm _settings;
        private Dictionary<ulong, DateTime> _chatResponses = new Dictionary<ulong, DateTime>();

        private bool _canGoBack;
        private bool _appExiting;
        private bool _appCountWarningDisplayed;

        private Session _activeSession;
        private WindowPanel _activeWindow;
        private DateTime _idleTimeStarted;

        private List<App> _appList = new List<App>();
        private List<App> _appListActive = new List<App>();
        private List<App> _appListBadges = new List<App>();
        private List<App> _appListSelected = new List<App>();

        private App _appCurrentBadge;
            
        private ISteam006 _steam006;
        private IClientUser _clientUser;
        private ISteamApps001 _steamApps001;
        private ISteamApps003 _steamApps003;
        private IClientEngine _clientEngine;
        private ISteamUser016 _steamUser016;
        private IClientFriends _clientFriends;
        private ISteamClient012 _steamClient012;
        private ISteamFriends002 _steamFriends002;

        private int _user;
        private int _pipe;

        public MainForm()
        {
            InitializeComponent();
        }
        
        private enum WindowPanel
        {
            Start, Loading, Tos, Idle, IdleStarted, Cards, CardsStarted
        }
        
        private enum Session
        {
            None, Idle, Cards, CardsBatch
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x20000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _log = new Log("Main.txt");
            _logChat = new Log("Chat.txt");

            _settings = new SettingsForm();
            _donation = new DonateForm();

            Directory.CreateDirectory("Erreur");

            ServicePointManager.Expect100Continue = true;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls
                        | SecurityProtocolType.Tls11
                        | SecurityProtocolType.Tls12
                        | SecurityProtocolType.Ssl3;

            if (IsDuplicateAlreadyRunning())
                ExitApplication();

            /*Overwrites the cursor and renderer for the ContextMenuStrip we use for CardsStarted options panel.*/
            CardsStartedOptionsMenu.Cursor = Cursors.Hand;
            CardsStartedOptionsMenu.Renderer = new MyRenderer();

            if (!File.Exists(Const.GAME_EXE))
            {
                /*I don't know if it's a good idea to embed the game exe into the main program. That might [trigger] some Anti-Virus software.*/
                MsgBox.Show($"Fichier {Const.GAME_EXE} manquant, veuillez re-télécharger le programme.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                ExitApplication();
            }

            try
            {
                /*We need to register the application under Internet Explorer emulation registry key. Not doing this would cause the Steam Login web browser to render the css all wonky.*/
                RegistryKey ie_root = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
                RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true);
                key.SetValue(AppDomain.CurrentDomain.FriendlyName, 10001, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                _log.Write(Log.LogLevel.Error, $"Erreur lors de l'ajout de la clé de registre pour le support de l'émulation de navigateur. {ex.Message}");
                MsgBox.Show($"Impossible d'ajouter une clé de registre pour le support du navigateur. Les pages Web peuvent apparaître plutôt ... bancales.\n\n{ex.Message}", "Erreur du registre",
                    MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
            }

            /*Here we're just setting a placeholder text for the AppSearch textbox in the Idle panel*/
            if (!PanelIdleTxtSearch.IsDisposed)
                NativeMethods.SendMessage(PanelIdleTxtSearch.Handle, Const.EM_SETCUEBANNER, IntPtr.Zero, "Chercher un jeu");

            if (_settings.Settings.VACWarningDisplayed)
            {
                ShowWindow(WindowPanel.Loading);
                InitializeApp();
            }
            else
            {
                /*We'll display the Terms of Service in whatever language the user is running on his computer.
                 Provided that we have that translation, of course.*/
                string language = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
                PanelTosLblText.Text = localization.TermsOfService.GetTermsOfService(language);
                ShowWindow(WindowPanel.Tos);
            }

            PanelStartLblVersion.Text = $"v{Application.ProductVersion}";
            ToolTip.OwnerDraw = true;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopApps();
            _appExiting = true;
            BgwSteamCallback.CancelAsync();

            if (_settings.Settings.SaveAppIdleHistory)
            {
                _settings.Settings.GameHistoryIds = _appListSelected.Select(o => o.appid).ToList();
                _settings.SaveSettings();
            }

            if (_settings.Settings.ClearRecentlyPlayedOnExit)
            {
                /*These three games does not show up in the recently played section on your profile,
                 however they still take a spot. So essentially they do clear the recently played games.*/
                _appListActive.Clear();
                _appListActive.Add(new App() { appid = 399220 });
                _appListActive.Add(new App() { appid = 399080 });
                _appListActive.Add(new App() { appid = 399480 });
                StartApps(Session.Idle);
            }

            AppNotifyIcon.Visible = false;
            AppNotifyIcon.Dispose();
        }

        private void MainForm_Deactivate(object sender, EventArgs e)
        {
            /*We set the active control to a random label else when the user tabs out of the form
             it will automatically focus the next button which makes it get a clunky border
             which looks absolutely awful.*/
            ActiveControl = PanelUserLblName;
        }

        #region ButtonEvents

        private void PanelCardsStartedLblOptions_Click(object sender, EventArgs e)
        {
            /*The options button in CardsStarted opens a ContextMenuStrip on right click.
             So here we will make it work with left click as well.*/
            Label btnSender = (Label)sender;
            Point ptLowerLeft = new Point(0, btnSender.Height);
            ptLowerLeft = btnSender.PointToScreen(ptLowerLeft);
            CardsStartedOptionsMenu.Show(ptLowerLeft);
        }

        private void CardsStartedOptionsMenuBtnBlacklist_Click(object sender, EventArgs e)
        {
            if (_appCurrentBadge == null)
            {
                _log.Write(Log.LogLevel.Error, $"L'application actuelle est nulle. Impossible de l'ajouter à la liste de blocage. Hmmmm ...");
                return;
            }

            //https://github.com/dotnet/roslyn/pull/3507
            var diag = MsgBox.Show($"Voulez-vous mettre sur la liste noire {_appCurrentBadge.name}? Vous pouvez toujours annuler ceci dans les paramètres.", "Liste noire", MsgBox.Buttons.YesNo, MsgBox.MsgIcon.Question);
            if (diag == DialogResult.Yes)
            {
                _settings.Settings.BlacklistedCardGames.Add(_appCurrentBadge.appid);
                StartNextCard();
            }
        }

        private void CardsStartedOptionsMenuBtnSortQueue_Click(object sender, EventArgs e)
        {
            QueueForm frm = null;
            if (_settings.Settings.OnlyIdleGamesWithCertainMinutes)
            {
                int minimumMinutes = _settings.Settings.NumOnlyIdleGamesWithCertainMinutes;
                frm = new QueueForm(_appListBadges.Where(o => o.card.minutesplayed > minimumMinutes).ToList(), _appCurrentBadge);
            }
            else
            {
                frm = new QueueForm(_appListBadges, _appCurrentBadge);
            }

            if (frm.ShowDialog() == DialogResult.OK)
                _appListBadges = frm.AppList.Union(_appListBadges).ToList();
        }

        private void PanelStartLblVersion_Click(object sender, EventArgs e)
        {
            Process.Start(Const.REPO_RELEASE_URL);
        }

        private void MessageText_Click(object sender, EventArgs e, string url)
        {
            Process.Start(url);
        }

        private void CloseText_Click(object sender, EventArgs e)
        {
            Control control = (Control)sender;
            Control grandparent = control?.Parent?.Parent;

            if (grandparent != null)
                PanelStartChatPanel.Controls.Remove(grandparent);
        }

        private void showToolStripMenuItemExit_Click(object sender, EventArgs e)
        {
            ExitApplication();
        }

        private void PanelCardsStartedBtnHide_Click(object sender, EventArgs e)
        {
            if (_settings.Settings.HideToTraybar)
            {
                Hide();
                AppNotifyIcon.ShowBalloonTip(1000, "SingleBoostr", "Je suis ici.", ToolTipIcon.Info);
            }
            else
            {
                WindowState = FormWindowState.Minimized;
            }
        }

        private void PanelIdleStartedBtnHide_Click(object sender, EventArgs e)
        {
            if (_settings.Settings.HideToTraybar)
            {
                Hide();
                AppNotifyIcon.ShowBalloonTip(1000, "SingleBoostr", "Je suis ici.", ToolTipIcon.Info);
            }
            else
            {
                WindowState = FormWindowState.Minimized;
            }
        }

        private void PanelCardsStartedBtnNext_Click(object sender, EventArgs e)
        {
            _log.Write(Log.LogLevel.Info, $"Passer le jeu actuel");
            PanelCardsStartedBtnNext.Enabled = false;
            StartNextCard();
        }

        private void PanelIdleStartedBtnStop_Click(object sender, EventArgs e)
        {
            StopApps();
        }

        private void PanelCardsStartedBtnStopIdle_Click(object sender, EventArgs e)
        {
            StopApps();
        }

        private void PanelIdleLblClear_Click(object sender, EventArgs e)
        {
            _appList.AddRange(_appListSelected);
            _appListSelected.Clear();
            RefreshGameList();
        }

        private void PanelStartBtnIdle_Click(object sender, EventArgs e)
        {
            switch (_activeSession)
            {
                case Session.Cards:
                    var diag = MsgBox.Show("Vous êtes déjà en train de farm des cartes à collectionner. Voulez-vous arrêter cela et ne pas utiliser d'autres applications?",
                    "Déjà actif", MsgBox.Buttons.YesNo, MsgBox.MsgIcon.Question);
                    if (diag == DialogResult.Yes)
                    {
                        StopApps();
                        ShowWindow(WindowPanel.Idle);
                    }
                    break;

                case Session.Idle:
                    ShowWindow(WindowPanel.IdleStarted);
                    break;

                case Session.None:
                    ShowWindow(WindowPanel.Idle);
                    break;
            }
        }

        private void PanelStartBtnCards_Click(object sender, EventArgs e)
        {
            switch (_activeSession)
            {
                case Session.Cards:
                    ShowWindow(WindowPanel.CardsStarted);
                    break;

                case Session.Idle:
                    var diag = MsgBox.Show("Vous booster déjà un autre jeu\nVoulez-vous arrêter cela et commencer à farm des cartes?",
                    "Déjà actif", MsgBox.Buttons.YesNo, MsgBox.MsgIcon.Question);
                    if (diag == DialogResult.Yes)
                    {
                        StopApps();
                        goto case Session.None;
                    }
                    break;

                case Session.None:
                    if (_settings.Settings.WebSession.IsLoggedIn())
                    {
                        _steamWeb = new SteamWeb(_settings.Settings.WebSession);
                        ShowWindow(WindowPanel.Loading);
                        StartCardsFarming();
                    }
                    else
                    {
                        ShowWindow(WindowPanel.Cards);
                    }
                    break;
            }
        }

        private void PanelUserPicGoBack_Click(object sender, EventArgs e)
        {
            if (_canGoBack)
            {
                if (_activeWindow == WindowPanel.Start)
                {
                    switch (_activeSession)
                    {
                        case Session.Cards:
                            ShowWindow(WindowPanel.CardsStarted);
                            break;

                        case Session.Idle:
                        case Session.CardsBatch:
                            ShowWindow(WindowPanel.IdleStarted);
                            break;
                    }
                }
                else
                {
                    ShowWindow(WindowPanel.Start);
                }
            }
        }

        private void PanelidleBtnIdle_Click(object sender, EventArgs e)
        {
            if (_appListSelected.Count > 0)
            {
                if (_activeSession == Session.None)
                {
                    _appListActive = _appListSelected.ToList();
                    StartApps(Session.Idle);
                }
            }
            else
            {
                MsgBox.Show("Vous n'avez sélectionné aucun jeu.", "Boost d'heures", MsgBox.Buttons.OK, MsgBox.MsgIcon.Info);
            }
        }

        private async void PanelCardsBtnLogin_Click(object sender, EventArgs e)
        {
            var browser = new BrowserForm();
            if (browser.ShowDialog() == DialogResult.OK)
            {
                if (browser.Session.IsLoggedIn())
                {
                    ShowWindow(WindowPanel.Loading);
                    if (_settings.Settings.SaveLoginCookies)
                        _settings.AddBrowserSessionInfo(browser.Session);

                    _steamWeb = new SteamWeb(_settings.Settings.WebSession);
                    if (_settings.Settings.JoinSteamGroup)
                    {
                        string joinGroupUrl = $"{Const.STEAM_GROUP_URL}?sessionID={browser.Session.SessionId}&action=join";
                        string resp = await _steamWeb.Request(joinGroupUrl);
                    }

                    StartCardsFarming();
                    return;
                }
            }

            ShowWindow(WindowPanel.Start);
        }

        private void PanelStartBtnExit_Click(object sender, EventArgs e)
        {
            ExitApplication();
        }

        private void PanelStartBtnSettings_Click(object sender, EventArgs e)
        {
            _settings.ShowDialog();
        }

        private void PanelStartBtnDonate_Click(object sender, EventArgs e)
        {
            _donation.ShowDialog();
        }

        private void PanelTosBtnAccept_Click(object sender, EventArgs e)
        {
            _settings.Settings.VACWarningDisplayed = true;
            ShowWindow(WindowPanel.Loading);
            InitializeApp();
        }

        private void PanelTosBtnDecline_Click(object sender, EventArgs e)
        {
            ExitApplication();
        }

        private void PanelStartPicGithub_Click(object sender, EventArgs e)
        {
            Process.Start(Const.GITHUB_PROFILE_URL);
        }

        private void AppNotifyIcon_Click(object sender, EventArgs e)
        {
            Show();
        }

        #endregion ButtonEvents

        #region OtherEvents

        private void ToolTip_Draw(object sender, DrawToolTipEventArgs e)
        {
            e.DrawBackground();
            e.DrawText();
        }

        private void BgwSteamCallback_DoWork(object sender, DoWorkEventArgs e)
        {
            int callbackErrors = 0;
            var callbackMsg = new CallbackMsg_t();
            while (!BgwSteamCallback.CancellationPending)
            {
                try
                {
                    while (Steamworks.GetCallback(_pipe, ref callbackMsg))
                    {
                        switch (callbackMsg.m_iCallback)
                        {
                            case FriendChatMsg_t.k_iCallback:
                                var msg = (FriendChatMsg_t)Marshal.PtrToStructure(callbackMsg.m_pubParam, typeof(FriendChatMsg_t));
                                if ((EChatEntryType)msg.m_eChatEntryType == EChatEntryType.k_EChatEntryTypeChatMsg)
                                {
                                    var data = new Byte[4096];
                                    EChatEntryType type = EChatEntryType.k_EChatEntryTypeChatMsg;
                                    var length = _steamFriends002.GetChatMessage(msg.m_ulFriendID, (int)msg.m_iChatID, data, ref type);

                                    string message = Encoding.UTF8.GetString(data, 0, length).Replace("\0", "");
                                    string senderName = _steamFriends002.GetFriendPersonaName(msg.m_ulSenderID);
                                    OnFriendChatMsg(message, senderName, msg.m_ulSenderID, msg.m_ulFriendID);
                                }
                                break;

                            case PersonaStateChange_t.k_iCallback:
                                var persona = (PersonaStateChange_t)Marshal.PtrToStructure(callbackMsg.m_pubParam, typeof(PersonaStateChange_t));
                                if (persona.m_ulSteamID == _clientUser.GetSteamID())
                                    onPersonaChange(persona.m_nChangeFlags);
                                break;

                            case LobbyInvite_t.k_iCallback:
                                var invite = (LobbyInvite_t)Marshal.PtrToStructure(callbackMsg.m_pubParam, typeof(LobbyInvite_t));
                                OnLobbyInvite(invite.m_ulSteamIDUser, invite.m_ulGameID);
                                break;

                            case AccountInformationUpdated_t.k_iCallback:
                                SetUserInfo();
                                break;

                            case UpdateItemAnnouncement_t.k_iCallback:
                                OnNewItem((UpdateItemAnnouncement_t)Marshal.PtrToStructure(callbackMsg.m_pubParam, typeof(UpdateItemAnnouncement_t)));
                                break;
                        }

                        if (!Steamworks.FreeLastCallback(_pipe))
                            callbackErrors = 0;
                    }
                }
                catch (Exception ex)
                {
                    _log.Write(Log.LogLevel.Error, $"Une exception s'est produite lors de la gestion du rappel Steam. {ex.Message}");

                    if (++callbackErrors > 5)
                        break;
                }
                finally
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void onPersonaChange(EPersonaChange change)
        {
            if (change == EPersonaChange.k_EPersonaChangeStatus && _steamFriends002.GetPersonaState() != EPersonaState.k_EPersonaStateOnline)
            {
                if (_settings.Settings.ForceOnlineStatus)
                {
                    _steamFriends002.SetPersonaState(EPersonaState.k_EPersonaStateOnline);
                }
            }
        }

        private void BgwSteamCallback_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (!_appExiting)
            {
                _log.Write(Log.LogLevel.Error, "Trop d'erreurs se sont produites dans la callback.");
                MsgBox.Show("Impossible de se connecter à Steam. Le client Steam fonctionne-t-il correctement? Redémarrez le logiciel pour que les choses fonctionnent correctement. Veuillez signaler cette erreur.", 
                    "Erreur", MsgBox.Buttons.Fuck, MsgBox.MsgIcon.Error);
            }
        }

        private async void TmrChangeBackground_Tick(object sender, EventArgs e)
        {
            TmrChangeBackground.Stop();

            var firstApp = _appListActive.FirstOrDefault();
            if (firstApp != null)
            {
                Bitmap bg = await GetAppBackground(firstApp.appid);
                if (bg != null)
                {
                    BackgroundImage = bg;
                }
            }
        }

        private void TmrCheckCardProgress_Tick(object sender, EventArgs e)
        {
            _log.Write(Log.LogLevel.Warn, $"Vérification de la progression des cartes pour s'assurer que nous ne sommes pas bloqués.");
            CheckCurrentBadge();
        }

        private void TmrCheckProcess_Tick(object sender, EventArgs e)
        {
            foreach (var app in _appListActive)
            {
                if (app.process == null)
                    continue;

                if (app.process.HasExited && _activeSession != Session.None)
                {
                    app.process.Start();
                    _log.Write(Log.LogLevel.Info, $"{app.name} à été quitter et a maintenant été redémarré.");
                }
            }
        }

        private void TmrRestartApp_Tick(object sender, EventArgs e)
        {
            if (_settings.Settings.RestartGamesAtRandom)
            {
                var app = _appListActive[Utils.GetRandom().Next(_appListActive.Count)];
                if (!app.process.HasExited)
                {
                    try
                    {
                        app.process.Kill();
                        _log.Write(Log.LogLevel.Info, $"Redémarré au hasard {app.name}");
                    }
                    catch (Exception ex)
                    {
                        _log.Write(Log.LogLevel.Error, $"Impossible de redémarrer {app.name}. Erreur: {ex.Message}");
                    }
                }
            }
            else
            {
                foreach (var app in _appListActive)
                {
                    if (!app.process.HasExited)
                    {
                        try
                        {
                            app.process.Kill();
                            _log.Write(Log.LogLevel.Info, $"Redémarré {app.name}");
                        }
                        catch (Exception ex)
                        {
                            _log.Write(Log.LogLevel.Error, $"Impossible de redémarrer {app.name}. Erreur: {ex.Message}");
                        }
                    }
                }
            }

            SetRandomTmrRestartAppInterval();
        }

        private void TmrIdleTime_Tick(object sender, EventArgs e)
        {
            var elapsedDate = DateTime.Now.Subtract(_idleTimeStarted);
            PanelIdleStartedLblIdleTime.Text = $"Vous avez boost vos heures pour {string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D2}", elapsedDate.Days, elapsedDate.Hours, elapsedDate.Minutes, elapsedDate.Seconds)}";
        }

        private async void TmrCardBatchCheck_Tick(object sender, EventArgs e)
        {
            _appListBadges = await LoadBadges();
            if (_appListBadges == null)
            {
                ShowWindow(WindowPanel.Cards);
                StopApps();

                MsgBox.Show("Impossible de lire les badges Steam. Ré-authentifier vous en vous connectant à Steam à nouveau.",
                    "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
            }
            else
            {
                StartNextCard();
            }
        }

        private void PanelIdleListGames_SelectedIndexChanged(object sender, EventArgs e)
        {
            var item = PanelIdleListGames.SelectedItem;
            if (item == null)
                return;

            var game = _appList.FirstOrDefault(o => o.GetIdAndName() == (string)item);
            if (game == null)
                return;

            _appListSelected.Add(game);
            _appList.Remove(game);
            RefreshGameList();
        }

        private void PanelIdleListGamesSelected_SelectedIndexChanged(object sender, EventArgs e)
        {
            var item = PanelIdleListGamesSelected.SelectedItem;
            if (item == null)
                return;

            var game = _appListSelected.FirstOrDefault(o => o.GetIdAndName() == (string)item);
            if (game == null)
                return;

            _appListSelected.Remove(game);
            _appList.Add(game);
            RefreshGameList();
        }

        private void PanelIdleTxtSearch_TextChanged(object sender, EventArgs e)
        {
            RefreshGameList();
        }

        #endregion OtherEvents

        #region MoveForm

        private void PanelTosLblText_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelUser_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelCards_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelCardsStarted_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelIdle_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelStart_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelIdleStarted_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelLoading_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void PanelTos_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        #endregion MoveForm

        #region UIStyle

        private void MessageText_MouseLeave(object sender, EventArgs e)
        {
            Label lbl = (Label)sender;
            lbl.ForeColor = Const.LABEL_NORMAL;
            lbl.Cursor = Cursors.Default;
        }

        private void MessageText_MouseEnter(object sender, EventArgs e)
        {
            Label lbl = (Label)sender;
            lbl.ForeColor = Const.LABEL_HOVER;
            lbl.Cursor = Cursors.Hand;
        }

        private void CloseText_MouseLeave(object sender, EventArgs e)
        {
            Label lbl = (Label)sender;
            lbl.ForeColor = Const.LABEL_NORMAL;
        }

        private void CloseText_MouseEnter(object sender, EventArgs e)
        {
            Label lbl = (Label)sender;
            lbl.ForeColor = Const.LABEL_HOVER;
        }

        private void PanelIdleStartedBtnHide_MouseEnter(object sender, EventArgs e)
        {
            PanelIdleStartedBtnHide.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelIdleStartedBtnHide_MouseLeave(object sender, EventArgs e)
        {
            PanelIdleStartedBtnHide.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelCardsStartedBtnHide_MouseEnter(object sender, EventArgs e)
        {
            PanelCardsStartedBtnHide.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelCardsStartedBtnHide_MouseLeave(object sender, EventArgs e)
        {
            PanelCardsStartedBtnHide.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelCardsStartedBtnNext_MouseEnter(object sender, EventArgs e)
        {
            PanelCardsStartedBtnNext.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelCardsStartedBtnNext_MouseLeave(object sender, EventArgs e)
        {
            PanelCardsStartedBtnNext.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelTosBtnAccept_MouseEnter(object sender, EventArgs e)
        {
            PanelTosBtnAccept.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelTosBtnAccept_MouseLeave(object sender, EventArgs e)
        {
            PanelTosBtnAccept.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelTosBtnDecline_MouseEnter(object sender, EventArgs e)
        {
            PanelTosBtnDecline.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelTosBtnDecline_MouseLeave(object sender, EventArgs e)
        {
            PanelTosBtnDecline.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelStartBtnIdle_MouseEnter(object sender, EventArgs e)
        {
            PanelStartBtnIdle.BackgroundImage = Properties.Resources.Idle_Selected;
        }

        private void PanelStartBtnIdle_MouseLeave(object sender, EventArgs e)
        {
            PanelStartBtnIdle.BackgroundImage = Properties.Resources.Idle;
        }

        private void PanelStartBtnCards_MouseEnter(object sender, EventArgs e)
        {
            PanelStartBtnCards.BackgroundImage = Properties.Resources.Cards_Selected;
        }

        private void PanelStartBtnCards_MouseLeave(object sender, EventArgs e)
        {
            PanelStartBtnCards.BackgroundImage = Properties.Resources.Cards;
        }

        private void PanelUserPicGoBack_MouseEnter(object sender, EventArgs e)
        {
            if (_canGoBack)
            {
                if (_activeSession != Session.None && _activeWindow == WindowPanel.Start)
                {
                    PanelUserPicGoBack.BackgroundImage = Properties.Resources.Active_Selected;
                }
                else
                {
                    PanelUserPicGoBack.BackgroundImage = Properties.Resources.Back_Selected;
                }
            }
        }

        private void PanelUserPicGoBack_MouseLeave(object sender, EventArgs e)
        {
            if (_canGoBack)
            {
                if (_activeSession != Session.None && _activeWindow == WindowPanel.Start)
                {
                    PanelUserPicGoBack.BackgroundImage = Properties.Resources.Active;
                }
                else
                {
                    PanelUserPicGoBack.BackgroundImage = Properties.Resources.Back;
                }
            }
        }

        private void PanelStartBtnSettings_MouseEnter(object sender, EventArgs e)
        {
            PanelStartBtnSettings.BackgroundImage = Properties.Resources.Settings_Selected;
        }

        private void PanelStartBtnSettings_MouseLeave(object sender, EventArgs e)
        {
            PanelStartBtnSettings.BackgroundImage = Properties.Resources.Settings;
        }

        private void PanelStartBtnDonate_MouseEnter(object sender, EventArgs e)
        {
            PanelStartBtnDonate.BackgroundImage = Properties.Resources.Donate_Selected;
        }

        private void PanelStartBtnDonate_MouseLeave(object sender, EventArgs e)
        {
            PanelStartBtnDonate.BackgroundImage = Properties.Resources.Donate;
        }

        private void PanelStartBtnExit_MouseEnter(object sender, EventArgs e)
        {
            PanelStartBtnExit.BackgroundImage = Properties.Resources.Exit_Selected;
        }

        private void PanelStartBtnExit_MouseLeave(object sender, EventArgs e)
        {
            PanelStartBtnExit.BackgroundImage = Properties.Resources.Exit;
        }

        private void PanelCardsStartedBtnStopIdle_MouseEnter(object sender, EventArgs e)
        {
            PanelCardsStartedBtnStopIdle.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelCardsStartedBtnStopIdle_MouseLeave(object sender, EventArgs e)
        {
            PanelCardsStartedBtnStopIdle.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelCardsBtnLogin_MouseEnter(object sender, EventArgs e)
        {
            PanelCardsBtnLogin.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelCardsBtnLogin_MouseLeave(object sender, EventArgs e)
        {
            PanelCardsBtnLogin.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelidleBtnIdle_MouseEnter(object sender, EventArgs e)
        {
            PanelIdleBtnIdle.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelidleBtnIdle_MouseLeave(object sender, EventArgs e)
        {
            PanelIdleBtnIdle.BackgroundImage = Properties.Resources.Button;
        }

        private void PanelidleLblClear_MouseEnter(object sender, EventArgs e)
        {
            PanelIdleLblClear.ForeColor = Const.LABEL_HOVER;
        }

        private void PanelidleLblClear_MouseLeave(object sender, EventArgs e)
        {
            PanelIdleLblClear.ForeColor = Const.LABEL_NORMAL;
        }

        private void PanelIdleStartedBtnStop_MouseEnter(object sender, EventArgs e)
        {
            PanelIdleStartedBtnStop.BackgroundImage = Properties.Resources.Button_Selected;
        }

        private void PanelIdleStartedBtnStop_MouseLeave(object sender, EventArgs e)
        {
            PanelIdleStartedBtnStop.BackgroundImage = Properties.Resources.Button;
        }

        #endregion UIStyle

        #region Functions

        private void ShowChatBubble(string title, string text, string url = "")
        {
            int tag = PanelStartChatPanel.Controls.Count + 1;

            Panel container = new Panel();
            container.Size = new Size(310, 58);
            container.BackgroundImage = Properties.Resources.Chat;
            container.Tag = tag;

            Panel textWrapper = new Panel();
            textWrapper.Dock = DockStyle.Fill;
            textWrapper.Padding = new Padding(2, 0, 0, 0);

            Panel buttonWrapper = new Panel();
            buttonWrapper.Size = new Size(20, 58);
            buttonWrapper.Dock = DockStyle.Right;

            Label closeText = new Label();
            closeText.Text = "x";
            closeText.Dock = DockStyle.Top;
            closeText.Cursor = Cursors.Hand;
            closeText.ForeColor = Color.Gray;
            closeText.Click += CloseText_Click;
            closeText.MouseEnter += CloseText_MouseEnter;
            closeText.MouseLeave += CloseText_MouseLeave;

            Label messageText = new Label();
            messageText.Text = Utils.Truncate(text, 105);
            messageText.AutoSize = false;
            messageText.Dock = DockStyle.Fill;
            messageText.ForeColor = Color.Gray;
            messageText.Font = new Font("Segoe UI", 8, FontStyle.Regular);
            messageText.Padding = new Padding(1, 0, 0, 0);
            if (!string.IsNullOrEmpty(url))
            {
                Uri uriResult;
                if (Uri.TryCreate(url, UriKind.Absolute, out uriResult))
                {
                    messageText.MouseEnter += MessageText_MouseEnter;
                    messageText.MouseLeave += MessageText_MouseLeave;
                    messageText.Click += (sender, EventArgs) => { MessageText_Click(sender, EventArgs, url); };
                    ToolTip.SetToolTip(messageText, url);
                }
            }

            Label titleText = new Label();
            titleText.Text = title;
            titleText.Dock = DockStyle.Top;
            titleText.Height = 20;
            titleText.ForeColor = Color.Gray;
            titleText.Font = new Font("Segoe UI", 10, FontStyle.Regular);

            buttonWrapper.Controls.Add(closeText);
            textWrapper.Controls.Add(messageText);
            textWrapper.Controls.Add(titleText);
            container.Controls.Add(textWrapper);
            container.Controls.Add(buttonWrapper);

            Invoke(new Action(() =>
            {
                PanelStartChatPanel.Controls.Add(container);

                if (PanelStartChatPanel.Controls.Count > 4)
                    PanelStartChatPanel.Controls.RemoveAt(0);
            }));
        }

        private void StartApps(Session session)
        {
            _idleTimeStarted = DateTime.Now;
            foreach (var app in _appListActive)
            {
                var pinfo = new ProcessStartInfo()
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    FileName = Path.Combine(Application.StartupPath, Const.GAME_EXE),
                    Arguments = $"{app.appid} {Process.GetCurrentProcess().Id}"
                };

                app.process = new Process() { StartInfo = pinfo };
                app.process.Start();
            }

            switch (session)
            {
                case Session.Idle:
                    TmrIdleTime.Start();
                    PanelIdleStartedListGames.Items.AddRange(_appListActive.Select(o => o.GetIdAndName()).Cast<string>().ToArray());
                    ShowWindow(WindowPanel.IdleStarted);
                    break;

                case Session.Cards:
                    ShowWindow(WindowPanel.CardsStarted);
                    TmrCheckCardProgress.Start();
                    break;

                case Session.CardsBatch:
                    TmrCardBatchCheck.Start();
                    TmrIdleTime.Start();
                    PanelIdleStartedListGames.Items.AddRange(_appListActive.Select(o => o.GetIdAndName()).Cast<string>().ToArray());
                    ShowWindow(WindowPanel.IdleStarted);
                    break;
            }

            if (_settings.Settings.RestartGames || session != Session.CardsBatch)
            {
                _log.Write(Log.LogLevel.Info, $"Redemarrage automatique des jeux est actuellement activé toutes les {_settings.Settings.RestartGamesTime} minutes.");
                SetRandomTmrRestartAppInterval();
                TmrRestartApp.Start();
            }

            _log.Write(Log.LogLevel.Info, $"Landement de {_appListActive.Count} avec la session {session}");
            _activeSession = session;
            TmrCheckProcess.Start();
        }

        private void StopApps(bool killSession = true)
        {
            if (_activeSession == Session.None)
                return;

            TmrCheckCardProgress.Stop();
            TmrCardBatchCheck.Stop();
            TmrCheckProcess.Stop();
            TmrRestartApp.Stop();
            TmrIdleTime.Stop();

            int appErrors = 0;
            foreach (var app in _appListActive)
            {
                if (app.process.HasExited)
                {
                    _log.Write(Log.LogLevel.Warn, $"Le processsus de {app.name} n'existe pas... hmm...");
                    continue;
                }

                try
                {
                    app.process.Kill();
                    app.process.WaitForExit();
                    _log.Write(Log.LogLevel.Info, $"Application {app.name} fermer");
                }
                catch (Exception ex)
                {
                    appErrors++;
                    _log.Write(Log.LogLevel.Error, $"Erreur lors de la tentative d'arrêt de l'application '{app.name}' - {ex.Message}");
                }
            }
            
            PanelIdleStartedLblIdleTime.Text = "Vous avez boost vos heures 00:00:00:00";
            PanelIdleStartedListGames.Items.Clear();

            if (killSession)
            {
                _activeSession = Session.None;
                ShowWindow(WindowPanel.Start);
            }

            _log.Write(Log.LogLevel.Info, $"Arret de la session {_activeSession} avec {appErrors} erreurs.");
            _appListActive.Clear();
        }

        private async void StartCardsFarming()
        {
            _appListBadges = await LoadBadges();
            if (_appListBadges == null)
            {
                MsgBox.Show("Impossible de lire les badges Steam. Ré-authentifier vous en vous connectant à Steam à nouveau.",
                    "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                ShowWindow(WindowPanel.Cards);
                StopApps();
            }
            else
            {
                _log.Write(Log.LogLevel.Info, $"Chargement de {_appListBadges.Count} applications avec badges.");
                if (_appListBadges.Count == 0)
                {
                    DoneCardFarming();
                }
                else
                {
                    if (_settings.Settings.IdleCardsWithMostValue)
                    {
                        _appListBadges = _appListBadges.OrderByDescending(o => o.card.price).ToList();
                        _log.Write(Log.LogLevel.Info, $"Liste des badges triés par prix à partir de Steam.");
                    }

                    StartNextCard();
                }
            }
        }

        private void DoneCardFarming()
        {
            using (var cnd = new SoundPlayer(Properties.Resources.storms))
                cnd.Play();

            _log.Write(Log.LogLevel.Info, "L'utilisateur n'a pas de carte à farm.");
            ShowChatBubble("Aucune carte restante", "Vous n'avez plus de cartes à farmer!");
            ShowWindow(WindowPanel.Start);
            StopApps();
        }

        private async void CheckCurrentBadge()
        {
            if (_appCurrentBadge == null)
            {
                _log.Write(Log.LogLevel.Warn, $"Le compte abonné est pour une raison inconnu nul. Impossible de vérifier le badge actuel.");
                return;
            }

            if (await UpdateCurrentCard())
            {
                if (_appCurrentBadge.card.cardsremaining == 0)
                {
                    _log.Write(Log.LogLevel.Info, $"Aucune carte restante pour ce badge, arret de l'application et nous allons choisir la prochaine carte.");
                    _appListBadges.Remove(_appCurrentBadge);
                    StartNextCard();
                }
                else
                {
                    _log.Write(Log.LogLevel.Info, $"Cartes restantes pour la carte actuelle: {_appCurrentBadge.card.cardsremaining}");
                    PanelCardsStartedLblCardsLeft.Text = $"{_appCurrentBadge.card.cardsremaining} Cartes restantes pour le jeu | {_appListBadges.Count} au total";
                }
            }
            else
            {
                _log.Write(Log.LogLevel.Info, $"Impossible d'obtenir les informations de la carte.");
            }
        }

        private async void StartNextCard()
        {
            if (_appCurrentBadge != null)
            {
                _log.Write(Log.LogLevel.Info, $"Suppression du badge actuel de la liste car nous en démarrons un nouveau.");
                _appListBadges.Remove(_appCurrentBadge);
            }

            _log.Write(Log.LogLevel.Info, $"Commencer la prochaine carte.");
            StopApps(false);
            App app;
            
            if (_settings.Settings.OnlyIdleGamesWithCertainMinutes)
            {
                int minimumMinutes = _settings.Settings.NumOnlyIdleGamesWithCertainMinutes;
                app = _appListBadges.FirstOrDefault(o => o.card.minutesplayed >= minimumMinutes);

                if (app == null)
                {
                    _log.Write(Log.LogLevel.Info, $"Nous n'avons aucune application correspondant au temps de jeu requis. Démarrage.");
                    _appListActive = _appListBadges.Take(_settings.Settings.NumGamesIdleWhenNoCards).ToList();
                    StartApps(Session.CardsBatch);
                    return;
                }
            }
            else
            {
                app = _appListBadges.FirstOrDefault();
            }

            if (app != null)
            {
                _log.Write(Log.LogLevel.Info, $"Nous avons une application a boost.");
                _appCurrentBadge = app;

                var imageBytes = await _steamWeb.RequestData($"http://cdn.akamai.steamstatic.com/steam/apps/{app.appid}/header.jpg");
                if (imageBytes != null)
                {
                    PanelCardsStartedPicGame.Image = Utils.BytesToImage(imageBytes);
                    PanelCardsStartedLblOptions.Parent = PanelCardsStartedPicGame;
                    PanelCardsStartedLblOptions.Location = new Point(4, 4);
                }

                PanelCardsStartedLblCurrentGame.Text = app.name;
                PanelCardsStartedLblCardsLeft.Text = $"{_appCurrentBadge.card.cardsremaining} cartes | {_appListBadges.Count} au total";

                _log.Write(Log.LogLevel.Info, $"Démarrage de {app.name} avec {app.card.cardsremaining} cartes restant à drop");
                _appListActive.Add(app);
                StartApps(Session.Cards);
            }
            else
            {
                DoneCardFarming();
            }

            PanelCardsStartedBtnNext.Enabled = true;
        }

        private async Task<bool> UpdateCurrentCard()
        {
            try
            {
                string response = await _steamWeb.Request($"http://steamcommunity.com/profiles/{_steamUser016.GetSteamID().ConvertToUint64()}/gamecards/{_appCurrentBadge.appid}");
                if (string.IsNullOrWhiteSpace(response))
                {
                    _log.Write(Log.LogLevel.Info, $"La réponse de la carte est vide.");
                    return false;
                }

                var document = new HtmlAgilityPack.HtmlDocument();
                document.LoadHtml(response);

                var cardNode = document.DocumentNode.SelectSingleNode(".//span[@class=\"progress_info_bold\"]");
                if (cardNode != null && !string.IsNullOrWhiteSpace(cardNode.InnerText))
                {
                    string cards = Regex.Match(cardNode.InnerText, @"[0-9]+").Value;

                    int cardsremaining;
                    if (int.TryParse(cards, out cardsremaining))
                    {
                        _appCurrentBadge.card.cardsremaining = cardsremaining;
                    }
                    else
                    {
                        _appCurrentBadge.card.cardsremaining = 0;
                    }

                    _log.Write(Log.LogLevel.Info, $"Informations de carte mises à jour. {cardsremaining} carte(s) à drop.");
                    return true;
                }
                else
                {
                    _log.Write(Log.LogLevel.Info, $"CImpossible d'analyser le nombre de cartes restantes lors de la mise à jour de la carte.");
                    File.WriteAllText("Erreur\\UpdateCurrentCard.html", response);
                }
            }
            catch (Exception ex)
            {
                _log.Write(Log.LogLevel.Error, $"Erreur de mise à jour de la carte actuelle. {ex.Message}");
            }

            return false;
        }

        private async Task<List<App>> LoadBadges()
        {
            _log.Write(Log.LogLevel.Info, $"Chargement des badges");
            string profileUrl = $"http://steamcommunity.com/profiles/{_steamUser016.GetSteamID().ConvertToUint64()}";
            var document = new HtmlAgilityPack.HtmlDocument();
            var appList = new List<App>();
            int pages = 0;

            try
            {
                /*Get the first page of badges and process the information on that page
                 which we will use to see how many more pages there are to scrape*/
                string pageUrl = $"{profileUrl}/badges/?p=1";
                string response = await _steamWeb.Request(pageUrl);

                if (!string.IsNullOrWhiteSpace(response))
                {
                    document.LoadHtml(response);
                    appList.AddRange(ProcessBadgesOnPage(document));
                    _log.Write(Log.LogLevel.Info, $"{appList.Count} traitée badges de la page 1.");

                    var pageNodes = document.DocumentNode.SelectNodes("//a[@class=\"pagelink\"]");
                    if (pageNodes != null)
                        pages = pageNodes.Select(o => o.Attributes["href"].Value).Distinct().Count() + 1;

                    /*Scrape the rest of the pages and add result to our app list*/
                    for (int i = 2; i <= pages; i++)
                    {
                        pageUrl = $"{profileUrl}/badges/?p={i}";
                        response = await _steamWeb.Request(pageUrl);

                        if (string.IsNullOrWhiteSpace(response))
                            continue;

                        document.LoadHtml(response);

                        var tempList = ProcessBadgesOnPage(document);
                        _log.Write(Log.LogLevel.Info, $"Processed {tempList.Count} badges from page {i}.");
                        appList.AddRange(tempList);
                    }

                    if (appList.Count() > 0)
                    {
                        /*We'll use Enhanced Steam api to get the prices of each card here.
                         Hihihihihihihihihihihihi don't hate me cuz i am just a silly anime girl*/
                        string appids = string.Join(",", appList.Select(o => o.appid));
                        string priceUrl = $"{Const.CARD_PRICE_URL}{appids}";
                        response = await _steamWeb.Request(priceUrl);

                        if (!string.IsNullOrWhiteSpace(response))
                        {
                            /* Example response from Enhanced Steam
                            {
                                "avg_values": {
                                    "3830": 0.04,
                                    "4000": 0.08,
                                    "70000": 0.05,
                                    "92100": 0.07
                                }
                            }*/
                            
                            dynamic dyn = JObject.Parse(response);
                            foreach (var card in dyn.avg_values)
                            {
                                string s_appid = card.Name, s_price = card.Value;
                                uint appid;
                                double price;
                                if (uint.TryParse(s_appid, out appid) && double.TryParse(s_price, out price))
                                {
                                    var app = appList.FirstOrDefault(o => o.appid == appid);
                                    if (app != null)
                                    {
                                        app.card.price = price;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        _log.Write(Log.LogLevel.Warn, $"Nous n'avons pas chargé de badges. Quelque chose ne va pas.");
                        File.WriteAllText("Erreur\\LoadBadges.html", response);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write(Log.LogLevel.Error, $"Impossible d'obtenir des badges Steam. Erreur: {ex}");
                MsgBox.Show("Erreur lors du chargement des badges Steam. Steam pourrait être en panne.", "Erreur", MsgBox.Buttons.Fuck, MsgBox.MsgIcon.Error);
            }
            
            return appList.Where(o => o.card.cardsremaining > 0 && !_settings.Settings.BlacklistedCardGames.Contains(o.appid)).ToList();
        }

        private List<App> ProcessBadgesOnPage(HtmlAgilityPack.HtmlDocument document)
        {
            var list = new List<App>();

            foreach (var badge in document.DocumentNode.SelectNodes("//div[@class=\"badge_row is_link\"]"))
            {
                var appIdNode = badge.SelectSingleNode(".//a[@class=\"badge_row_overlay\"]").Attributes["href"].Value;
                string appid = Regex.Match(appIdNode, @"gamecards/(\d+)/").Groups[1].Value;

                if (string.IsNullOrWhiteSpace(appid))
                    continue;

                var hoursNode = badge.SelectSingleNode(".//div[@class=\"badge_title_stats_playtime\"]");
                string hours = hoursNode == null ? string.Empty : Regex.Match(hoursNode.InnerText, @"[0-9\.,]+").Value;

                var cardNode = badge.SelectSingleNode(".//span[@class=\"progress_info_bold\"]");
                if (cardNode != null && !string.IsNullOrWhiteSpace(cardNode.InnerText))
                {
                    string cards = Regex.Match(cardNode.InnerText, @"[0-9]+").Value;
                    cards = string.IsNullOrWhiteSpace(cards) ? "0" : cards;

                    uint id;
                    if (uint.TryParse(appid, out id))
                    {
                        var game = _appList.FirstOrDefault(o => o.appid == id);
                        if (game != null)
                        {
                            var tc = new TradeCard();

                            double hoursplayed;
                            if (double.TryParse(hours, out hoursplayed))
                            {
                                var span = TimeSpan.FromHours(hoursplayed);
                                tc.minutesplayed = span.TotalMinutes;
                            }

                            int cardsremaining;
                            if (int.TryParse(cards, out cardsremaining))
                                tc.cardsremaining = cardsremaining;

                            game.card = tc;
                            list.Add(game);
                        }
                    }
                }
            }

            return list;
        }

        private async Task<Bitmap> GetAppBackground(uint appid)
        {
            var storeJson = await DownloadString($"{Const.STORE_JSON_URL}{appid}");
            string bgUrl = Store.GetAppScreenshotUrl(storeJson);
            if (!string.IsNullOrWhiteSpace(bgUrl))
            {
                var imageBytes = await DownloadData(bgUrl);
                if (imageBytes != null)
                {
                    Image img = Utils.BytesToImage(imageBytes);
                    return Utils.ChangeImageOpacity(Utils.FixedImageSize(img, Width, Height), 0.05f);
                }
            }

            return null;
        }

        private async Task<string> DownloadString(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                wc.Headers.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/42.0.2311.135 Safari/537.36");
                return await wc.DownloadStringTaskAsync(url);
            }
        }

        private async Task<byte[]> DownloadData(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                wc.Headers.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/42.0.2311.135 Safari/537.36");
                return await wc.DownloadDataTaskAsync(url);
            }
        }

        private async void InitializeApp()
        {
            if (ConnectToSteam())
            {
                int appCount = await Task.Run(() => GetAppList());
                if (appCount > 0)
                {
                    ShowLoadingText("Définition des informations utilisateur");
                    foreach (uint appid in _settings.Settings.GameHistoryIds)
                    {
                        var app = _appList.FirstOrDefault(o => o.appid == appid);
                        if (app != null)
                        {
                            _appListSelected.Add(app);
                            _appList.Remove(app);
                        }
                    }

                    SetUserInfo();
                    RefreshGameList();
                    ShowWindow(WindowPanel.Start);
                    BgwSteamCallback.RunWorkerAsync();
                    
                    string bubbleJson = await DownloadString(Const.CHAT_BUBBLE_URL);
                    if (!string.IsNullOrWhiteSpace(bubbleJson))
                    {
                        try
                        {
                            var entries = JsonConvert.DeserializeObject<ChatBubbles>(bubbleJson);
                            foreach (var bubble in entries.bubbles.Take(4))
                                ShowChatBubble(bubble.title, bubble.text, bubble.url);
                        }
                        catch (Exception ex)
                        {
                            _log.Write(Log.LogLevel.Error, $"Erreur lors du chargement des bulles. {ex.Message}");
                        }
                    }

                    string updateInfo = await UpdateCheck.IsUpdateAvailable();
                    if (updateInfo.Length > 0)
                    {
                        ShowChatBubble("Mise à jour disponible", $"Cliquez ici pour télécharger la nouvelle mise à jour. ({updateInfo})", Const.REPO_RELEASE_URL);
                        PanelStartLblVersion.Text = "Mise à jour disponible";
                    }

                    if (Utils.IsApplicationInstalled("Discord") && !_settings.Settings.ShowedDiscordInfo)
                    {
                        ShowChatBubble("Serveur Discord", "J'ai remarqué que vous avez installé Discord. Cliquez ici pour rejoindre notre serveur de support!", "https://discord.gg/YaB3tQ8");
                        _settings.Settings.ShowedDiscordInfo = true;
                    }

                    PanelStartChatPanel.Visible = true;
                }
            }
            else
            {
                MsgBox.Show("Impossible de se connecter à Steam. Assurez-vous que Steam est en cours d'exécution.", "Erreur", 
                    MsgBox.Buttons.Gotit, MsgBox.MsgIcon.Exclamation);
                ExitApplication();
            }
        }

        private bool IsDuplicateAlreadyRunning()
        {
            var duplicates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location))
                .OrderBy(o => o.StartTime).ToList();

            if (duplicates.Count > 1)
            {
                MsgBox.Show("SingleBoostr est déjà en cours d'exécution.", "Duplication", MsgBox.Buttons.Gotit, MsgBox.MsgIcon.Exclamation);
                return true;
            }

            return false;
        }

        private void ShowLoadingText(string text)
        {
            PanelLoadingText.Invoke((MethodInvoker)delegate {
                PanelLoadingText.Visible = true;
                PanelLoadingText.Text = text.ToUpper();
            });
        }

        private async Task<int> GetAppList()
        {
            if (!File.Exists(Const.APP_LIST))
            {
                ShowLoadingText("Téléchargement de la liste des applications Steam");
                if (!await _settings.DownloadAppList())
                    return 0;
            }

            var lastChanged = File.GetLastWriteTime(Const.APP_LIST);
            int daysSinceChanged = (int)(DateTime.Now - lastChanged).TotalDays;
            if (daysSinceChanged > 10)
            {
                ShowLoadingText("Mise à jour de la liste des applications Steam");
                _log.Write(Log.LogLevel.Info, "Plus de 10 jours depuis la dernière mise à jour de la liste des applications. Téléchargement de la nouvelle liste.");
                if (!await _settings.DownloadAppList())
                    return 0;
            }

            string json = File.ReadAllText(Const.APP_LIST);
            var apps = JsonConvert.DeserializeObject<SteamApps>(json);
            ShowLoadingText("Configuration des applications abonnées");
            foreach (var app in apps.applist.apps)
            {
                if (_steamApps003.BIsSubscribedApp(app.appid))
                {
                    app.name = app.name;
                    _appList.Add(app);
                }
            }

            return _appList.Count;
        }

        private void RefreshGameList()
        {
            _appList.Sort((app1, app2) => app1.CompareTo(app2));
            PanelIdleListGames.Items.Clear();
            PanelIdleListGamesSelected.Items.Clear();

            List<App> appList = new List<App>();
            string searchQuery = PanelIdleTxtSearch.Text.Trim().ToLower();
            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                appList = _appList.ToList();
            }
            else
            {
                appList = _appList.ToList().Where(o => o.GetIdAndName().ToLower().Contains(searchQuery)).ToList();
            }
            
            PanelIdleListGames.Items.AddRange(appList.Select(o => o.GetIdAndName()).Cast<string>().ToArray());
            PanelIdleListGamesSelected.Items.AddRange(_appListSelected.ToList().Select(o => o.GetIdAndName()).Cast<string>().ToArray());

            PanelIdleLblClear.Visible = _appListSelected.Count > 0;
            PanelIdleLblSelectedGamesCount.Visible = _appListSelected.Count > 0;
            PanelIdleLblSelectedGamesCount.Text = $"Jeux sélectionnés: {_appListSelected.Count}";

            PanelIdleLblMatchingSearch.Visible = !string.IsNullOrWhiteSpace(searchQuery);
            PanelIdleLblMatchingSearch.Text = $"Applications correspondant à la recherche: {appList.Count}";

            if (_appListSelected.Count >= Const.MAX_GAMES && !_appCountWarningDisplayed)
            {
                _appCountWarningDisplayed = true;
                MsgBox.Show($"Steam n'autoriqe que {Const.MAX_GAMES} jeux à jouer à la fois. Vous pouvez continuer à ajouter plus de jeux, "
                    + "mais les heures ne seront pas boosté", "Limite maximum atteinte", MsgBox.Buttons.OK, MsgBox.MsgIcon.Info);
            }
        }

        private bool ConnectToSteam()
        {
            TSteamError steamError = new TSteamError();
            if (!Steamworks.Load(true))
            {
                MsgBox.Show("Steamworks n'a pas pu charger.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"Steamworks n'a pas pu être chargé.");
                return false;
            }

            if (Application.StartupPath == Steamworks.GetInstallPath())
            {
                MsgBox.Show("Vous n'êtes pas autorisé à exécuter cette application à partir du répertoire Steam.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"Vous n'êtes pas autorisé à exécuter cette application à partir du répertoire Steam.");
                return false;
            }

            _steam006 = Steamworks.CreateSteamInterface<ISteam006>();
            if (_steam006.Startup(0, ref steamError) == 0)
            {
                MsgBox.Show("ISteam006 n'a pas pu démarrer. il à retourné 0.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"ISteam006 n'a pas pu démarrer. il à retourné 0.");
                return false;
            }

            _steamClient012 = Steamworks.CreateInterface<ISteamClient012>();
            _clientEngine = Steamworks.CreateInterface<IClientEngine>();

            if (_steamClient012 == null)
            {
                MsgBox.Show("ISteamClient012 est null. Impossible de créer l'interface.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"ISteamClient012 est null. Impossible de créer l'interface.");
                return false;
            }

            if (_clientEngine == null)
            {
                MsgBox.Show("IClientEngine est null. Impossible de créer l'interface.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"IClientEngine est null. Impossible de créer l'interface.");
                return false;
            }

            _pipe = _steamClient012.CreateSteamPipe();
            if (_pipe == 0)
            {
                MsgBox.Show("ISteamClient012 n'a pas réussi à créer une connexion pipe à Steam.", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"ISteamClient012 n'a pas réussi à créer une connexion pipe à Steam.");
                return false;
            }

            _user = _steamClient012.ConnectToGlobalUser(_pipe);
            if (_user == 0 || _user == -1)
            {
                MsgBox.Show($"ISteamClient012 n'a pas réussi à se connecter à l'utilisateur global. Valeur: {_user}", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                _log.Write(Log.LogLevel.Error, $"ISteamClient012 n'a pas réussi à se connecter à l'utilisateur global. Valeur: {_user}");
                return false;
            }

            _steamUser016 = _steamClient012.GetISteamUser<ISteamUser016>(_user, _pipe);
            _clientUser = _clientEngine.GetIClientUser<IClientUser>(_user, _pipe);
            _clientFriends = _clientEngine.GetIClientFriends<IClientFriends>(_user, _pipe);
            _steamApps001 = _steamClient012.GetISteamApps<ISteamApps001>(_user, _pipe);
            _steamApps003 = _steamClient012.GetISteamApps<ISteamApps003>(_user, _pipe);
            _steamFriends002 = _steamClient012.GetISteamFriends<ISteamFriends002>(_user, _pipe);
            
            return _steamUser016 != null 
                && _clientUser != null
                && _clientFriends != null
                && _steamApps001 != null
                && _steamApps003 != null
                && _steamFriends002 != null;
        }

        private void SetUserInfo()
        {
            string games = $"{_appList.Count} jeux";
            string displayName = _clientFriends.GetPersonaName();
            displayName = string.IsNullOrWhiteSpace(displayName) ? "Inconnu" : Utils.GetUnicodeString(displayName);
            
            Invoke(new Action(() =>
            {
                PanelUserLblName.Text = displayName;
                PanelUserLblGames.Text = games;
            }));
        }

        private void ExitApplication()
        {
            if (_activeSession != Session.None)
            {
                var diag = MsgBox.Show($"Vous êtes actuellement entrain de boost vos heures {_activeSession}. Voulez-vous arrêter et quitter?", "Session active",
                    MsgBox.Buttons.YesNo, MsgBox.MsgIcon.Info);

                if (diag == DialogResult.No)
                    return;
            }

            StopApps();
            Application.Exit();
        }

        private void CanGoBack(bool enable)
        {
            if (!enable && _activeSession != Session.None && _activeWindow == WindowPanel.Start)
            {
                _canGoBack = true;
                PanelUserPicGoBack.Size = new Size(48, 48);
                PanelUserPicGoBack.Cursor = Cursors.Hand;
                PanelUserPicGoBack.BackgroundImage = Properties.Resources.Active;
            }
            else
            {
                _canGoBack = enable;
                PanelUserPicGoBack.Size = _canGoBack ? new Size(20, 48) : new Size(3, 48);
                PanelUserPicGoBack.Cursor = _canGoBack ? Cursors.Hand : Cursors.Default;
                PanelUserPicGoBack.BackgroundImage = Properties.Resources.Back;
            }
        }

        private void ShowWindow(WindowPanel panel)
        {
            _activeWindow = panel;
            PanelStart.Visible = false;
            PanelLoading.Visible = false;
            PanelTos.Visible = false;
            PanelIdle.Visible = false;
            PanelIdleStarted.Visible = false;
            PanelCards.Visible = false;
            PanelCardsStarted.Visible = false;
            PanelUser.Visible = false;
            CanGoBack(false);

            switch (panel)
            {
                case WindowPanel.Start:
                    PanelStart.Visible = true;
                    PanelUser.Visible = true;
                    break;

                case WindowPanel.Loading:
                    PanelLoading.Visible = true;
                    break;

                case WindowPanel.Tos:
                    PanelTos.Visible = true;
                    break;

                case WindowPanel.Idle:
                    PanelIdle.Visible = true;
                    PanelUser.Visible = true;
                    CanGoBack(true);
                    break;

                case WindowPanel.IdleStarted:
                    PanelIdleStarted.Visible = true;
                    PanelUser.Visible = true;
                    CanGoBack(true);
                    break;

                case WindowPanel.Cards:
                    PanelCards.Visible = true;
                    PanelUser.Visible = true;
                    CanGoBack(true);
                    break;

                case WindowPanel.CardsStarted:
                    PanelCardsStarted.Visible = true;
                    PanelUser.Visible = true;
                    CanGoBack(true);
                    break;
            }
        }

        private void SetRandomTmrRestartAppInterval()
        {
            int baseRestartTime = _settings.Settings.RestartGamesTime;
            baseRestartTime += Utils.GetRandom().Next(0, 10);
            TmrRestartApp.Interval = (int)TimeSpan.FromMinutes(baseRestartTime).TotalMilliseconds;
        }

        private string GetGameNameById(uint appId)
        {
            App app = null;
            return (app = _appList
                .FirstOrDefault(o => o.appid == appId)) == null ? appId.ToString() : app.name;
        }

        private void OnLobbyInvite(CSteamID senderId, GameID_t game)
        {
            if (!_settings.Settings.EnableChatResponse)
                return;

            if (senderId.ConvertToUint64() == _steamUser016.GetSteamID().ConvertToUint64())
                return;

            string senderName = _steamFriends002.GetFriendPersonaName(senderId);

            _logChat.Write(Log.LogLevel.Info, $"{senderName} à envoyer une invitation au lobby pour le jeu {GetGameNameById(game.m_nAppID)}");

            if (_settings.Settings.ChatResponses.Count == 0)
                return;

            if (_activeSession == Session.None && _settings.Settings.OnlyReplyIfIdling)
                return;

            if (_settings.Settings.WaitBetweenReplies)
            {
                DateTime value;
                if (_chatResponses.TryGetValue(senderId.ConvertToUint64(), out value))
                {
                    TimeSpan diff = DateTime.Now.Subtract(value);
                    if (diff.Minutes < _settings.Settings.WaitBetweenRepliesTime)
                        return;
                }
            }

            string response = _settings.Settings.ChatResponses[Utils.GetRandom().Next(0, _settings.Settings.ChatResponses.Count)];
            if (SendChatMessage(senderId, response))
            {
                _chatResponses[senderId.ConvertToUint64()] = DateTime.Now;
                _logChat.Write(Log.LogLevel.Info, $"Réponse automatique à {senderName} avec comme message '{response}'");
            }
            else
            {
                _logChat.Write(Log.LogLevel.Info, $"Impossible de répondre à {senderName} avec le message '{response}'");
            }
        }

        private void OnFriendChatMsg(string message, string senderName, CSteamID senderId, CSteamID friendId)
        {
            if (!_settings.Settings.EnableChatResponse)
                return;

            if (senderId.ConvertToUint64() == _steamUser016.GetSteamID().ConvertToUint64())
                return;

            _logChat.Write(Log.LogLevel.Info, $"{senderName}: {message}");

            if (_settings.Settings.ChatResponses.Count == 0)
                return;

            if (_activeSession == Session.None && _settings.Settings.OnlyReplyIfIdling)
                return;

            if (_settings.Settings.WaitBetweenReplies)
            {
                DateTime value;
                if (_chatResponses.TryGetValue(friendId.ConvertToUint64(), out value))
                {
                    TimeSpan diff = DateTime.Now.Subtract(value);
                    if (diff.Minutes < _settings.Settings.WaitBetweenRepliesTime)
                        return;
                }
            }

            string response = _settings.Settings.ChatResponses[Utils.GetRandom().Next(0, _settings.Settings.ChatResponses.Count)];
            if (SendChatMessage(friendId, response))
            {
                _chatResponses[friendId.ConvertToUint64()] = DateTime.Now;
                _logChat.Write(Log.LogLevel.Info, $"Réponse automatique à {senderName} avec comme message '{response}'");
            }
            else
            {
                _logChat.Write(Log.LogLevel.Info, $"Impossible de répondre à {senderName} avec comme message '{response}'");
            }
        }

        private void OnNewItem(UpdateItemAnnouncement_t e)
        {
            if (e.m_cNewItems > 0 && _activeSession == Session.Cards)
            {
                _log.Write(Log.LogLevel.Info, $"Nous avons reçu l'item drop. Vérification de la progression actuelle du badge");
                TmrCheckCardProgress.Start();
                Invoke(new Action(() =>
                {
                    CheckCurrentBadge();
                }));
            }
        }

        private bool SendChatMessage(CSteamID receiver, string message)
        {
            return _steamFriends002.SendMsgToFriend(receiver, EChatEntryType.k_EChatEntryTypeChatMsg, Encoding.UTF8.GetBytes(message));
        }

        #endregion Functions
    }

    class MyRenderer : ToolStripProfessionalRenderer
    {
        public MyRenderer() : base(new MyColors()) { }
    }

    class MyColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected
        {
            get { return Color.FromArgb(37, 37, 37); }
        }

        public override Color MenuItemBorder
        {
            get { return Color.FromArgb(37, 37, 37); }
        }
    }
}