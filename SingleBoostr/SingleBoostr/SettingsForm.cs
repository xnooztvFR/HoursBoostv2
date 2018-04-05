﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Net;
using System.Diagnostics;
using Newtonsoft.Json;

namespace SingleBoostr
{
    public partial class SettingsForm : Form
    {
        public Settings Settings { get; private set; }

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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Rectangle rect = new Rectangle(new Point(0, 0), new Size(this.Width - 1, this.Height - 1));
            Pen pen = new Pen(Const.LABEL_HOVER);
            e.Graphics.DrawRectangle(pen, rect);
        }

        public SettingsForm()
        {
            InitializeComponent();

            if (!LoadSettings())
                SaveSettings();
        }

        public bool SaveSettings()
        {
            string jsonStr = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            if (!string.IsNullOrWhiteSpace(jsonStr))
            {
                File.WriteAllText(Const.SETTINGS_FILE, jsonStr);
                return true;
            }

            return false;
        }

        public bool LoadSettings()
        {
            if (File.Exists(Const.SETTINGS_FILE))
            {
                string jsonStr = File.ReadAllText(Const.SETTINGS_FILE);
                if (!string.IsNullOrWhiteSpace(jsonStr))
                {
                    try
                    {
                        Settings = JsonConvert.DeserializeObject<Settings>(jsonStr);
                        Settings.Verify();
                        return Settings != null;
                    }
                    catch (Exception ex)
                    {
                        MsgBox.Show($"Erreur d'analyse {Const.SETTINGS_FILE}. Le fichier est probablement corrompu. Les paramètres seront réinitialisés. Ne jouez pas directement avec le fichier à moins de savoir ce que vous faites.\nErreur: {ex.Message}", "Erreur",
                            MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
                    }
                }
            }

            Settings = new Settings();
            Settings.ChatResponses.Add("Je boost mes heures sur Steam..");
            Settings.ChatResponses.Add("Logiciel créer par xnooztv!");
            return false;
        }

        public void AddBrowserSessionInfo(SessionInfo info)
        {
            Settings.WebSession = info;
        }

        public async Task<bool> DownloadAppList()
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)");
                    client.Encoding = Encoding.UTF8;

                    string str = await client.DownloadStringTaskAsync(new Uri(Const.APP_LIST_URL));

                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        File.WriteAllText(Const.APP_LIST, str);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                MsgBox.Show($"Erreur lors du téléchargement de la liste d'applications. Steam peut être en panne.\n{ex.Message}", "Erreur", MsgBox.Buttons.OK, MsgBox.MsgIcon.Error);
            }

            return false;
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            CbEnableChatResponse.Checked = Settings.EnableChatResponse;
            TxtChatResponses.Lines = Settings.ChatResponses.ToArray();
            CbOnlyReplyIfIdling.Checked = Settings.OnlyReplyIfIdling;
            CbWaitBetweenReplies.Checked = Settings.WaitBetweenReplies;
            NumWaitBetweenReplies.Value = Settings.WaitBetweenRepliesTime;
            CbRestartGames.Checked = Settings.RestartGames;
            NumRestartGamesMinutes.Value = Settings.RestartGamesTime;
            CbRestartGamesRandomly.Checked = Settings.RestartGamesAtRandom;
            CbCheckForUpdates.Checked = Settings.CheckForUpdates;
            CbClearRecentlyPlayed.Checked = Settings.ClearRecentlyPlayedOnExit;
            CbForceOnlineStatus.Checked = Settings.ForceOnlineStatus;
            CbSaveAppIdleHistory.Checked = Settings.SaveAppIdleHistory;
            CbJoinSteamGroup.Checked = Settings.JoinSteamGroup;
            CbSaveLoginCookies.Checked = Settings.SaveLoginCookies;
            CbHideToTraybar.Checked = Settings.HideToTraybar;
            CbOnlyIdleGamesWithCertainMinutes.Checked = Settings.OnlyIdleGamesWithCertainMinutes;
            NumOnlyIdleGamesWithCertainMinutes.Value = Settings.NumOnlyIdleGamesWithCertainMinutes;
            NumGamesIdleWhenNoCards.Value = Settings.NumGamesIdleWhenNoCards;
            CbIdleCardsWithMostValue.Checked = Settings.IdleCardsWithMostValue;
            LblClearBlackList.Text = $"Clear {Settings.BlacklistedCardGames.Count} blacklisted card(s)";
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            Settings.EnableChatResponse = CbEnableChatResponse.Checked;
            Settings.ChatResponses = TxtChatResponses.Lines.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
            Settings.OnlyReplyIfIdling = CbOnlyReplyIfIdling.Checked;
            Settings.WaitBetweenReplies = CbWaitBetweenReplies.Checked;
            Settings.RestartGames = CbRestartGames.Checked;
            Settings.RestartGamesTime = (int)NumRestartGamesMinutes.Value;
            Settings.RestartGamesAtRandom = CbRestartGamesRandomly.Checked;
            Settings.CheckForUpdates = CbCheckForUpdates.Checked;
            Settings.ClearRecentlyPlayedOnExit = CbClearRecentlyPlayed.Checked;
            Settings.ForceOnlineStatus = CbForceOnlineStatus.Checked;
            Settings.SaveAppIdleHistory = CbSaveAppIdleHistory.Checked;
            Settings.JoinSteamGroup = CbJoinSteamGroup.Checked;
            Settings.SaveLoginCookies = CbSaveLoginCookies.Checked;
            Settings.HideToTraybar = CbHideToTraybar.Checked;
            Settings.OnlyIdleGamesWithCertainMinutes = CbOnlyIdleGamesWithCertainMinutes.Checked;
            Settings.NumOnlyIdleGamesWithCertainMinutes = (int)NumOnlyIdleGamesWithCertainMinutes.Value;
            Settings.NumGamesIdleWhenNoCards = (int)NumGamesIdleWhenNoCards.Value;
            Settings.IdleCardsWithMostValue = CbIdleCardsWithMostValue.Checked;
            Settings.WaitBetweenRepliesTime = (int)NumWaitBetweenReplies.Value;

            DialogResult = DialogResult.OK;
            ActiveControl = label1;
            SaveSettings();
            Close();
        }

        private void PanelContainer_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, Const.WM_NCLBUTTONDOWN, Const.HT_CAPTION, 0);
            }
        }

        private void BtnSave_MouseEnter(object sender, EventArgs e)
        {
            BtnSave.BackgroundImage = Properties.Resources.Back_Selected;
        }

        private void BtnSave_MouseLeave(object sender, EventArgs e)
        {
            BtnSave.BackgroundImage = Properties.Resources.Back;
        }

        private void LblDownloadNewAppList_MouseEnter(object sender, EventArgs e)
        {
            LblDownloadNewAppList.ForeColor = Const.LABEL_HOVER;
        }

        private void LblDownloadNewAppList_MouseLeave(object sender, EventArgs e)
        {
            LblDownloadNewAppList.ForeColor = Const.LABEL_NORMAL;
        }

        private async void LblDownloadNewAppList_Click(object sender, EventArgs e)
        {
            LblDownloadNewAppList.Enabled = false;
            if (await DownloadAppList())
            {
                MsgBox.Show("Nouvelle liste d'applications téléchargées! Redémarrez l'application pour charger les nouveaux jeux.", "Succès", MsgBox.Buttons.OK, MsgBox.MsgIcon.Info);
                LblDownloadNewAppList.Enabled = true;
            }
        }

        private void LblClearBlackList_Click(object sender, EventArgs e)
        {
            Settings.BlacklistedCardGames.Clear();
            LblClearBlackList.Text = $"Effacer {Settings.BlacklistedCardGames.Count} carte(s) sur liste noire";
        }

        private void LblClearBlackList_MouseEnter(object sender, EventArgs e)
        {
            LblClearBlackList.ForeColor = Const.LABEL_HOVER;
        }

        private void LblClearBlackList_MouseLeave(object sender, EventArgs e)
        {
            LblClearBlackList.ForeColor = Const.LABEL_NORMAL;
        }

        private void CbEnableChatResponse_CheckedChanged(object sender, EventArgs e)
        {
            if (!Settings.ChatResponseTipDisplayed)
            {
                Settings.ChatResponseTipDisplayed = true;
                MsgBox.Show("Entrez plusieurs lignes dans la boîte de réponses de chat et le programme choisira une ligne aléatoire chaque fois que quelqu'un vous envoie un message.", 
                    "Tip!", MsgBox.Buttons.Gotit, MsgBox.MsgIcon.Info);
            }
        }

        private void TxtChatResponses_TextChanged(object sender, EventArgs e)
        {

        }
    }

    public class Settings
    {
        public bool EnableChatResponse { get; set; }

        public List<string> ChatResponses { get; set; } = new List<string>();

        public bool OnlyReplyIfIdling { get; set; } = true;

        public bool WaitBetweenReplies { get; set; } = true;

        public bool RestartGames { get; set; } = true;

        public bool RestartGamesAtRandom { get; set; } = true;

        public bool ForceOnlineStatus { get; set; } = true;

        public bool CheckForUpdates { get; set; } = true;

        public bool ClearRecentlyPlayedOnExit { get; set; }

        public bool SaveAppIdleHistory { get; set; } = true;

        public bool JoinSteamGroup { get; set; } = true;

        public bool SaveLoginCookies { get; set; } = true;

        public bool HideToTraybar { get; set; }

        public bool OnlyIdleGamesWithCertainMinutes { get; set; } = true;

        public int NumOnlyIdleGamesWithCertainMinutes { get; set; } = 120;

        public int NumGamesIdleWhenNoCards { get; set; } = 25;

        public int RestartGamesTime { get; set; } = 60;

        public int WaitBetweenRepliesTime { get; set; } = 10;

        public bool IdleCardsWithMostValue { get; set; } = true;

        /*UI hidden settings*/

        public bool VACWarningDisplayed { get; set; }

        public bool ChatResponseTipDisplayed { get; set; }

        public bool ShowedDiscordInfo { get; set; }

        public SessionInfo WebSession { get; set; } = new SessionInfo();

        public List<uint> GameHistoryIds { get; set; } = new List<uint>();

        public List<uint> BlacklistedCardGames { get; set; } = new List<uint>();

        public void Verify()
        {
            if (WaitBetweenRepliesTime > 1000 || WaitBetweenRepliesTime < 1)
                WaitBetweenRepliesTime = 10;

            if (RestartGamesTime > 1000 || RestartGamesTime < 1)
                RestartGamesTime = 10;

            if (NumGamesIdleWhenNoCards > 33 || NumGamesIdleWhenNoCards < 1)
                NumGamesIdleWhenNoCards = 10;

            if (NumOnlyIdleGamesWithCertainMinutes > 1000 || NumOnlyIdleGamesWithCertainMinutes < 1)
                NumOnlyIdleGamesWithCertainMinutes = 10;
        }
    }
}
