﻿using Newtonsoft.Json;
using ReserveBlockCore.Beacon;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Nodes
{
    public class BeaconProcessor
    {
        public static async Task ProcessData(string message, string data)
        {
            if (message == null || message == "")
            {
                return;
            }
            if (Globals.StopAllTimers == false && Globals.BlocksDownloading != 1) //this will prevent new blocks from coming in if flag. Normally only flagged when syncing chain.
            {
                if (message == "send")
                {
                    try
                    {
                        var payload = JsonConvert.DeserializeObject<string[]>(data);
                        if (payload != null)
                        {
                            var scUID = payload[0];
                            var assetName = payload[1];

                            var filePath = NFTAssetFileUtility.NFTAssetPath(assetName, scUID);
                            var beaconString = Globals.Locators.FirstOrDefault().ToStringFromBase64();
                            var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);

                            BeaconResponse rsp = BeaconClient.Send(filePath, beacon.IPAddress, beacon.Port);
                            if (rsp.Status == 1)
                            {
                                //success
                                var aqDb = AssetQueue.GetAssetQueue();
                                var aq = aqDb.FindOne(x => x.SmartContractUID == scUID);
                                if(aq != null)
                                {
                                    aq.IsComplete = true;
                                    aq.IsDownloaded = true;

                                    aqDb.UpdateSafe(aq);
                                }

                                NFTLogUtility.Log($"Success sending asset: {assetName}. Description: {rsp.Description}", "BeaconProcessor.ProcessData()");
                                await P2PClient.BeaconFileIsReady(scUID, assetName);
                            }
                            else
                            {
                                NFTLogUtility.Log($"NFT Send for assets -> {assetName} <- failed.", "BeaconProcessor.ProcessData()");
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        NFTLogUtility.Log($"NFT Send for assets failed. Unknown Error {ex.Message}. Data: {data}", "BeaconProcessor.ProcessData()");
                    }

                }

                if (message == "receive")
                {
                    try
                    {
                        var payload = JsonConvert.DeserializeObject<string[]>(data);
                        if (payload != null)
                        {
                            var scUID = payload[0];
                            var assetName = payload[1];

                            var beaconString = Globals.Locators.FirstOrDefault().ToStringFromBase64();
                            var beacon = JsonConvert.DeserializeObject<BeaconInfo.BeaconInfoJson>(beaconString);

                            if(beacon != null)
                            {
                                BeaconResponse rsp = BeaconClient.Receive(assetName, beacon.IPAddress, beacon.Port, scUID);
                                if (rsp.Status == 1)
                                {
                                    //success
                                }
                                else
                                {
                                    //failed
                                }
                            }
                            
                        }
                    }
                    catch(Exception ex)
                    {

                    }
                }

            }
        }
    }
}
