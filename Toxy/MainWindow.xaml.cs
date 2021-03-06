﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Threading;
using System.Windows.Media.Imaging;

using MahApps.Metro;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;

using Microsoft.Win32;

using SharpTox.Core;
using SharpTox.Av;

using Toxy.Common;
using Toxy.ToxHelpers;
using Toxy.ViewModels;

using Path = System.IO.Path;
using Brushes = System.Windows.Media.Brushes;

using NAudio.Wave;
using System.Drawing.Imaging;

namespace Toxy
{
    public partial class MainWindow : MetroWindow
    {
        private Tox tox;
        private ToxAv toxav;
        private ToxCall call;

        private Dictionary<int, FlowDocument> convdic = new Dictionary<int, FlowDocument>();
        private Dictionary<int, FlowDocument> groupdic = new Dictionary<int, FlowDocument>();
        private List<FileTransfer> transfers = new List<FileTransfer>();

        private bool resizing;
        private bool focusTextbox;
        private bool typing;

        private Accent oldAccent;
        private AppTheme oldAppTheme;

        private Config config;

        System.Windows.Forms.NotifyIcon nIcon = new System.Windows.Forms.NotifyIcon();

        private Icon notifyIcon;
        private Icon newMessageNotifyIcon;

        private string toxDataDir
        {
            get
            {
                if (!config.Portable)
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tox");
                else
                    return "";
            }
        }

        private string toxDataFilename
        {
            get
            {
                return Path.Combine(toxDataDir, "tox_save");
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            DataContext = new MainWindowViewModel();

            if (File.Exists("config.xml"))
            {
                config = ConfigTools.Load("config.xml");
            }
            else
            {
                config = new Config();
                ConfigTools.Save(config, "config.xml");
            }

            ToxOptions options;
            if (config.ProxyEnabled)
                options = new ToxOptions(config.Ipv6Enabled, config.ProxyAddress, config.ProxyPort);
            else
                options = new ToxOptions(config.Ipv6Enabled, config.UdpDisabled);

            applyConfig();

            tox = new Tox(options);
            tox.Invoker = Dispatcher.BeginInvoke;
            tox.OnNameChange += tox_OnNameChange;
            tox.OnFriendMessage += tox_OnFriendMessage;
            tox.OnFriendAction += tox_OnFriendAction;
            tox.OnFriendRequest += tox_OnFriendRequest;
            tox.OnUserStatus += tox_OnUserStatus;
            tox.OnStatusMessage += tox_OnStatusMessage;
            tox.OnTypingChange += tox_OnTypingChange;
            tox.OnConnectionStatusChanged += tox_OnConnectionStatusChanged;
            tox.OnFileSendRequest += tox_OnFileSendRequest;
            tox.OnFileData += tox_OnFileData;
            tox.OnFileControl += tox_OnFileControl;
            tox.OnReadReceipt += tox_OnReadReceipt;
            tox.OnConnected += tox_OnConnected;
            tox.OnDisconnected += tox_OnDisconnected;
            tox.OnAvatarData += tox_OnAvatarData;
            tox.OnAvatarInfo += tox_OnAvatarInfo;

            tox.OnGroupInvite += tox_OnGroupInvite;
            tox.OnGroupMessage += tox_OnGroupMessage;
            tox.OnGroupAction += tox_OnGroupAction;
            tox.OnGroupNamelistChange += tox_OnGroupNamelistChange;

            toxav = new ToxAv(tox.GetHandle(), ToxAv.DefaultCodecSettings, 1);
            toxav.Invoker = Dispatcher.BeginInvoke;
            toxav.OnInvite += toxav_OnInvite;
            toxav.OnStart += toxav_OnStart;
            toxav.OnStarting += toxav_OnStart;
            toxav.OnEnd += toxav_OnEnd;
            toxav.OnEnding += toxav_OnEnd;
            toxav.OnPeerTimeout += toxav_OnEnd;
            toxav.OnRequestTimeout += toxav_OnEnd;
            toxav.OnReject += toxav_OnEnd;
            toxav.OnCancel += toxav_OnEnd;
            toxav.OnReceivedAudio += toxav_OnReceivedAudio;
            toxav.OnMediaChange += toxav_OnMediaChange;

            bool bootstrap_success = false;
            foreach (ToxConfigNode node in config.Nodes)
            {
                if (tox.BootstrapFromNode(new ToxNode(node.Address, node.Port, new ToxKey(ToxKeyType.Public, node.ClientId))))
                    bootstrap_success = true;
            }

            if (!bootstrap_success)
                Console.WriteLine("Could not bootstrap from any node!");

            loadTox();
            tox.Start();

            if (string.IsNullOrEmpty(tox.GetSelfName()))
                tox.SetName("Toxy User");

            ViewModel.MainToxyUser.Name = tox.GetSelfName();
            ViewModel.MainToxyUser.StatusMessage = tox.GetSelfStatusMessage();

            InitializeNotifyIcon();

            SetStatus(null, false);
            InitFriends();

            TextToSend.AddHandler(DragOverEvent, new DragEventHandler(Chat_DragOver), true);
            TextToSend.AddHandler(DropEvent, new DragEventHandler(Chat_Drop), true);

            ChatBox.AddHandler(DragOverEvent, new DragEventHandler(Chat_DragOver), true);
            ChatBox.AddHandler(DropEvent, new DragEventHandler(Chat_Drop), true);

            if (tox.GetFriendlistCount() > 0)
                ViewModel.SelectedChatObject = ViewModel.ChatCollection.OfType<IFriendObject>().FirstOrDefault();

            loadAvatars();
        }

        private void loadAvatars()
        {
            string avatarLoc = Path.Combine(toxDataDir, "avatar.png");
            if (File.Exists(avatarLoc))
            {
                byte[] bytes = File.ReadAllBytes(avatarLoc);
                if (bytes.Length > 0)
                {
                    tox.SetAvatar(ToxAvatarFormat.Png, bytes);

                    MemoryStream stream = null;
                    try
                    {
                        stream = new MemoryStream(bytes);

                        using (Bitmap bmp = new Bitmap(stream))
                        {
                            ViewModel.MainToxyUser.Avatar = BitmapToImageSource(bmp, ImageFormat.Png);
                        }
                    }
                    finally
                    {
                        if (stream != null)
                            stream.Dispose();
                    }
                }
            }

            string avatarsDir = Path.Combine(toxDataDir, "avatars");
            if (!Directory.Exists(avatarsDir))
                return;

            foreach (int friend in tox.GetFriendlist())
            {
                var obj = ViewModel.GetFriendObjectByNumber(friend);
                if (obj == null)
                    continue;

                ToxKey publicKey = tox.GetClientId(friend);
                string avatarFilename = Path.Combine(avatarsDir, publicKey.GetString() + ".png");
                if (File.Exists(avatarFilename))
                {
                    byte[] bytes = File.ReadAllBytes(avatarFilename);
                    if (bytes.Length > 0)
                    {
                        obj.AvatarBytes = bytes;

                        MemoryStream stream = null;
                        try
                        {
                            stream = new MemoryStream(bytes);

                            using (Bitmap bmp = new Bitmap(stream))
                            {
                                obj.Avatar = BitmapToImageSource(bmp, ImageFormat.Png);
                            }
                        }
                        finally
                        {
                            if (stream != null)
                                stream.Dispose();
                        }
                    }
                }
            }
        }

        private bool avatarExistsOnDisk(int friendnumber)
        {
            return File.Exists(Path.Combine(toxDataDir, "avatars\\" + tox.GetClientId(friendnumber).GetString() + ".png"));
        }

        private void tox_OnAvatarInfo(int friendnumber, ToxAvatarFormat format, byte[] hash)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend == null)
                return;

            if (format == ToxAvatarFormat.None)
            {
                //friend removed avatar
                if (avatarExistsOnDisk(friendnumber))
                    File.Delete(Path.Combine(toxDataDir, "avatars\\" + tox.GetClientId(friendnumber).GetString() + ".png"));

                friend.AvatarBytes = null;
                friend.Avatar = new BitmapImage(new Uri("pack://application:,,,/Resources/Icons/profilepicture.png"));
            }
            else
            {
                if (friend.AvatarBytes == null || friend.AvatarBytes.Length == 0)
                {
                    //looks like we're still busy loading the avatar or we don't have it at all
                    //let's see if we can find the avatar on the disk just to be sure

                    if (!avatarExistsOnDisk(friendnumber))
                        if (!tox.RequestAvatarData(friendnumber))
                            Console.WriteLine("Could not request avatar date from {0}: {1}", friendnumber, tox.GetName(friendnumber));

                    //if the avatar DOES exist on disk we're probably still loading it
                }
                else
                {
                    //let's compare this to the hash we have
                    if (tox.GetHash(friend.AvatarBytes).SequenceEqual(hash))
                    {
                        //we already have this avatar, ignore
                        return;
                    }
                    else
                    {
                        //that's interesting, let's ask for the avatar data
                        if (!tox.RequestAvatarData(friendnumber))
                            Console.WriteLine("Could not request avatar date from {0}: {1}", friendnumber, tox.GetName(friendnumber));
                    }
                }
            }
        }

        private void tox_OnAvatarData(int friendnumber, ToxAvatar avatar)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend == null)
                return;

            if (friend.AvatarBytes != null && tox.GetHash(friend.AvatarBytes).SequenceEqual(avatar.Hash))
            {
                //that's odd, why did we even receive this avatar?
                //let's ignore ..
                Console.WriteLine("Received avatar data unexpectedly, ignoring");
            }
            else
            {
                try
                {
                    //note: this might be dangerous, maybe we need a way of verifying that this isn't malicious data (but how?)
                    friend.AvatarBytes = avatar.Data;

                    MemoryStream stream = null;
                    try
                    {
                        stream = new MemoryStream(avatar.Data);

                        using (Bitmap bmp = new Bitmap(stream))
                        {
                            friend.Avatar = BitmapToImageSource(bmp, ImageFormat.Png);
                        }
                    }
                    finally
                    {
                        if (stream != null)
                            stream.Dispose();
                    }

                    string avatarsDir = Path.Combine(toxDataDir, "avatars");
                    if (!Directory.Exists(avatarsDir))
                        Directory.CreateDirectory(avatarsDir);

                    File.WriteAllBytes(Path.Combine(avatarsDir, tox.GetClientId(friendnumber).GetString() + ".png"), avatar.Data);
                }
                catch
                {
                    //looks like someone sent invalid image data, what a troll
                }
            }
        }

        private void tox_OnDisconnected()
        {
            SetStatus(ToxUserStatus.Invalid, false);
        }

        private void tox_OnConnected()
        {
            SetStatus(tox.GetSelfUserStatus(), false);
        }

        private async void Chat_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && tox.IsOnline(ViewModel.SelectedChatNumber))
            {
                var docPath = (string[]) e.Data.GetData(DataFormats.FileDrop);
                MetroDialogOptions.ColorScheme = MetroDialogColorScheme.Theme;

                var mySettings = new MetroDialogSettings()
                {
                    AffirmativeButtonText = "Yes",
                    FirstAuxiliaryButtonText = "Cancel",
                    AnimateShow = false,
                    AnimateHide = false,
                    ColorScheme = MetroDialogColorScheme.Theme
                };

                MessageDialogResult result = await this.ShowMessageAsync("Please confirm", "Are you sure you want to send this file?",
                MessageDialogStyle.AffirmativeAndNegative, mySettings);

                if (result == MessageDialogResult.Affirmative)
                {
                    SendFile(ViewModel.SelectedChatNumber, docPath[0]);
                }
            }
        }

        private void Chat_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && tox.IsOnline(ViewModel.SelectedChatNumber))
            {
                e.Effects = DragDropEffects.All;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = false;
        }

        private void loadTox()
        {
            if (File.Exists(toxDataFilename) && !config.Portable)
            {
                if (!tox.Load(toxDataFilename))
                {
                    MessageBox.Show("Could not load tox data, this program will now exit.", "Error");
                    Close();
                }
            }
            else if (File.Exists("tox_save"))
            {
                if (!config.Portable)
                {
                    if (tox.Load("tox_save"))
                    {
                        if (!Directory.Exists(toxDataDir))
                            Directory.CreateDirectory(toxDataDir);

                        if (tox.Save(toxDataFilename))
                            File.Delete("tox_save");
                    }
                }
                else
                {
                    if (!tox.Load("tox_save"))
                    {
                        MessageBox.Show("Could not load tox data, this program will now exit.", "Error");
                        Close();
                    }
                }
            }
            else if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tox\\data")))
            {
                MessageBoxResult result = MessageBox.Show("Could not find your tox_save file.\nWe did find a file named \"data\" located in AppData though, do you want to load this file?\nNote: We'll rename the \"data\" file to \"tox_save\" for you", "Notice", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tox");
                    File.Move(Path.Combine(path, "data"), Path.Combine(path, "tox_save"));

                    tox.Load(Path.Combine(path, "tox_save"));
                }
            }
            else if (File.Exists("data"))
            {
                MessageBoxResult result = MessageBox.Show("Could not find your tox_save file.\nWe did find a file named \"data\" though, do you want to load this file?\nNote: We'll rename the \"data\" file to \"tox_save\" for you", "Notice", MessageBoxButton.YesNo, MessageBoxImage.Question);
                MessageBoxResult result2 = MessageBox.Show("Do you want to move \"tox_save\" to AppData?\nThis will make it easier to switch between Tox clients.", "Notice", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tox\\tox_save");

                    if (result2 != MessageBoxResult.Yes)
                    {
                        File.Move("data", "tox_save");

                        config.Portable = true;
                        ConfigTools.Save(config, "config.xml");

                        tox.Load("tox_save");
                    }
                    else
                    {
                        if (!Directory.Exists(toxDataDir))
                            Directory.CreateDirectory(toxDataDir);

                        File.Move("data", path);

                        tox.Load(path);
                    }
                }
            }
        }

        private void applyConfig()
        {
            var accent = ThemeManager.GetAccent(config.AccentColor);
            var theme = ThemeManager.GetAppTheme(config.Theme);

            if (accent != null && theme != null)
                ThemeManager.ChangeAppStyle(System.Windows.Application.Current, accent, theme);

            ExecuteActionsOnNotifyIcon();
        }

        private void tox_OnReadReceipt(int friendnumber, uint receipt)
        {
            //a flowdocument should already be created, but hey, just in case
            if (!convdic.ContainsKey(friendnumber))
                return;

            Paragraph para = (Paragraph)convdic[friendnumber].FindChildren<TableRow>().Where(r => r.Tag.GetType() != typeof(FileTransfer) && ((MessageData)(r.Tag)).Id == receipt).First().FindChildren<TableCell>().ToArray()[1].Blocks.FirstBlock;

            if (para == null)
                return; //row or cell doesn't exist? odd, just return

            if (config.Theme == "BaseDark")
                para.Foreground = Brushes.White;
            else
                para.Foreground = Brushes.Black;
        }

        private void toxav_OnMediaChange(int call_index, IntPtr args)
        {
            if (call == null)
                return;

            //can't change the call type, we don't support video calls
            if (call.CallIndex == call_index)
                EndCall();
        }

        private void InitializeNotifyIcon()
        {
            Stream newMessageIconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Toxy;component/Resources/Icons/icon2.ico")).Stream;
            Stream iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Toxy;component/Resources/Icons/icon.ico")).Stream;

            notifyIcon = new Icon(iconStream);
            newMessageNotifyIcon = new Icon(newMessageIconStream);

            nIcon.Icon = notifyIcon;
            nIcon.MouseClick += nIcon_MouseClick;

            var trayIconContextMenu = new System.Windows.Forms.ContextMenu();
            var closeMenuItem = new System.Windows.Forms.MenuItem("Exit", closeMenuItem_Click);
            var openMenuItem = new System.Windows.Forms.MenuItem("Open", openMenuItem_Click);

            var statusMenuItem = new System.Windows.Forms.MenuItem("Status");
            var setOnlineMenuItem = new System.Windows.Forms.MenuItem("Online", setStatusMenuItem_Click);
            var setAwayMenuItem = new System.Windows.Forms.MenuItem("Away", setStatusMenuItem_Click);
            var setBusyMenuItem = new System.Windows.Forms.MenuItem("Busy", setStatusMenuItem_Click);

            setOnlineMenuItem.Tag = 0; // Online
            setAwayMenuItem.Tag = 1; // Away
            setBusyMenuItem.Tag = 2; // Busy

            statusMenuItem.MenuItems.Add(setOnlineMenuItem);
            statusMenuItem.MenuItems.Add(setAwayMenuItem);
            statusMenuItem.MenuItems.Add(setBusyMenuItem);

            trayIconContextMenu.MenuItems.Add(openMenuItem);
            trayIconContextMenu.MenuItems.Add(statusMenuItem);
            trayIconContextMenu.MenuItems.Add(closeMenuItem);
            nIcon.ContextMenu = trayIconContextMenu;
        }

        private void nIcon_MouseClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left)
                return;

            if (WindowState != WindowState.Normal)
            {
                Show();
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
            }
            else
            {
                Hide();
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
            }
        }

        private void setStatusMenuItem_Click(object sender, EventArgs eventArgs)
        {
            if (tox.IsConnected())
                SetStatus((ToxUserStatus)((System.Windows.Forms.MenuItem)sender).Tag, true);
        }

        private void openMenuItem_Click(object sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
        }

        private void closeMenuItem_Click(object sender, EventArgs eventArgs)
        {
            config.HideInTray = false;
            Close();
        }

        private void toxav_OnReceivedAudio(IntPtr toxav, int call_index, short[] frame, int frame_size, IntPtr userdata)
        {
            if (call == null)
                return;

            call.ProcessAudioFrame(frame, frame_size);
        }

        public MainWindowViewModel ViewModel
        {
            get { return DataContext as MainWindowViewModel; }
        }

        private void toxav_OnEnd(int call_index, IntPtr args)
        {
            EndCall();
            CallButton.Visibility = Visibility.Visible;
            HangupButton.Visibility = Visibility.Collapsed;
        }

        private void toxav_OnStart(int call_index, IntPtr args)
        {
            if (call != null)
                call.Start(config.InputDevice, config.OutputDevice);

            int friendnumber = toxav.GetPeerID(call_index, 0);
            var callingFriend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (callingFriend != null)
            {
                callingFriend.IsCalling = false;
                callingFriend.IsCallingToFriend = false;
                CallButton.Visibility = Visibility.Collapsed;
                if (callingFriend.Selected)
                {
                    HangupButton.Visibility = Visibility.Visible;
                }
                ViewModel.CallingFriend = callingFriend;
            }
        }

        private void toxav_OnInvite(int call_index, IntPtr args)
        {
            //TODO: notify the user of another incoming call
            if (call != null)
                return;

            int friendnumber = toxav.GetPeerID(call_index, 0);

            ToxAvCodecSettings settings = toxav.GetPeerCodecSettings(call_index, 0);
            if (settings.CallType == ToxAvCallType.Video)
            {
                //we don't support video calls, just reject this and return.
                toxav.Reject(call_index, "Toxy does not support video calls.");
                return;
            }

            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null)
            {
                friend.CallIndex = call_index;
                friend.IsCalling = true;
            }
        }

        private void tox_OnGroupNamelistChange(int groupnumber, int peernumber, ToxChatChange change)
        {
            var group = ViewModel.GetGroupObjectByNumber(groupnumber);
            if (group != null)
            {
                if (change == ToxChatChange.PeerAdd || change == ToxChatChange.PeerDel)
                {
                    var status = string.Format("Peers online: {0}", tox.GetGroupMemberCount(group.ChatNumber));
                    group.StatusMessage = status;
                }
                if (group.Selected)
                {
                    group.AdditionalInfo = string.Join(", ", tox.GetGroupNames(group.ChatNumber));
                }
            }
        }

        private void tox_OnGroupAction(int groupnumber, int friendgroupnumber, string action)
        {
            MessageData data = new MessageData() { Username = "*  ", Message = string.Format("{0} {1}", tox.GetGroupMemberName(groupnumber, friendgroupnumber), action), IsAction = true };

            if (groupdic.ContainsKey(groupnumber))
            {
                groupdic[groupnumber].AddNewMessageRow(tox, data, false);
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                groupdic.Add(groupnumber, document);
                groupdic[groupnumber].AddNewMessageRow(tox, data, false);
            }

            var group = ViewModel.GetGroupObjectByNumber(groupnumber);
            if (group != null)
            {
                if (!group.Selected)
                {
                    MessageAlertIncrement(group);
                }
                else
                {
                    ScrollChatBox();
                }
            }
            if (ViewModel.MainToxyUser.ToxStatus != ToxUserStatus.Busy)
                this.Flash();
        }

        private void tox_OnGroupMessage(int groupnumber, int friendgroupnumber, string message)
        {
            MessageData data = new MessageData() { Username = tox.GetGroupMemberName(groupnumber, friendgroupnumber), Message = message };

            if (groupdic.ContainsKey(groupnumber))
            {
                var run = GetLastMessageRun(groupdic[groupnumber]);

                if (run != null)
                {
                    if (((MessageData)run.Tag).Username == data.Username)
                        groupdic[groupnumber].AddNewMessageRow(tox, data, true);
                    else
                        groupdic[groupnumber].AddNewMessageRow(tox, data, false);
                }
                else
                {
                    groupdic[groupnumber].AddNewMessageRow(tox, data, false);
                }
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                groupdic.Add(groupnumber, document);
                groupdic[groupnumber].AddNewMessageRow(tox, data, false);
            }

            var group = ViewModel.GetGroupObjectByNumber(groupnumber);
            if (group != null)
            {
                if (!group.Selected)
                {
                    MessageAlertIncrement(group);
                }
                else
                {
                    ScrollChatBox();
                }
            }
            if (ViewModel.MainToxyUser.ToxStatus != ToxUserStatus.Busy)
                this.Flash();

            nIcon.Icon = newMessageNotifyIcon;
            ViewModel.HasNewMessage = true;
        }

        private void tox_OnGroupInvite(int friendnumber, byte[] data)
        {
            int number = tox.JoinGroup(friendnumber, data);
            var group = ViewModel.GetGroupObjectByNumber(number);

            if (group != null)
                SelectGroupControl(group);
            else if (number != -1)
                AddGroupToView(number);
        }

        private void tox_OnFriendRequest(string id, string message)
        {
            try
            {
                AddFriendRequestToView(id, message);
                if (ViewModel.MainToxyUser.ToxStatus != ToxUserStatus.Busy)
                    this.Flash();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }

            nIcon.Icon = newMessageNotifyIcon;
            ViewModel.HasNewMessage = true;
        }

        private void tox_OnFileControl(int friendnumber, int receive_send, int filenumber, int control_type, byte[] data)
        {
            switch ((ToxFileControl)control_type)
            {
                case ToxFileControl.Finished:
                    {
                        FileTransfer ft = GetFileTransfer(friendnumber, filenumber);

                        if (ft == null)
                            return;

                        ft.Stream.Close();
                        ft.Stream = null;

                        ft.Control.TransferFinished();
                        ft.Control.SetStatus("Finished!");
                        ft.Finished = true;

                        transfers.Remove(ft);

                        tox.FileSendControl(ft.FriendNumber, 1, ft.FileNumber, ToxFileControl.Finished, new byte[0]);
                        break;
                    }

                case ToxFileControl.Accept:
                    {
                        FileTransfer ft = GetFileTransfer(friendnumber, filenumber);
                        ft.Control.SetStatus("Transferring...");

                        if (ft.Stream == null)
                        {
                            ft.Stream = new FileStream(ft.FileName, FileMode.Open);
                        }

                        ft.Thread = new Thread(transferFile);
                        ft.Thread.Start(ft);

                        break;
                    }

                case ToxFileControl.Kill:
                    {
                        FileTransfer transfer = GetFileTransfer(friendnumber, filenumber);
                        transfer.Finished = true;
                        if (transfer != null)
                        {
                            if (transfer.Stream != null)
                                transfer.Stream.Close();

                            if (transfer.Thread != null)
                            {
                                transfer.Thread.Abort();
                                transfer.Thread.Join();
                            }

                            transfer.Control.HideAllButtons();
                            transfer.Control.SetStatus("Transfer killed!");
                        }

                        break;
                    }
            }
        }

        private void transferFile(object ft)
        {
            FileTransfer transfer = (FileTransfer)ft;

            ToxHandle handle = tox.GetHandle();
            int chunk_size = tox.FileDataSize(transfer.FriendNumber);
            byte[] buffer = new byte[chunk_size];

            while (true)
            {
                ulong remaining = tox.FileDataRemaining(transfer.FriendNumber, transfer.FileNumber, 0);
                if (remaining > (ulong)chunk_size)
                {
                    if (transfer.Stream.Read(buffer, 0, chunk_size) == 0)
                        break;

                    while (!tox.FileSendData(transfer.FriendNumber, transfer.FileNumber, buffer))
                    {
                        int time = (int)ToxFunctions.DoInterval(handle);

                        Console.WriteLine("Could not send data, sleeping for {0}ms", time);
                        Thread.Sleep(time);
                    }

                    Console.WriteLine("Data sent: {0} bytes", buffer.Length);
                }
                else
                {
                    buffer = new byte[remaining];

                    if (transfer.Stream.Read(buffer, 0, (int)remaining) == 0)
                        break;

                    tox.FileSendData(transfer.FriendNumber, transfer.FileNumber, buffer);

                    Console.WriteLine("Sent the last chunk of data: {0} bytes", buffer.Length);
                }

                double value = (double)remaining / (double)transfer.FileSize;
                transfer.Control.SetProgress(100 - (int)(value * 100));
            }

            transfer.Stream.Close();
            tox.FileSendControl(transfer.FriendNumber, 0, transfer.FileNumber, ToxFileControl.Finished, new byte[0]);

            transfer.Control.HideAllButtons();
            transfer.Control.SetStatus("Finished!");
            transfer.Finished = true;
        }

        private FileTransfer GetFileTransfer(int friendnumber, int filenumber)
        {
            foreach (FileTransfer ft in transfers)
                if (ft.FileNumber == filenumber && ft.FriendNumber == friendnumber && !ft.Finished)
                    return ft;

            return null;
        }

        private void tox_OnFileData(int friendnumber, int filenumber, byte[] data)
        {
            FileTransfer ft = GetFileTransfer(friendnumber, filenumber);

            if (ft == null)
                return;

            if (ft.Stream == null)
                throw new NullReferenceException("Unexpectedly received data");

            ulong remaining = tox.FileDataRemaining(friendnumber, filenumber, 1);
            double value = (double)remaining / (double)ft.FileSize;

            ft.Control.SetProgress(100 - (int)(value * 100));
            ft.Control.SetStatus(string.Format("{0}/{1}", ft.FileSize - remaining, ft.FileSize));

            if (ft.Stream.CanWrite)
                ft.Stream.Write(data, 0, data.Length);
        }

        private void tox_OnFileSendRequest(int friendnumber, int filenumber, ulong filesize, string filename)
        {
            if (!convdic.ContainsKey(friendnumber))
                convdic.Add(friendnumber, GetNewFlowDocument());

            FileTransfer transfer = convdic[friendnumber].AddNewFileTransfer(tox, friendnumber, filenumber, filename, filesize, false);

            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null && !friend.Selected)
            {
                MessageAlertIncrement(friend);
            }

            transfer.Control.OnAccept += delegate(int friendnum, int filenum)
            {
                if (transfer.Stream != null)
                    return;

                SaveFileDialog dialog = new SaveFileDialog();
                dialog.FileName = filename;

                if (dialog.ShowDialog() == true) //guess what, this bool is nullable
                {
                    transfer.Stream = new FileStream(dialog.FileName, FileMode.Create);
                    transfer.FileName = dialog.FileName;
                    transfer.Control.FilePath = dialog.FileName;
                    tox.FileSendControl(friendnumber, 1, filenumber, ToxFileControl.Accept, new byte[0]);
                }
            };

            transfer.Control.OnDecline += delegate(int friendnum, int filenum)
            {
                if (!transfer.IsSender)
                    tox.FileSendControl(friendnumber, 1, filenumber, ToxFileControl.Kill, new byte[0]);
                else
                    tox.FileSendControl(friendnumber, 0, filenumber, ToxFileControl.Kill, new byte[0]);

                if (transfer.Thread != null)
                {
                    transfer.Thread.Abort();
                    transfer.Thread.Join();
                }

                if (transfer.Stream != null)
                    transfer.Stream.Close();

                transfer.Finished = true;
            };

            transfer.Control.OnFileOpen += delegate()
            {
                try { Process.Start(transfer.FileName); }
                catch { /*want to open a "choose program dialog" here*/ }
            };

            transfer.Control.OnFolderOpen += delegate()
            {
                Process.Start("explorer.exe", @"/select, " + transfer.FileName);
            };

            transfers.Add(transfer);
            if (ViewModel.MainToxyUser.ToxStatus != ToxUserStatus.Busy)
                this.Flash();

            ScrollChatBox();
        }

        private void tox_OnConnectionStatusChanged(int friendnumber, ToxFriendConnectionStatus status)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend == null)
                return;

            if (status == ToxFriendConnectionStatus.Offline)
            {
                DateTime lastOnline = TimeZoneInfo.ConvertTime(tox.GetLastOnline(friendnumber), TimeZoneInfo.Utc, TimeZoneInfo.Local);

                if (lastOnline.Year == 1970) //quick and dirty way to check if we're dealing with epoch 0
                    friend.StatusMessage = "Friend request sent";
                else
                    friend.StatusMessage = string.Format("Last seen: {0} {1}", lastOnline.ToShortDateString(), lastOnline.ToLongTimeString());

                friend.ToxStatus = ToxUserStatus.Invalid; //not the proper way to do it, I know...

                if (friend.Selected)
                {
                    CallButton.Visibility = Visibility.Collapsed;
                    FileButton.Visibility = Visibility.Collapsed;
                }
            }
            else if (status == ToxFriendConnectionStatus.Online)
            {
                friend.StatusMessage = tox.GetStatusMessage(friend.ChatNumber);

                if (friend.Selected)
                {
                    CallButton.Visibility = Visibility.Visible;
                    FileButton.Visibility = Visibility.Visible;
                }

                //kinda ugly to do this every time, I guess we don't really have a choice
                tox.RequestAvatarInfo(friendnumber);
            }
        }

        private void tox_OnTypingChange(int friendnumber, bool is_typing)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend == null)
                return;

            if (friend.Selected)
            {
                if (is_typing)
                    TypingStatusLabel.Content = tox.GetName(friendnumber) + " is typing...";
                else
                    TypingStatusLabel.Content = "";
            }
        }

        private void tox_OnFriendAction(int friendnumber, string action)
        {
            MessageData data = new MessageData() { Username = "*  ", Message = string.Format("{0} {1}", tox.GetName(friendnumber), action), IsAction = true };

            if (convdic.ContainsKey(friendnumber))
            {
                convdic[friendnumber].AddNewMessageRow(tox, data, false);
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                convdic.Add(friendnumber, document);
                convdic[friendnumber].AddNewMessageRow(tox, data, false);
            }

            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null)
            {
                if (!friend.Selected)
                {
                    MessageAlertIncrement(friend);
                }
                else
                {
                    ScrollChatBox();
                }
            }
            if (ViewModel.MainToxyUser.ToxStatus != ToxUserStatus.Busy)
                this.Flash();

            nIcon.Icon = newMessageNotifyIcon;
            ViewModel.HasNewMessage = true;
        }

        private FlowDocument GetNewFlowDocument()
        {
            Stream doc_stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Toxy.Message.xaml");
            FlowDocument doc = (FlowDocument)XamlReader.Load(doc_stream);
            doc.IsEnabled = true;

            return doc;
        }

        private void tox_OnFriendMessage(int friendnumber, string message)
        {
            MessageData data = new MessageData() { Username = tox.GetName(friendnumber), Message = message };

            if (convdic.ContainsKey(friendnumber))
            {
                var run = GetLastMessageRun(convdic[friendnumber]);

                if (run != null)
                {
                    if (((MessageData)run.Tag).Username == tox.GetName(friendnumber))
                        convdic[friendnumber].AddNewMessageRow(tox, data, true);
                    else
                        convdic[friendnumber].AddNewMessageRow(tox, data, false);
                }
                else
                {
                    convdic[friendnumber].AddNewMessageRow(tox, data, false);
                }
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                convdic.Add(friendnumber, document);
                convdic[friendnumber].AddNewMessageRow(tox, data, false);
            }

            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null)
            {
                if (!friend.Selected)
                {
                    MessageAlertIncrement(friend);
                }
                else
                {
                    ScrollChatBox();
                }
            }
            if (ViewModel.MainToxyUser.ToxStatus != ToxUserStatus.Busy)
                this.Flash();

            nIcon.Icon = newMessageNotifyIcon;
            ViewModel.HasNewMessage = true;
        }

        private void ScrollChatBox()
        {
            ScrollViewer viewer = FindScrollViewer(ChatBox);

            if (viewer != null)
                if (viewer.ScrollableHeight == viewer.VerticalOffset)
                    viewer.ScrollToBottom();
        }

        private static ScrollViewer FindScrollViewer(FlowDocumentScrollViewer viewer)
        {
            if (VisualTreeHelper.GetChildrenCount(viewer) == 0)
                return null;

            DependencyObject first = VisualTreeHelper.GetChild(viewer, 0);
            if (first == null)
                return null;

            Decorator border = (Decorator)VisualTreeHelper.GetChild(first, 0);
            if (border == null)
                return null;

            return (ScrollViewer)border.Child;
        }

        private TableRow GetLastMessageRun(FlowDocument doc)
        {
            try
            {
                return doc.FindChildren<TableRow>().Last(t => t.Tag.GetType() != typeof(FileTransfer));
            }
            catch
            {
                return null;
            }
        }

        private void NewGroupButton_Click(object sender, RoutedEventArgs e)
        {
            int groupnumber = tox.NewGroup();
            if (groupnumber != -1)
            {
                AddGroupToView(groupnumber);
            }
        }

        private void tox_OnUserStatus(int friendnumber, ToxUserStatus status)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null)
            {
                friend.ToxStatus = status;
            }
        }

        private void tox_OnStatusMessage(int friendnumber, string newstatus)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null)
            {
                friend.StatusMessage = newstatus;
            }
        }

        private void tox_OnNameChange(int friendnumber, string newname)
        {
            var friend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (friend != null)
            {
                friend.Name = newname;
            }
        }

        private void InitFriends()
        {
            //Creates a new FriendControl for every friend
            foreach (var friendNumber in tox.GetFriendlist())
            {
                AddFriendToView(friendNumber);
            }
        }

        private void AddGroupToView(int groupnumber)
        {
            string groupname = string.Format("Groupchat #{0}", groupnumber);

            var groupMV = new GroupControlModelView();
            groupMV.ChatNumber = groupnumber;
            groupMV.Name = groupname;
            groupMV.StatusMessage = string.Format("Peers online: {0}", tox.GetGroupMemberCount(groupnumber));//string.Join(", ", tox.GetGroupNames(groupnumber));
            groupMV.SelectedAction = GroupSelectedAction;
            groupMV.DeleteAction = GroupDeleteAction;

            ViewModel.ChatCollection.Add(groupMV);
        }

        private void GroupDeleteAction(IGroupObject groupObject)
        {
            ViewModel.ChatCollection.Remove(groupObject);
            int groupNumber = groupObject.ChatNumber;
            if (groupdic.ContainsKey(groupNumber))
            {
                groupdic.Remove(groupNumber);

                if (groupObject.Selected)
                    ChatBox.Document = null;
            }
            tox.DeleteGroupChat(groupNumber);
            groupObject.SelectedAction = null;
            groupObject.DeleteAction = null;
        }

        private void GroupSelectedAction(IGroupObject groupObject, bool isSelected)
        {
            MessageAlertClear(groupObject);

            TypingStatusLabel.Content = "";

            if (isSelected)
            {
                SelectGroupControl(groupObject);
                ScrollChatBox();
                TextToSend.Focus();
            }
        }

        private void AddFriendToView(int friendNumber)
        {
            string friendStatus;
            if (tox.IsOnline(friendNumber))
            {
                friendStatus = tox.GetStatusMessage(friendNumber);
            }
            else
            {
                DateTime lastOnline = TimeZoneInfo.ConvertTime(tox.GetLastOnline(friendNumber), TimeZoneInfo.Utc, TimeZoneInfo.Local);

                if (lastOnline.Year == 1970)
                    friendStatus = "Friend request sent";
                else
                    friendStatus = string.Format("Last seen: {0} {1}", lastOnline.ToShortDateString(), lastOnline.ToLongTimeString());
            }

            string friendName = tox.GetName(friendNumber);
            if (string.IsNullOrEmpty(friendName))
            {
                friendName = tox.GetClientId(friendNumber).GetString();
            }

            var friendMV = new FriendControlModelView(ViewModel);
            friendMV.ChatNumber = friendNumber;
            friendMV.Name = friendName;
            friendMV.StatusMessage = friendStatus;
            friendMV.ToxStatus = ToxUserStatus.Invalid;
            friendMV.SelectedAction = FriendSelectedAction;
            friendMV.DenyCallAction = FriendDenyCallAction;
            friendMV.AcceptCallAction = FriendAcceptCallAction;
            friendMV.CopyIDAction = FriendCopyIdAction;
            friendMV.DeleteAction = FriendDeleteAction;
            friendMV.GroupInviteAction = GroupInviteAction;
            friendMV.HangupAction = FriendHangupAction;

            ViewModel.ChatCollection.Add(friendMV);
        }

        private void FriendHangupAction(IFriendObject friendObject)
        {
            EndCall(friendObject);
        }

        private void GroupInviteAction(IFriendObject friendObject, IGroupObject groupObject)
        {
            tox.InviteFriend(friendObject.ChatNumber, groupObject.ChatNumber);
        }

        private void FriendDeleteAction(IFriendObject friendObject)
        {
            ViewModel.ChatCollection.Remove(friendObject);
            var friendNumber = friendObject.ChatNumber;
            if (convdic.ContainsKey(friendNumber))
            {
                convdic.Remove(friendNumber);
                if (friendObject.Selected)
                {
                    ChatBox.Document = null;
                }
            }
            tox.DeleteFriend(friendNumber);
            friendObject.SelectedAction = null;
            friendObject.DenyCallAction = null;
            friendObject.AcceptCallAction = null;
            friendObject.CopyIDAction = null;
            friendObject.DeleteAction = null;
            friendObject.GroupInviteAction = null;
            friendObject.MainViewModel = null;

            saveTox();
        }

        private void saveTox()
        {
            if (!config.Portable)
            {
                if (!Directory.Exists(toxDataDir))
                    Directory.CreateDirectory(toxDataDir);
            }

            tox.Save(toxDataFilename);
        }

        private void FriendCopyIdAction(IFriendObject friendObject)
        {
            Clipboard.Clear();
            Clipboard.SetText(tox.GetClientId(friendObject.ChatNumber).GetString());
        }

        private void FriendSelectedAction(IFriendObject friendObject, bool isSelected)
        {
            MessageAlertClear(friendObject);

            if (isSelected)
            {
                if (!tox.GetIsTyping(friendObject.ChatNumber))
                    TypingStatusLabel.Content = "";
                else
                    TypingStatusLabel.Content = tox.GetName(friendObject.ChatNumber) + " is typing...";

                SelectFriendControl(friendObject);
                ScrollChatBox();
                TextToSend.Focus();
            }
        }

        private void FriendAcceptCallAction(IFriendObject friendObject)
        {
            if (call != null)
                return;

            call = new ToxCall(toxav, friendObject.CallIndex, friendObject.ChatNumber);
            call.Answer();
        }

        private void FriendDenyCallAction(IFriendObject friendObject)
        {
            if (call == null)
            {
                toxav.Reject(friendObject.CallIndex, "I'm busy...");
                friendObject.IsCalling = false;
            }
            else
            {
                call.Stop();
                call = null;
            }
        }

        private void AddFriendRequestToView(string id, string message)
        {
            var friendMV = new FriendControlModelView(ViewModel);
            friendMV.IsRequest = true;
            friendMV.Name = id;
            friendMV.ToxStatus = ToxUserStatus.Invalid;
            friendMV.RequestMessageData = new MessageData() { Message = message, Username = "Request Message" };
            friendMV.RequestFlowDocument = GetNewFlowDocument();
            friendMV.SelectedAction = FriendRequestSelectedAction;
            friendMV.AcceptAction = FriendRequestAcceptAction;
            friendMV.DeclineAction = FriendRequestDeclineAction;

            ViewModel.ChatRequestCollection.Add(friendMV);

            if (ListViewTabControl.SelectedIndex != 1)
            {
                RequestsTabItem.Header = "Requests*";
            }
        }

        private void FriendRequestSelectedAction(IFriendObject friendObject, bool isSelected)
        {
            friendObject.RequestFlowDocument.AddNewMessageRow(tox, friendObject.RequestMessageData, false);
        }

        private void FriendRequestAcceptAction(IFriendObject friendObject)
        {
            int friendnumber = tox.AddFriendNoRequest(friendObject.Name);

            AddFriendToView(friendnumber);

            ViewModel.ChatRequestCollection.Remove(friendObject);
            friendObject.RequestFlowDocument = null;
            friendObject.SelectedAction = null;
            friendObject.AcceptAction = null;
            friendObject.DeclineAction = null;
            friendObject.MainViewModel = null;

            saveTox();
        }

        private void FriendRequestDeclineAction(IFriendObject friendObject)
        {
            ViewModel.ChatRequestCollection.Remove(friendObject);
            friendObject.RequestFlowDocument = null;
            friendObject.SelectedAction = null;
            friendObject.AcceptAction = null;
            friendObject.DeclineAction = null;
        }

        private void SelectGroupControl(IGroupObject group)
        {
            if (group == null)
            {
                return;
            }

            CallButton.Visibility = Visibility.Collapsed;
            FileButton.Visibility = Visibility.Collapsed;

            group.AdditionalInfo = string.Join(", ", tox.GetGroupNames(group.ChatNumber));

            if (groupdic.ContainsKey(group.ChatNumber))
            {
                ChatBox.Document = groupdic[group.ChatNumber];
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                groupdic.Add(group.ChatNumber, document);
                ChatBox.Document = groupdic[group.ChatNumber];
            }
        }

        private void EndCall()
        {
            if (call != null)
            {
                var friendnumber = toxav.GetPeerID(call.CallIndex, 0);
                var friend = ViewModel.GetFriendObjectByNumber(friendnumber);

                EndCall(friend);
            }
            else
            {
                EndCall(null);
            }
        }

        private void EndCall(IFriendObject friend)
        {
            if (friend != null)
            {
                toxav.Cancel(friend.CallIndex, friend.ChatNumber, "I'm busy...");

                friend.IsCalling = false;
                friend.IsCallingToFriend = false;
            }

            if (call != null)
            {
                call.Stop();
                call = null;
            }

            ViewModel.CallingFriend = null;

            HangupButton.Visibility = Visibility.Collapsed;
            CallButton.Visibility = Visibility.Visible;
        }

        private void SelectFriendControl(IFriendObject friend)
        {
            if (friend == null)
            {
                return;
            }
            int friendNumber = friend.ChatNumber;

            if (call != null)
            {
                if (call.FriendNumber != friendNumber)
                    HangupButton.Visibility = Visibility.Collapsed;
                else
                    HangupButton.Visibility = Visibility.Visible;
            }
            else
            {
                if (!tox.IsOnline(friendNumber))
                {
                    CallButton.Visibility = Visibility.Collapsed;
                    FileButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CallButton.Visibility = Visibility.Visible;
                    FileButton.Visibility = Visibility.Visible;
                }
            }

            if (convdic.ContainsKey(friend.ChatNumber))
            {
                ChatBox.Document = convdic[friend.ChatNumber];
            }
            else
            {
                FlowDocument document = GetNewFlowDocument();
                convdic.Add(friend.ChatNumber, document);
                ChatBox.Document = convdic[friend.ChatNumber];
            }
        }

        private void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (config.HideInTray)
            {
                e.Cancel = true;
                ShowInTaskbar = false;
                WindowState = WindowState.Minimized;
                Hide();
            }
            else
            {
                if (call != null)
                    call.Stop();

                foreach (FileTransfer transfer in transfers)
                {
                    if (transfer.Thread != null)
                    {
                        //TODO: show a message warning the users that there are still file transfers in progress
                        transfer.Thread.Abort();
                        transfer.Thread.Join();
                    }
                }

                saveTox();

                toxav.Dispose();
                tox.Dispose();

                nIcon.Dispose();
            }
        }

        private void OpenAddFriend_Click(object sender, RoutedEventArgs e)
        {
            FriendFlyout.IsOpen = !FriendFlyout.IsOpen;
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!SettingsFlyout.IsOpen)
            {
                SettingsUsername.Text = tox.GetSelfName();
                SettingsStatus.Text = tox.GetSelfStatusMessage();
                SettingsNospam.Text = tox.GetNospam().ToString();

                Tuple<AppTheme, Accent> style = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
                Accent accent = ThemeManager.GetAccent(style.Item2.Name);
                oldAccent = accent;
                if (accent != null)
                    AccentComboBox.SelectedItem = AccentComboBox.Items.Cast<AccentColorMenuData>().Single(a => a.Name == style.Item2.Name);

                AppTheme theme = ThemeManager.GetAppTheme(style.Item1.Name);
                oldAppTheme = theme;
                if (theme != null)
                    AppThemeComboBox.SelectedItem = AppThemeComboBox.Items.Cast<AppThemeMenuData>().Single(a => a.Name == style.Item1.Name);

                if (InputDevicesComboBox.Items.Count - 1 >= config.InputDevice)
                    InputDevicesComboBox.SelectedIndex = config.InputDevice;

                if (OutputDevicesComboBox.Items.Count - 1 >= config.OutputDevice)
                    OutputDevicesComboBox.SelectedIndex = config.OutputDevice;

                HideInTrayCheckBox.IsChecked = config.HideInTray;
                PortableCheckBox.IsChecked = config.Portable;
                AudioNotificationCheckBox.IsChecked = config.EnableAudioNotifications;
            }

            SettingsFlyout.IsOpen = !SettingsFlyout.IsOpen;
        }

        private void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            TextRange message = new TextRange(AddFriendMessage.Document.ContentStart, AddFriendMessage.Document.ContentEnd);

            if (!(!string.IsNullOrWhiteSpace(AddFriendID.Text) && message.Text != null))
                return;

            string friendID = AddFriendID.Text.Trim();

            if (friendID.Contains("@"))
            {
                try
                {
                    string id = DnsTools.DiscoverToxID(friendID, config.NameServices);

                    if (string.IsNullOrEmpty(id))
                        throw new Exception("The server returned an empty result");

                    AddFriendID.Text = id;
                }
                catch (Exception ex)
                {
                    this.ShowMessageAsync("Could not find a tox id", ex.Message.ToString());
                }

                return;
            }

            try
            {
                int friendnumber = tox.AddFriend(friendID, message.Text);
                FriendFlyout.IsOpen = false;
                AddFriendToView(friendnumber);
            }
            catch (ToxAFException ex)
            {
                if (ex.Error != ToxAFError.SetNewNospam)
                    this.ShowMessageAsync("An error occurred", Tools.GetAFError(ex.Error));
                
                return;
            }
            catch
            {
                this.ShowMessageAsync("An error occurred", "The ID you entered is not valid.");
                return;
            }

            AddFriendID.Text = string.Empty;
            AddFriendMessage.Document.Blocks.Clear();
            AddFriendMessage.Document.Blocks.Add(new Paragraph(new Run("Hello, I'd like to add you to my friends list.")));

            saveTox();
            FriendFlyout.IsOpen = false;
        }

        private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            tox.SetName(SettingsUsername.Text);
            tox.SetStatusMessage(SettingsStatus.Text);

            uint nospam;
            if (uint.TryParse(SettingsNospam.Text, out nospam))
                tox.SetNospam(nospam);

            ViewModel.MainToxyUser.Name = SettingsUsername.Text;
            ViewModel.MainToxyUser.StatusMessage = SettingsStatus.Text;

            config.HideInTray = HideInTrayCheckBox.IsChecked ?? false;

            SettingsFlyout.IsOpen = false;

            if (AccentComboBox.SelectedItem != null)
            {
                string accentName = ((AccentColorMenuData)AccentComboBox.SelectedItem).Name;
                var theme = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
                var accent = ThemeManager.GetAccent(accentName);
                ThemeManager.ChangeAppStyle(System.Windows.Application.Current, accent, theme.Item1);

                config.AccentColor = accentName;
            }

            if (AppThemeComboBox.SelectedItem != null)
            {
                string themeName = ((AppThemeMenuData)AppThemeComboBox.SelectedItem).Name;
                var theme = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
                var appTheme = ThemeManager.GetAppTheme(themeName);
                ThemeManager.ChangeAppStyle(System.Windows.Application.Current, theme.Item2, appTheme);

                config.Theme = themeName;
            }

            int index = InputDevicesComboBox.SelectedIndex + 1;
            if (index != 0 && WaveIn.DeviceCount > 0 && WaveIn.DeviceCount >= index)
                config.InputDevice = index - 1;

            index = OutputDevicesComboBox.SelectedIndex + 1;
            if (index != 0 && WaveOut.DeviceCount > 0 && WaveOut.DeviceCount >= index)
                config.OutputDevice = index - 1;

            config.Portable = (bool)PortableCheckBox.IsChecked;
            config.EnableAudioNotifications = (bool)AudioNotificationCheckBox.IsChecked;
            ExecuteActionsOnNotifyIcon();

            ConfigTools.Save(config, "config.xml");
            saveTox();
        }

        private void TextToSend_KeyDown(object sender, KeyEventArgs e)
        {
            string text = TextToSend.Text;

            if (e.Key == Key.Enter)
            {
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    TextToSend.Text += Environment.NewLine;
                    TextToSend.CaretIndex = TextToSend.Text.Length;
                    return;
                }

                if (e.IsRepeat)
                    return;

                if (string.IsNullOrEmpty(text))
                    return;

                var selectedChatNumber = ViewModel.SelectedChatNumber;
                if (!tox.IsOnline(selectedChatNumber) && ViewModel.IsFriendSelected)
                    return;

                if (text.StartsWith("/me "))
                {
                    //action
                    string action = text.Substring(4);
                    int messageid = -1;

                    if (ViewModel.IsFriendSelected)
                        messageid = tox.SendAction(selectedChatNumber, action);
                    else if (ViewModel.IsGroupSelected)
                        tox.SendGroupAction(selectedChatNumber, action);

                    MessageData data = new MessageData() { Username = "*  ", Message = string.Format("{0} {1}", tox.GetSelfName(), action), IsAction = true, Id = messageid, IsSelf = ViewModel.IsFriendSelected };

                    if (ViewModel.IsFriendSelected)
                    {
                        if (convdic.ContainsKey(selectedChatNumber))
                        {
                            convdic[selectedChatNumber].AddNewMessageRow(tox, data, false);
                        }
                        else
                        {
                            FlowDocument document = GetNewFlowDocument();
                            convdic.Add(selectedChatNumber, document);
                            convdic[selectedChatNumber].AddNewMessageRow(tox, data, false);
                        }
                    }
                }
                else
                {
                    //regular message
                    foreach (string message in text.WordWrap(ToxConstants.MaxMessageLength))
                    {
                        int messageid = -1;

                        if (ViewModel.IsFriendSelected)
                            messageid = tox.SendMessage(selectedChatNumber, message);
                        else if (ViewModel.IsGroupSelected)
                            tox.SendGroupMessage(selectedChatNumber, message);

                        MessageData data = new MessageData() { Username = tox.GetSelfName(), Message = message, Id = messageid, IsSelf = ViewModel.IsFriendSelected };

                        if (ViewModel.IsFriendSelected)
                        {
                            if (convdic.ContainsKey(selectedChatNumber))
                            {
                                var run = GetLastMessageRun(convdic[selectedChatNumber]);
                                if (run != null)
                                {
                                    if (((MessageData)run.Tag).Username == data.Username)
                                        convdic[selectedChatNumber].AddNewMessageRow(tox, data, true);
                                    else
                                        convdic[selectedChatNumber].AddNewMessageRow(tox, data, false);
                                }
                                else
                                    convdic[selectedChatNumber].AddNewMessageRow(tox, data, false);
                            }
                            else
                            {
                                FlowDocument document = GetNewFlowDocument();
                                convdic.Add(selectedChatNumber, document);
                                convdic[selectedChatNumber].AddNewMessageRow(tox, data, false);
                            }
                        }
                    }
                }

                ScrollChatBox();

                TextToSend.Text = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && ViewModel.IsGroupSelected)
            {
                string[] names = tox.GetGroupNames(ViewModel.SelectedChatNumber);

                foreach (string name in names)
                {
                    if (!name.ToLower().StartsWith(text.ToLower()))
                        continue;

                    TextToSend.Text = string.Format("{0}, ", name);
                    TextToSend.SelectionStart = TextToSend.Text.Length;
                }

                e.Handled = true;
            }
        }

        private void GithubButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://github.com/Reverp/Toxy-WPF");
        }

        private void TextToSend_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!ViewModel.IsFriendSelected)
                return;

            string text = TextToSend.Text;

            if (string.IsNullOrEmpty(text))
            {
                if (typing)
                {
                    typing = false;
                    tox.SetUserIsTyping(ViewModel.SelectedChatNumber, typing);
                }
            }
            else
            {
                if (!typing)
                {
                    typing = true;
                    tox.SetUserIsTyping(ViewModel.SelectedChatNumber, typing);
                }
            }
        }

        private void CopyIDButton_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetDataObject(tox.GetAddress());
        }

        private void MetroWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!resizing && focusTextbox)
                TextToSend.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        }

        private void TextToSend_OnGotFocus(object sender, RoutedEventArgs e)
        {
            focusTextbox = true;
        }

        private void TextToSend_OnLostFocus(object sender, RoutedEventArgs e)
        {
            focusTextbox = false;
        }

        private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (resizing)
            {
                resizing = false;
                if (focusTextbox)
                {
                    TextToSend.Focus();
                    focusTextbox = false;
                }
            }
        }

        private void OnlineThumbButton_Click(object sender, EventArgs e)
        {
            SetStatus(ToxUserStatus.None, true);
        }

        private void AwayThumbButton_Click(object sender, EventArgs e)
        {
            SetStatus(ToxUserStatus.Away, true);
        }

        private void BusyThumbButton_Click(object sender, EventArgs e)
        {
            SetStatus(ToxUserStatus.Busy, true);
        }

        private void ListViewTabControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RequestsTabItem.IsSelected)
                RequestsTabItem.Header = "Requests";
        }

        private void StatusRectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            StatusContextMenu.PlacementTarget = this;
            StatusContextMenu.IsOpen = true;
        }

        private void MenuItem_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (!tox.IsConnected())
                return;

            MenuItem menuItem = (MenuItem)e.Source;
            SetStatus((ToxUserStatus)int.Parse(menuItem.Tag.ToString()), true);
        }

        private void TextToSend_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (Clipboard.ContainsImage())
                {
                    var bmp = (Bitmap) System.Windows.Forms.Clipboard.GetImage();
                    byte[] bytes = bmp.GetBytes();

                    if (!convdic.ContainsKey(ViewModel.SelectedChatNumber))
                        convdic.Add(ViewModel.SelectedChatNumber, GetNewFlowDocument());

                    int filenumber = tox.NewFileSender(ViewModel.SelectedChatNumber, (ulong)bytes.Length, "image.bmp");

                    if (filenumber == -1)
                        return;

                    FileTransfer transfer = convdic[ViewModel.SelectedChatNumber].AddNewFileTransfer(tox, ViewModel.SelectedChatNumber, filenumber, "image.bmp", (ulong)bytes.Length, false);
                    transfer.Stream = new MemoryStream(bytes);
                    transfer.Control.SetStatus(string.Format("Waiting for {0} to accept...", tox.GetName(ViewModel.SelectedChatNumber)));
                    transfer.Control.AcceptButton.Visibility = Visibility.Collapsed;
                    transfer.Control.DeclineButton.Visibility = Visibility.Visible;

                    transfer.Control.OnDecline += delegate(int friendnum, int filenum)
                    {
                        if (transfer.Thread != null)
                        {
                            transfer.Thread.Abort();
                            transfer.Thread.Join();
                        }

                        if (transfer.Stream != null)
                            transfer.Stream.Close();

                        if (!transfer.IsSender)
                            tox.FileSendControl(transfer.FriendNumber, 1, filenumber, ToxFileControl.Kill, new byte[0]);
                        else
                            tox.FileSendControl(transfer.FriendNumber, 0, filenumber, ToxFileControl.Kill, new byte[0]);
                    };

                    transfers.Add(transfer);

                    ScrollChatBox();
                }
            }
        }

        private void SetStatus(ToxUserStatus? newStatus, bool changeUserStatus)
        {
            if (newStatus == null)
            {
                newStatus = ToxUserStatus.Invalid;
            }
            else
            {
                if (changeUserStatus)
                    if (!tox.SetUserStatus(newStatus.GetValueOrDefault()))
                        return;
            }

            ViewModel.MainToxyUser.ToxStatus = newStatus.GetValueOrDefault();
        }

        private void CallButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsFriendSelected)
                return;

            if (call != null)
                return;

            var selectedChatNumber = ViewModel.SelectedChatNumber;
            if (!tox.IsOnline(selectedChatNumber))
                return;

            int call_index;
            ToxAvError error = toxav.Call(selectedChatNumber, ToxAv.DefaultCodecSettings, 30, out call_index);
            if (error != ToxAvError.None)
                return;

            int friendnumber = toxav.GetPeerID(call_index, 0);
            call = new ToxCall(toxav, call_index, friendnumber);

            CallButton.Visibility = Visibility.Collapsed;
            HangupButton.Visibility = Visibility.Visible;
            var callingFriend = ViewModel.GetFriendObjectByNumber(friendnumber);
            if (callingFriend != null)
            {
                ViewModel.CallingFriend = callingFriend;
                callingFriend.IsCallingToFriend = true;
            }
        }

        private void MainHangupButton_OnClick(object sender, RoutedEventArgs e)
        {
            EndCall();
        }

        private void FileButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsFriendSelected)
                return;

            var selectedChatNumber = ViewModel.SelectedChatNumber;
            if (!tox.IsOnline(selectedChatNumber))
                return;

            OpenFileDialog dialog = new OpenFileDialog();
            dialog.InitialDirectory = Environment.CurrentDirectory;
            dialog.Multiselect = false;

            if (dialog.ShowDialog() != true)
                return;

            string filename = dialog.FileName;

            SendFile(selectedChatNumber, filename);
        }

        private void SendFile(int chatNumber, string filename)
        {
            FileInfo info = new FileInfo(filename);
            int filenumber = tox.NewFileSender(chatNumber, (ulong)info.Length, filename.Split('\\').Last<string>());

            if (filenumber == -1)
                return;

            FileTransfer ft = convdic[chatNumber].AddNewFileTransfer(tox, chatNumber, filenumber, filename, (ulong)info.Length, true);
            ft.Control.SetStatus(string.Format("Waiting for {0} to accept...", tox.GetName(chatNumber)));
            ft.Control.AcceptButton.Visibility = Visibility.Collapsed;
            ft.Control.DeclineButton.Visibility = Visibility.Visible;

            ft.Control.OnDecline += delegate(int friendnum, int filenum)
            {
                if (ft.Thread != null)
                {
                    ft.Thread.Abort();
                    ft.Thread.Join();
                }

                if (ft.Stream != null)
                    ft.Stream.Close();

                if (!ft.IsSender)
                    tox.FileSendControl(ft.FriendNumber, 1, filenumber, ToxFileControl.Kill, new byte[0]);
                else
                    tox.FileSendControl(ft.FriendNumber, 0, filenumber, ToxFileControl.Kill, new byte[0]);

                ft.Finished = true;
            };

            transfers.Add(ft);

            ScrollChatBox();
        }

        private void ExecuteActionsOnNotifyIcon()
        {
            nIcon.Visible = config.HideInTray;
        }

        private void mv_Activated(object sender, EventArgs e)
        {
            nIcon.Icon = notifyIcon;
            ViewModel.HasNewMessage = false;
        }

        private void AccentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var theme = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
            var accent = ThemeManager.GetAccent(((AccentColorMenuData)AccentComboBox.SelectedItem).Name);
            ThemeManager.ChangeAppStyle(System.Windows.Application.Current, accent, theme.Item1);
        }

        private void SettingsFlyout_IsOpenChanged(object sender, EventArgs e)
        {
            if (!SettingsFlyout.IsOpen)
            {
                ThemeManager.ChangeAppStyle(System.Windows.Application.Current, oldAccent, oldAppTheme);
            }
        }

        private void AppThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var theme = ThemeManager.DetectAppStyle(System.Windows.Application.Current);
            var appTheme = ThemeManager.GetAppTheme(((AppThemeMenuData)AppThemeComboBox.SelectedItem).Name);
            ThemeManager.ChangeAppStyle(System.Windows.Application.Current, theme.Item2, appTheme);
        }

        private void ExportDataButton_OnClick(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "Export Tox data";
            dialog.InitialDirectory = Environment.CurrentDirectory;

            if (dialog.ShowDialog() != true)
                return;

            try { File.WriteAllBytes(dialog.FileName, tox.GetDataBytes()); }
            catch { this.ShowMessageAsync("Error", "Could not export data."); }
        }

        private void AvatarMenuItem_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            MenuItem menuItem = (MenuItem)e.Source;
            AvatarMenuItem item = (AvatarMenuItem)menuItem.Tag;

            switch (item)
            {
                case AvatarMenuItem.ChangeAvatar:
                    changeAvatar();
                    break;
                case AvatarMenuItem.RemoveAvatar:
                    removeAvatar();
                    break;
            }
        }

        private void changeAvatar()
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Image files (*.png) | *.png";
            dialog.InitialDirectory = Environment.CurrentDirectory;
            dialog.Multiselect = false;

            if (dialog.ShowDialog() != true)
                return;

            string filename = dialog.FileName;
            FileInfo info = new FileInfo(filename);
            byte[] avatarBytes = File.ReadAllBytes(filename);
            MemoryStream stream = new MemoryStream(avatarBytes);
            Bitmap bmp = new Bitmap(stream);

            if (info.Length > 0x4000)
            {
                //TODO: maintain aspect ratio
                Bitmap newBmp = new Bitmap(64, 64);
                using (Graphics g = Graphics.FromImage(newBmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, 64, 64);
                }

                bmp.Dispose();
                stream.Dispose();

                bmp = newBmp;
                avatarBytes = avatarBitmapToBytes(bmp);

                if (avatarBytes.Length > 0x4000)
                {
                    this.ShowMessageAsync("Error", "This image is bigger than 16 KB and Toxy could not resize the image.");
                    return;
                }
            }

            ViewModel.MainToxyUser.Avatar = BitmapToImageSource(bmp, ImageFormat.Png);
            bmp.Dispose();

            if (tox.SetAvatar(ToxAvatarFormat.Png, avatarBytes))
            {
                if (!config.Portable)
                    File.WriteAllBytes(Path.Combine(toxDataDir, "avatar.png"), avatarBytes);
                else
                    File.WriteAllBytes("avatar.png", avatarBytes);
            }

            //let's announce our new avatar
            foreach(int friend in tox.GetFriendlist())
            {
                if (!tox.IsOnline(friend))
                    continue;

                tox.SendAvatarInfo(friend);
            }
        }
        
        private byte[] avatarBitmapToBytes(Bitmap bmp)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bmp.Save(stream, ImageFormat.Png);
                stream.Close();

                return stream.ToArray();
            }
        }

        private BitmapImage BitmapToImageSource(Bitmap bmp, ImageFormat format)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bmp.Save(stream, format);

                stream.Position = 0;

                BitmapImage newBmp = new BitmapImage();
                newBmp.BeginInit();
                newBmp.StreamSource = stream;
                newBmp.CacheOption = BitmapCacheOption.OnLoad;
                newBmp.EndInit();

                return newBmp;
            }
        }

        private void removeAvatar()
        {
            if (tox.RemoveAvatar())
            {
                ViewModel.MainToxyUser.Avatar = new BitmapImage(new Uri("pack://application:,,,/Resources/Icons/profilepicture.png"));

                if (!config.Portable)
                {
                    string path = Path.Combine(toxDataDir, "avatar.png");

                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                {
                    if (File.Exists("avatar.png"))
                        File.Delete("avatar.png");
                }
            }
        }
        
        private void AvatarImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AvatarContextMenu.PlacementTarget = this;
            AvatarContextMenu.IsOpen = true;
        }

        private void MessageAlertIncrement(IChatObject chat)
        {
            chat.HasNewMessage = true;
            chat.NewMessageCount++;
            if(config.EnableAudioNotifications && call==null)
            {
                Win32.Winmm.PlayMessageNotify();
            }
        }

        private void MessageAlertClear(IChatObject chat)
        {
            chat.HasNewMessage = false;
            chat.NewMessageCount = 0;
        }

    }
}
