﻿using System;

using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System.IO;

using BencodeNET.Objects;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using System.Collections.Generic;

namespace BTProtocol.BitTorrent
{
    class TrackerManager
    {
        private Torrent torrent_file { get; }
        private TFData torrent_data { get; }
        private BDictionary dictionary { get; }
        private List<string> announce_urls;
        private Int64 UDP_MAGIC_CONSTANT = 0x41727101980;
        private DateTime last_contacted;

        private string EventToString(Events _event)
        {
            string ret = "";

            switch (_event)
            {
                case Events.started:
                    ret = "started";
                    break;
                case Events.paused:
                    ret = "paused";
                    break;
                case Events.stopped:
                    ret = "stopped";
                    break;
                default:
                    ret = "paused";
                    break;
            }

            return ret;
        }

        public TrackerManager(Torrent torrent_file, TFData torrent_data)
        {
            this.torrent_file = torrent_file;
            this.torrent_data = torrent_data;
            announce_urls = new List<string>();
            dictionary = torrent_file.ToBDictionary();
            Initialize();
        }

        public void Initialize()
        {
            BList announce_list = (BList)dictionary["announce-list"];
            foreach (BList tracker_url in announce_list)
            {
                announce_urls.Add(tracker_url[0].ToString());
                UpdateTracker(tracker_url[0].ToString());
            }
        }

        private (byte[], int) SendUdpTrackerRequest(string announce_url)
        {
            // Announce url format: udp://annouce_url:port
            string[] tokens = announce_url.Split(':');
            announce_url = tokens[1].Substring(2);
            int port = int.Parse(tokens[2].Split('/')[0]);
            Random random = new Random();
            //Console.WriteLine("Tracker ip: " + Dns.GetHostEntry(announce_url).AddressList[0]);
            IPEndPoint tracker_ip = new IPEndPoint(Dns.GetHostEntry(announce_url).AddressList[0], port);
            UdpClient udp_client = new UdpClient(Dns.GetHostEntry(announce_url).AddressList[0].ToString(), port);

            int transaction_id = random.Next();
            MemoryStream connection_request = new MemoryStream();
            connection_request.Write(Utils.Int64ToByteArray(UDP_MAGIC_CONSTANT), 0, 8);
            connection_request.Write(Utils.Int32ToByteArray(0), 0, 4); //Message Type
            connection_request.Write(Utils.Int32ToByteArray(transaction_id), 0, 4);
                
            byte[] received_bytes = SendUDPPacket(udp_client, tracker_ip, connection_request.ToArray());

            // Check if we recieved a valid message by matching the message type and transaction_id
            if (received_bytes.Length >= 16 && Utils.ParseInt(received_bytes, 0) == 0 && transaction_id == Utils.ParseInt(received_bytes, 4))
            {
                Int64 connection_id = Utils.ParseInt64(received_bytes, 8);

                /*
                    Offset  Size    Name    Value
                    0       64-bit integer  connection_id
                    8       32-bit integer  action          1 // announce
                    12      32-bit integer  transaction_id
                    16      20-byte string  info_hash
                    36      20-byte string  peer_id
                    56      64-bit integer  downloaded
                    64      64-bit integer  left
                    72      64-bit integer  uploaded
                    80      32-bit integer  event           0 // 0: none; 1: completed; 2: started; 3: stopped
                    84      32-bit integer  IP address      0 // default
                    88      32-bit integer  key
                    92      32-bit integer  num_want        -1 // default
                    96      16-bit integer  port
                */

                MemoryStream announce_request = new MemoryStream();
                announce_request.Write(Utils.Int64ToByteArray(connection_id), 0, 8);
                announce_request.Write(Utils.Int32ToByteArray(1), 0, 4);
                announce_request.Write(Utils.Int32ToByteArray(transaction_id), 0, 4);
                announce_request.Write(torrent_file.GetInfoHashBytes(), 0, 20);
                announce_request.Write(Encoding.UTF8.GetBytes(MainProc.peerid), 0, 20);
                announce_request.Write(Utils.Int64ToByteArray(torrent_data.bytes_downloaded), 0, 8);
                announce_request.Write(Utils.Int64ToByteArray(torrent_data.torrent_size - torrent_data.bytes_downloaded), 0, 8);
                announce_request.Write(Utils.Int64ToByteArray(torrent_data.bytes_uploaded), 0, 8);
                announce_request.Write(Utils.Int32ToByteArray((int) torrent_data._event), 0, 4);
                announce_request.Write(Utils.Int32ToByteArray(0), 0, 4);
                announce_request.Write(Utils.Int32ToByteArray(0), 0, 4);
                announce_request.Write(Utils.Int32ToByteArray(1000), 0, 4);
                announce_request.Write(Utils.Int16ToByteArray(6889), 0, 2);

                received_bytes = SendUDPPacket(udp_client, tracker_ip, announce_request.ToArray());
                if (received_bytes.Length >= 16 && Utils.ParseInt(received_bytes, 0) == 1 && transaction_id == Utils.ParseInt(received_bytes, 4))
                {
                    int interval = Utils.ParseInt(received_bytes, 8);
                    return (Utils.SubArray(received_bytes, 20, received_bytes.Length - 20), interval);
                }
            }

            throw new Exception("Failed to contact tracker");
        }

        private void FlushUdpSocket(UdpClient udp_client, IPEndPoint tracker_ip)
        {
            byte[] received_bytes;
            try
            {
                received_bytes = udp_client.Receive(ref tracker_ip);
                while (received_bytes.Length > 0)
                {
                    udp_client.Receive(ref tracker_ip);
                }
            }
            catch (SocketException) { }
        }

        private byte[] SendUDPPacket(UdpClient udp_client, IPEndPoint tracker_ip, byte[] message)
        {
            try
            {
                udp_client.Send(message, (int)message.Length);
                udp_client.Client.ReceiveTimeout = 5000;
                return udp_client.Receive(ref tracker_ip);
            }
            catch (SocketException)
            {
                throw new Exception("Tracker did not respond");
            }
        }

        private byte[] SendHttpTrackerRequest(string announce_url)
        {
            byte[] data;
            StringBuilder sb = new StringBuilder();
            sb.Append(announce_url);
            sb.Append("?info_hash=").Append(Utils.UrlSafeStringInfohash(torrent_file.GetInfoHashBytes()));
            sb.Append("&peer_id=").Append(MainProc.peerid);
            sb.Append("&port=").Append(6889);
            sb.Append("&uploaded=").Append(torrent_data.bytes_uploaded);
            sb.Append("&downloaded=").Append(torrent_data.bytes_downloaded);
            sb.Append("&left=").Append(torrent_data.torrent_size - torrent_data.bytes_downloaded);
            sb.Append("&event=").Append(EventToString(torrent_data._event));
            sb.Append("&compact=").Append(torrent_data.compact);
            sb.Append("&numwant=").Append(1000);

            HttpWebRequest HttpWReq = (HttpWebRequest)WebRequest.Create(sb.ToString());
            HttpWebResponse HttpWResp = (HttpWebResponse)HttpWReq.GetResponse();

            using (Stream stream = HttpWResp.GetResponseStream())
            {
                data = new byte[HttpWResp.ContentLength];
                stream.Read(data, 0, Convert.ToInt32(HttpWResp.ContentLength));
            }

            return data;
        }

        private int SendRecvToTracker(string announce_url)
        {
            // Console.WriteLine(sb.ToString());
            byte[] data;
            byte[] buffer;
            int interval; 
            if (announce_url.Substring(0,3) == "udp")
            {
                (buffer, interval) = SendUdpTrackerRequest(announce_url);
            }
            else
            {
                data = SendHttpTrackerRequest(announce_url);
                BDictionary tracker_dict = Utils.parser.Parse<BDictionary>(data);
                BString peers = tracker_dict.Get<BString>("peers");
                buffer = peers.Value.ToArray();
                interval = int.Parse(tracker_dict["interval"].ToString());
            }

            List<(string, int)> tracker_peers = new List<(string, int)>();
            for (int i = 0; i < buffer.Length; i += 6)
            {
                string ip =((int)buffer[i] + "." + (int)buffer[i + 1] + "." + (int)buffer[i + 2] + "." + (int)buffer[i + 3]);
                int port = buffer[i + 4] << 8;
                port += buffer[i + 5];
                if (!torrent_data.peer_list.Contains((ip, port)) && !ip.Equals(Utils.PUBLIC_IP_ADDRESS))
                {
                    torrent_data.peer_list.Add((ip, port));
                }
                tracker_peers.Add((ip, port));
            }

            for (int i = 0; i < torrent_data.peer_list.Count; i++)
            {
                string ip = torrent_data.peer_list[i].Item1;
                int port = torrent_data.peer_list[i].Item2;

                if (!tracker_peers.Contains((ip, port))){
                    torrent_data.peer_list.Remove((ip, port));
                    if (i < torrent_data.peer_list_indx)
                    {
                        Interlocked.Decrement(ref this.torrent_data.peer_list_indx);
                    }
                    i -= 1;
                }
            }
              
            return interval;
        }

        private void UpdateTracker(Object obj)
        {
            string url = (string) obj;
            Console.WriteLine("Contacting Tracker: " + url);
            try
            {
                int wait_time = SendRecvToTracker(url);
                var state_timer = new Timer(UpdateTracker, url, wait_time * 1000, -1);
            }catch (Exception e) 
            { 
                Console.WriteLine("Lost a tracker: " + url); 
                Console.WriteLine(e.Message);
            }

            last_contacted = DateTime.Now;
        }

        public bool ContactTracker()
        {
            if (DateTime.Now.Subtract(last_contacted).Seconds < 300)
            {
                return false;
            }

            Console.WriteLine("Attempting to recontact trackers");
            last_contacted = DateTime.Now;
            foreach (string url in announce_urls)
            {
                Console.WriteLine("Contacting Tracker: " + url);
                SendRecvToTracker(url);
            }
            return true;
        }
    }
}
