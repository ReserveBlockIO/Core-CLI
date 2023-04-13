﻿using ReserveBlockCore.Models.DST;
using ReserveBlockCore.Utilities;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.DST
{
    public class KeepAliveService
    {
        public static async Task KeepAlive(int seconds, IPEndPoint peerEndPoint, UdpClient udpClient, bool isShop = false, bool isStun = false)
        {
            bool stop = false;
            while (true && !stop)
            {
                try
                {
                    if (isShop)
                    {
                        if (Globals.ConnectedShops.TryGetValue(peerEndPoint.ToString(), out var shop))
                        {
                            if (shop != null)
                            {
                                var delay = Task.Delay(new TimeSpan(0, 0, seconds));
                                var payload = new Message { Type = MessageType.STUNKeepAlive, Data = "" };
                                var message = MessageService.GenerateMessage(payload, false);
                                var messageDataBytes = Encoding.UTF8.GetBytes(message);
                                udpClient.Send(messageDataBytes, peerEndPoint);

                                shop.LastSentMessage = TimeUtil.GetTime();

                                Globals.ConnectedShops[peerEndPoint.ToString()] = shop;

                                var currentTime = TimeUtil.GetTime();
                                if (currentTime - shop.LastReceiveMessage > 15)
                                {
                                    stop = true;
                                    Globals.ConnectedShops.TryRemove(peerEndPoint.ToString(), out _);

                                }

                                await delay;
                            }
                        }
                        else
                        {
                            stop = true;
                        }
                    }
                    else if (isStun)
                    {
                        if (Globals.STUNServer != null)
                        {
                            var delay = Task.Delay(new TimeSpan(0, 0, seconds));

                            var payload = new Message { Type = MessageType.ShopKeepAlive, Data = "" };
                            var message = MessageService.GenerateMessage(payload, false);
                            var messageDataBytes = Encoding.UTF8.GetBytes(message);
                            udpClient.Send(messageDataBytes, peerEndPoint);

                            Globals.STUNServer.LastSentMessage = TimeUtil.GetTime();

                            var currentTime = TimeUtil.GetTime();
                            if (currentTime - Globals.STUNServer.LastReceiveMessage > 15)
                            {
                                stop = true;
                                _ = DSTClient.DisconnectFromSTUNServer(); //disconnect from STUN Server
                                await Task.Delay(1000);
                                Globals.STUNServer = null;
                                _ = DSTClient.Run(); //attempt to reconnect to a STUN server.
                                await Task.Delay(1000);
                            }

                            await delay;
                        }
                        else
                        {
                            stop = true;
                        }
                    }
                    else
                    {
                        if (Globals.ConnectedClients.TryGetValue(peerEndPoint.ToString(), out var client))
                        {
                            if (client != null)
                            {
                                var delay = Task.Delay(new TimeSpan(0, 0, seconds));
                                var payload = new Message { Type = MessageType.KeepAlive, Data = "" };
                                var message = MessageService.GenerateMessage(payload, false);
                                var messageDataBytes = Encoding.UTF8.GetBytes(message);
                                udpClient.Send(messageDataBytes, peerEndPoint);

                                client.LastSentMessage = TimeUtil.GetTime();

                                Globals.ConnectedClients[peerEndPoint.ToString()] = client;

                                var currentTime = TimeUtil.GetTime();
                                if (currentTime - client.LastReceiveMessage > 28)
                                {
                                    //stop = true;
                                    //Globals.ConnectedClients.TryRemove(peerEndPoint.ToString(), out _);
                                    stop = true;
                                    var shopServer = client.IPAddress; //this also has port
                                    var shopEndPoint = IPEndPoint.Parse(shopServer);
                                    Globals.ConnectedClients.TryRemove(peerEndPoint.ToString(), out _);
                                    _ = DSTClient.DisconnectFromShop(true);
                                    await Task.Delay(1000);
                                    _ = DSTClient.ConnectToShop(shopEndPoint, shopServer);
                                    await Task.Delay(1000);
                                }

                                await delay;
                            }
                        }
                        else
                        {
                            stop = true;
                        }
                    }
                }
                catch { stop = true; }
            }
        }

    }
}
