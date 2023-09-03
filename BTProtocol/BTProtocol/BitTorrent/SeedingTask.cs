﻿using System;
using System.Net.Sockets;

using static BTProtocol.BitTorrent.MessageType;

namespace BTProtocol.BitTorrent
{
    sealed internal class SeedingTask : TorrentTask
    {
        protected override private void ExitThread()
        {
            SeedingThreadManager.thread_pool.Release();
            Console.WriteLine("Exiting Task");
        }

        public SeedingTask(Peer peer)
        {
            this.peer = peer;
            torrent_data = null;
            file_manager = null;
        }

        public void StartTask()
        {
            // Call Wait to decrement the count of available threads.
            SeedingThreadManager.thread_pool.Wait();
            // Release the lock on main so it can continue execution.
            SeedingThreadManager.main_semaphore.Release();
            NetworkStream peer_netstream = peer.GetStream();

            try
            { 
                string torrent_name = ReceiveHandshakeSeeding();
                torrent_data = MainProc.torrent_file_dict[torrent_name];
                file_manager = MainProc.file_dict[torrent_name];
                SendHandshake();
                SendBitField();

                ReceivePackets();
            } 
            catch(Exception ex) 
            {
                Console.WriteLine(ex.Message);
            }
            Console.WriteLine("I got here");
            ExitThread();
        }

        private void ReceivePackets()
        {
            /*
             * Continuously read in new packets from the peer.
             * The proess of reading in packets is as follows:
             *      - Read in 4 bytes to determine the size of the packets. If the size is 0, it is a keep-alive packet
             *      - Read in the full packet to a byte array with size as specified 
             *      - Get the message type of the packet (First 4 bytes)
             *      - Use a case statement to proess the full packet
             *      - Continue readng packets from the peer until the peer closes the connection
            */
            NetworkStream netstream = peer.GetStream();
            while (true)
            {
                byte[] byte_buffer = new byte[4];
                int packet_size;
                netstream.Read(byte_buffer, 0, 4);
                packet_size = Utils.ParseInt(byte_buffer);

                if (packet_size > 0)
                {
                    //Console.WriteLine("Packet Size: " + packet_size);
                    byte_buffer = new byte[packet_size];
                    int bytes_read = 0;
                    while (bytes_read < packet_size)
                    {
                        bytes_read += netstream.Read(byte_buffer, bytes_read, packet_size - bytes_read);
                    }
                    //Console.WriteLine("Bytes Read: " + bytes_read);

                    MessageType packet_type = (MessageType)byte_buffer[0];
                    Console.WriteLine($"IP: {peer.ip} Packet Type: {packet_type}");
                    switch (packet_type)
                    {
                        case Interested:
                            peer.interested = true;
                            SendBitMessageType(Unchoke);
                            break;
                        case NotInterested:
                            peer.interested = false;
                            break;
                        case Request:
                            ReceiveRequest(byte_buffer);
                            break;
                        case Cancel:
                            // Not handled by current seeding implementation
                            break;
                    }
                }
            }
        }
    }
}
