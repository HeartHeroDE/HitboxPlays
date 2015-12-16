using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.IO;
using System.Windows.Forms;
using System.Threading;
using WebSocket4Net;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using WindowsInput;

namespace HitboxPlays
{

    public partial class HtbxPlays : Form
    {
        // Enter the credentials of a valid hitbox-user
        string user_name = "WulfBot";
        string user_pass = "1e433c57975715609c1a2866d9c1b0ff";

        WebSocket websocket;
        KeyCommand lastcommand = new KeyCommand();
        string Token;
        bool connected = false;

        public HtbxPlays()
        {
            InitializeComponent();

            channelBox.Text = Properties.Settings.Default.channel;
            processBox.Text = Properties.Settings.Default.process;

            ImportDialog.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
            ExportDialog.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;

            if (!String.IsNullOrEmpty(Properties.Settings.Default.commands)) {

                try
                {
                    KeyList importKeys = JsonConvert.DeserializeObject<KeyList>(File.ReadAllText(ImportDialog.FileName));
                    ImportList(importKeys);
                }
                catch
                {
                    Debug.WriteLine("Import of last used key-list failed");
                }

            }
        }


        // This is important to make virtual key inputs for other processes possible
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        private void websocket_Opened(object sender, EventArgs e)
        {
            Debug.WriteLine("Connected");
            connected = true;
        }

        private void websocket_Closed(object sender, EventArgs e)
        {
            Debug.WriteLine("Diconnected");
            connected = false;
        }

        private void websocket_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            Debug.WriteLine(e.Message);

            // Message 1:: means we have to (re)connect
            if (e.Message == "1::")
            {

                WebRequest request = WebRequest.Create("https://www.hitbox.tv/api/auth/token");

                request.ContentType = "application/json";
                request.Method = "POST";
                byte[] buffer = Encoding.GetEncoding("UTF-8").GetBytes("{\"login\":\"" + user_name + "\",\"pass\":\"" + user_pass + "\",\"app\":\"desktop\"}");
                string result = System.Convert.ToBase64String(buffer);
                Stream reqstr = request.GetRequestStream();
                reqstr.Write(buffer, 0, buffer.Length);
                reqstr.Close();

                WebResponse response = request.GetResponse();
                Token = response.ToString();

                websocket.Send("5:::{\"name\":\"message\",\"args\":[{\"method\":\"joinChannel\",\"params\":{\"channel\":\"" + channelBox.Text + "\",\"name\":\"" + user_name + "\",\"token\":\"" + Token + "\",\"isAdmin\":false}}]}");
            }

            // Ping server when necessary
            if (e.Message == "2::")
            {
                websocket.Send("2::{}");
                websocket.Send("5:::{\"name\":\"message\",\"args\":[{\"method\":\"getChannelUserList\",\"params\":{\"channel\":\"" + channelBox.Text + "\"}}]}");
            }

            // Handling important messages
            if (e.Message.StartsWith("5::")) {

                // getting the method
                Regex rgx = new Regex(@"\\\""method\\\"":\\\""(\w*)\\\""");
                Process game1 = new Process();

                // Try to find the game process and prepare it to work with input
                try
                {
                    Process[] processes = Process.GetProcessesByName(processBox.Text);
                    game1 = processes[0];

                    IntPtr p = game1.MainWindowHandle;
                    SetForegroundWindow(p);

                }
                catch
                {
                    MessageBox.Show("Process not available!");
                    button1.Invoke(new Action(() => button1.Text = "Connect"));
                    channelBox.Invoke(new Action(() => channelBox.Enabled = true));
                    processBox.Invoke(new Action(() => processBox.Enabled = true));

                    websocket.Close();
                }

                // We get messages from the chat to retrieve key inputs
                if (rgx.Match(e.Message).Groups[1].Value == "chatMsg") {
                
                /*<< Getting information from the chatMsg */

                rgx = new Regex(@"\\\""");
                string corrected_request = rgx.Replace(e.Message, @"""");

                rgx = new Regex(@"\[""\{");
                corrected_request = rgx.Replace(corrected_request, @"[{");

                rgx = new Regex(@"}""]");
                corrected_request = rgx.Replace(corrected_request, @"}]");

                rgx = new Regex(@"5:::");
                corrected_request = rgx.Replace(corrected_request, @"");

                rgx = new Regex(@"\\\\""");
                corrected_request = rgx.Replace(corrected_request, @"'");

                /* Got ChatMsg details  >>*/

                Debug.WriteLine(corrected_request);

                // Prepare the JSON-Data to be properly useable
                var message = JsonConvert.DeserializeObject<ChatMSG>(corrected_request);
                string input = message.args[0].@params.text;

                    // We check if the input can be found in the commandlist
                    foreach (ListViewItem item in CommandList.Items)
                    {
                        // Handle a match
                        if (input.ToLower() == item.Text.ToLower())
                        {
                            // The key will be sent to the process
                            lastcommand.command = item.Text;
                            lastcommand.key1 = item.SubItems[1].Text;
                            lastcommand.key2 = item.SubItems[2].Text;
                            lastcommand.time = Convert.ToInt32(item.SubItems[3].Text);
                            KeyCommand TransmitKey = new KeyCommand();
                            TransmitKey = lastcommand;
                            StartThread(TransmitKey);

                            // We log the last input
                            this.BeginInvoke((Action)(() => {
                            ListViewItem log = new ListViewItem();
                            log.Text = input;
                            log.SubItems.Add(message.args[0].@params.name);
                            log.SubItems.Add(DateTime.Now.ToString());
                            listView1.Items.Add(log);
                            }));
                        }
                    }
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (connected == false)
            {
                button1.Text = "Disconnect";
                channelBox.Enabled = false;
                processBox.Enabled = false;
                button1.Enabled = false;

                using (WebClient client = new WebClient())
                {
                    // We try to get a list of valid IP's from Hitbox
                retry:
                    var server = new List<HtbxServers>();
                    string servers = client.DownloadString("http://api.hitbox.tv/chat/servers?redis=true");
                    try
                    {
                        server = JsonConvert.DeserializeObject<List<HtbxServers>>(servers);
                    }
                    catch
                    {
                        goto retry;
                    }

                    if (String.IsNullOrEmpty(server[0].server_ip)) { goto retry; }

                    // We connect to the first available IP
                    string current_server = server[0].server_ip + "/socket.io/1/";
                    string socket_id = client.DownloadString("http://" + current_server).Split(':')[0];

                    websocket = new WebSocket("ws://" + current_server + "websocket/" + socket_id);
                    websocket.EnableAutoSendPing = false;

                }

                // We now connect via WebSocket
                websocket.Opened += new EventHandler(websocket_Opened);
                websocket.Closed += new EventHandler(websocket_Closed);
                websocket.MessageReceived += new EventHandler<WebSocket4Net.MessageReceivedEventArgs>(websocket_MessageReceived);
                websocket.Open();
                button1.Enabled = true;
            }
            else
            {
                button1.Text = "Connect";
                channelBox.Enabled = true;
                processBox.Enabled = true;
                websocket.Close();
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            // Adding a command to the current list
            ListViewItem key = new ListViewItem();
            key.Text = CommandBox.Text;
            key.SubItems.Add(Key1Box.Text);
            key.SubItems.Add(Key2Box.Text);
            key.SubItems.Add(TimeBox.Text);

            CommandList.Items.Add(key);
        }

        private void exportListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Exporting a KeyList
            List<KeyCommand> Exportlist = new List<KeyCommand>();

            foreach (ListViewItem item in CommandList.Items)
            {
                KeyCommand exportkey = new KeyCommand();
                exportkey.command = item.Text;
                exportkey.key1 = item.SubItems[1].Text;
                exportkey.key2 = item.SubItems[2].Text;
                exportkey.time = Convert.ToInt32(item.SubItems[3].Text);
                Exportlist.Add(exportkey);
            }

            KeyList JsonExport = new KeyList();
            JsonExport.KeyCommands = Exportlist;

            if (ExportDialog.ShowDialog() == DialogResult.OK)
                File.WriteAllText(ExportDialog.FileName, JsonConvert.SerializeObject(JsonExport));

        }

        private void importListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Importing a KeyList
            if (ImportDialog.ShowDialog() == DialogResult.OK)
            {
                KeyList importKeys = JsonConvert.DeserializeObject<KeyList>(File.ReadAllText(ImportDialog.FileName));
                ImportList(importKeys);

            }
        }

        public void ImportList(KeyList importKeys)
        {
            CommandList.Items.Clear();

            foreach (KeyCommand key in importKeys.KeyCommands)
            {
                ListViewItem newkey = new ListViewItem();
                newkey.Text = key.command;
                newkey.SubItems.Add(key.key1);
                newkey.SubItems.Add(key.key2);
                newkey.SubItems.Add(key.time.ToString());

                CommandList.Items.Add(newkey);
            }
        }

        public void TranslateKey(string key, int status = 0)
        {

            if (status == 0)
            {

                switch (key)
                {
                    case "1": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_1); break;
                    case "2": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_2); break;
                    case "3": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_3); break;
                    case "4": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_4); break;
                    case "5": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_5); break;
                    case "6": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_6); break;
                    case "7": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_7); break;
                    case "8": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_8); break;
                    case "9": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_9); break;
                    case "0": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_0); break;
                    case "Q": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_Q); break;
                    case "W": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_W); break;
                    case "E": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_E); break;
                    case "R": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_R); break;
                    case "T": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_T); break;
                    case "Z": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_Z); break;
                    case "U": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_U); break;
                    case "I": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_I); break;
                    case "O": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_O); break;
                    case "P": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_P); break;
                    case "A": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_A); break;
                    case "S": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_S); break;
                    case "D": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_D); break;
                    case "F": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_F); break;
                    case "G": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_G); break;
                    case "H": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_H); break;
                    case "J": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_J); break;
                    case "K": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_K); break;
                    case "L": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_L); break;
                    case "Y": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_Y); break;
                    case "X": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_X); break;
                    case "C": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_C); break;
                    case "V": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_V); break;
                    case "B": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_B); break;
                    case "N": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_N); break;
                    case "M": InputSimulator.SimulateKeyDown(VirtualKeyCode.VK_M); break;
                    case "Space": InputSimulator.SimulateKeyDown(VirtualKeyCode.SPACE); break;
                    case "Return": InputSimulator.SimulateKeyDown(VirtualKeyCode.RETURN); break;
                }

            }
            else
            {
                switch (key)
                {
                    case "1": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_1); break;
                    case "2": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_2); break;
                    case "3": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_3); break;
                    case "4": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_4); break;
                    case "5": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_5); break;
                    case "6": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_6); break;
                    case "7": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_7); break;
                    case "8": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_8); break;
                    case "9": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_9); break;
                    case "0": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_0); break;
                    case "Q": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_Q); break;
                    case "W": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_W); break;
                    case "E": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_E); break;
                    case "R": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_R); break;
                    case "T": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_T); break;
                    case "Z": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_Z); break;
                    case "U": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_U); break;
                    case "I": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_I); break;
                    case "O": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_O); break;
                    case "P": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_P); break;
                    case "A": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_A); break;
                    case "S": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_S); break;
                    case "D": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_D); break;
                    case "F": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_F); break;
                    case "G": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_G); break;
                    case "H": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_H); break;
                    case "J": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_J); break;
                    case "K": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_K); break;
                    case "L": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_L); break;
                    case "Y": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_Y); break;
                    case "X": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_X); break;
                    case "C": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_C); break;
                    case "V": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_V); break;
                    case "B": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_B); break;
                    case "N": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_N); break;
                    case "M": InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_M); break;
                    case "Space": InputSimulator.SimulateKeyUp(VirtualKeyCode.SPACE); break;
                    case "Return": InputSimulator.SimulateKeyUp(VirtualKeyCode.RETURN); break;
                }
            }

        }

        public void ClearKeys()
        {
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_1);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_2);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_3);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_4);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_5);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_6);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_7);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_8);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_9);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_0);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_Q);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_W);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_E);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_R);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_T);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_Z);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_U);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_I);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_O);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_P);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_A);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_S);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_D);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_F);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_G);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_H);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_J);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_K);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_L);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_Y);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_X);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_C);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_V);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_B);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_N);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.VK_M);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.SPACE);
            InputSimulator.SimulateKeyUp(VirtualKeyCode.RETURN);
        }

        public void StartThread(KeyCommand TransmitKey)
        {
            Thread thread = new Thread(delegate() { InputThread(TransmitKey); });
            thread.Start();
        }

        private void InputThread(KeyCommand TransmitKey)
        {
            // New Input always clears the last input
            ClearKeys();
            // KeyDown Events
            TranslateKey(TransmitKey.key1, 0);
            if (!String.IsNullOrEmpty(TransmitKey.key2))
                TranslateKey(TransmitKey.key2, 0);

            // Wait x milliseconds to simulate accurate timing
            Thread.Sleep(TransmitKey.time);

            //KeyUp Events
            TranslateKey(TransmitKey.key1, 1);
            if (!String.IsNullOrEmpty(TransmitKey.key2))
                TranslateKey(TransmitKey.key2, 1);

        }

        private void HtbxPlays_FormClosing(object sender, FormClosingEventArgs e)
        {
            // We save the last settings
            Properties.Settings.Default.channel = channelBox.Text;
            Properties.Settings.Default.process = processBox.Text;

            if (!String.IsNullOrEmpty(ImportDialog.FileName))
                Properties.Settings.Default.commands = ImportDialog.FileName;

            Properties.Settings.Default.Save();

        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in CommandList.SelectedItems)
            {
                item.Remove();
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void toolStripMenuItem2_Click(object sender, EventArgs e)
        {
            MessageBox.Show(@"""Hitbox Plays!"" is being developed by Chrisblue. (http://chrisblue.org)","About",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }


    }

    // HITBOX-API Classes
    // This is a reconstruction of the JSON-Data classes used for Hitbox-API

    public class HtbxServers
    {
        public string server_ip { get; set; }
    }

    public class HtbxAuthToken
    {
        public string authToken { get; set; }
    }

    public class Params
    {
        public string channel { get; set; }
        public string name { get; set; }
        public string nameColor { get; set; }
        public string text { get; set; }
        public int time { get; set; }
        public string role { get; set; }
        public bool isFollower { get; set; }
        public bool isSubscriber { get; set; }
        public bool isOwner { get; set; }
        public bool isStaff { get; set; }
        public bool isCommunity { get; set; }
        public bool media { get; set; }
        public string image { get; set; }
        public bool buffer { get; set; }
        public bool buffersent { get; set; }
    }

    public class Arg
    {
        public string method { get; set; }
        public Params @params { get; set; }
    }

    public class ChatMSG
    {
        public string name { get; set; }
        public List<Arg> args { get; set; }
    }

    // KEY-LIST Classes

    public class KeyList
    {
        public List<KeyCommand> KeyCommands { get; set; }
    }

    public class KeyCommand
    {
        public string command { get; set; }
        public string key1 { get; set; }
        public string key2 { get; set; }
        public int time { get; set; }
    }

}