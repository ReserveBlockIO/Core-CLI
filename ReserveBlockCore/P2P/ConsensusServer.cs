﻿using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Transactions;
using System.Xml.Linq;

namespace ReserveBlockCore.P2P
{
    public class ConsensusServer : P2PServer
    {
        static ConsensusServer()
        {
            AdjPool = new ConcurrentDictionary<string, AdjPool>();
            Messages = new ConcurrentDictionary<(long Height, int MethodCode), ConcurrentDictionary<string, (string Message, string Signature)>>();
            Hashes = new ConcurrentDictionary<(long Height, int MethodCode), ConcurrentDictionary<string, (string Hash, string Signature)>>();
            ConsenusStateSingelton = new ConsensusState();
        }

        public static ConcurrentDictionary<string, AdjPool> AdjPool;
        public static ConcurrentDictionary<(long Height, int MethodCode), ConcurrentDictionary<string, (string Message, string Signature)>> Messages;
        public static ConcurrentDictionary<(long Height, int MethodCode), ConcurrentDictionary<string, (string Hash, string Signature)>> Hashes;

        public static object UpdateNodeLock = new object();
        private static ConsensusState ConsenusStateSingelton;
        private static object UpdateLock = new object();
        public override async Task OnConnectedAsync()
        {
            string peerIP = null;
            try
            {
                peerIP = GetIP(Context);
                if(!Globals.Nodes.ContainsKey(peerIP))
                {
                    EndOnConnect(peerIP, peerIP + " attempted to connect as adjudicator", peerIP + " attempted to connect as adjudicator");
                    return;
                }

                var httpContext = Context.GetHttpContext();
                if (httpContext == null)
                {
                    EndOnConnect(peerIP, "httpContext is null", "httpContext is null");
                    return;
                }

                var address = httpContext.Request.Headers["address"].ToString();
                var time = httpContext.Request.Headers["time"].ToString();
                var signature = httpContext.Request.Headers["signature"].ToString();

                var Now = TimeUtil.GetTime();
                if (Math.Abs(Now - long.Parse(time)) > 15)
                {
                    EndOnConnect(peerIP, "Signature Bad time.", "Signature Bad time.");
                    return;
                }

                if (!Globals.Signatures.TryAdd(signature, Now))
                {
                    EndOnConnect(peerIP, "Reused signature.", "Reused signature.");
                    return;
                }

                var fortisPool = Globals.FortisPool.Values;
                if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(signature))
                {
                    EndOnConnect(peerIP,
                        "Connection Attempted, but missing field(s). Address, and Signature required. You are being disconnected.",
                        "Connected, but missing field(s). Address, and Signature required: " + address);
                    return;
                }

                var verifySig = SignatureService.VerifySignature(address, address + ":" + time, signature);
                if (!verifySig)
                {
                    EndOnConnect(peerIP,
                        "Connected, but your address signature failed to verify. You are being disconnected.",
                        "Connected, but your address signature failed to verify with Consensus: " + address);
                    return;
                }

                if (!AdjPool.TryAdd(peerIP, new AdjPool { Address = address, Context = Context }))
                {
                    var Pool = AdjPool[peerIP];
                    if (Pool?.Context.ConnectionId != Context.ConnectionId)
                    {
                        Pool.Context?.Abort();                        
                    }
                    Pool.Context = Context;
                }
            }
            catch (Exception ex)
            {                
                ErrorLogUtility.LogError($"Unhandled exception has happend. Error : {ex.ToString()}", "ConsensusServer.OnConnectedAsync()");
            }

        }
        private void EndOnConnect(string ipAddress, string adjMessage, string loggMessage)
        {            
            if (Globals.OptionalLogging == true)
            {
                LogUtility.Log(loggMessage, "Consensus Connection");
                LogUtility.Log($"IP: {ipAddress} ", "Consensus Connection");
            }

            Context?.Abort();
        }

        public static void UpdateState(long height = -100, int methodCode = -100, int status = -1, int randomNumber = -1, string encryptedAnswer = null, bool? isUsed = null)
        {
            lock (UpdateLock)
            {
                if(height != -100)
                    ConsenusStateSingelton.Height = height;
                if (status != -1)
                    ConsenusStateSingelton.Status = (ConsensusStatus)status;
                if (methodCode != -100)
                    ConsenusStateSingelton.MethodCode = methodCode;
                if (randomNumber != -1)
                    ConsenusStateSingelton.RandomNumber = randomNumber;
                if(encryptedAnswer != null)
                    ConsenusStateSingelton.EncryptedAnswer = encryptedAnswer;
                if (isUsed != null)
                    ConsenusStateSingelton.IsUsed = isUsed.Value;
            }
        }
        public static (long Height, int MethodCode, ConsensusStatus Status, int Answer, string EncryptedAnswer, bool IsUsed) GetState()
        {
            if (ConsenusStateSingelton == null)
                return (-1, 0, ConsensusStatus.Processing, -1, null, false);
            return (ConsenusStateSingelton.Height, ConsenusStateSingelton.MethodCode, ConsenusStateSingelton.Status, ConsenusStateSingelton.RandomNumber,
                ConsenusStateSingelton.EncryptedAnswer, ConsenusStateSingelton.IsUsed);
        }    

        public static void UpdateNode(NodeInfo node, long height, int methodCode, bool finalized)
        {
            lock(UpdateNodeLock)
            {
                node.NodeHeight = height;
                node.MethodCode = methodCode;
                node.IsFinalized = finalized;
                node.LastMethodCodeTime = TimeUtil.GetMillisecondTime();
            }

            RemoveStaleCache(node);
        }

        public static void RemoveStaleCache(NodeInfo node)
        {
            var NodeHeight = node.NodeHeight + 1;
            var MessageKeys = ConsensusServer.Messages.Where(x => x.Value.ContainsKey(node.Address)).Select(x => x.Key).ToHashSet();
            var HashKeys = ConsensusServer.Hashes.Where(x => x.Value.ContainsKey(node.Address)).Select(x => x.Key).ToHashSet();

            var state = GetState();
            foreach (var key in MessageKeys.Where(x => x.MethodCode != node.MethodCode && state.MethodCode != x.MethodCode))
                if (ConsensusServer.Messages.TryGetValue(key, out var message))
                    message.TryRemove(node.Address, out _);
            foreach (var key in HashKeys.Where(x => (node.IsFinalized && x.MethodCode != node.MethodCode) || 
                (!node.IsFinalized && x.MethodCode + 1 == node.MethodCode)))
                if (ConsensusServer.Hashes.TryGetValue(key, out var hash))
                    hash.TryRemove(node.Address, out _);
        }

        public static void UpdateConsensusDump(string ipAddress, string method, string request, string response)
        {
            var NodeElem = Globals.ConsensusDump.GetOrAdd(ipAddress, new ConcurrentDictionary<string, (DateTime Time, string Request, string Response)>());
            NodeElem[method] = (DateTime.Now, request, response);
        }

        public string RequestMethodCode(long height, int methodCode, bool isFinalized)
        {
            var ip = GetIP(Context);
            LogUtility.LogQueue(ip + " " + height + " " + methodCode + " " + isFinalized + " " + TimeUtil.GetMillisecondTime(), "RequestMethodCode", "ConsensusServer.txt", false);
            if (!Globals.Nodes.TryGetValue(ip, out var node))
            {
                Context?.Abort();
                UpdateConsensusDump(ip, "RequestMethodCode", height + " " + methodCode + " " + isFinalized, null);
                return null;
            }

            UpdateNode(node, height, methodCode, isFinalized);

            UpdateConsensusDump(ip, "RequestMethodCode", height + " " + methodCode + " " + isFinalized, (Globals.LastBlock.Height).ToString() + ":" + ConsenusStateSingelton.MethodCode + ":" +
                (ConsenusStateSingelton.Status == ConsensusStatus.Finalized ? 1 : 0));
            return (Globals.LastBlock.Height).ToString() + ":" + ConsenusStateSingelton.MethodCode + ":" +
                (ConsenusStateSingelton.Status == ConsensusStatus.Finalized ? 1 : 0);
        }

        public string Message(long height, int methodCode, string[] addresses, string peerMessage)
        {
            //var now = DateTime.Now.ToString();
            string Prefix = null;
            string ip = null;
            try
            {
                ip = GetIP(Context);
                LogUtility.LogQueue(ip + " " + height + " " + methodCode + " " + string.IsNullOrWhiteSpace(peerMessage) + " " + TimeUtil.GetMillisecondTime(), "Message", "ConsensusServer.txt", false);                
                if (!Globals.Nodes.TryGetValue(ip, out var node))
                {
                    Context?.Abort();
                    UpdateConsensusDump(ip, "Message", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerMessage, null);
                    return null;
                }

                UpdateNode(node, height - 1, methodCode, false);
                Prefix = (Globals.LastBlock.Height).ToString() + ":" + ConsenusStateSingelton.MethodCode + ":" +
                                    (ConsenusStateSingelton.Status == ConsensusStatus.Finalized ? 1 : 0);

                string message = null;
                string signature = null;
                if(peerMessage != null)
                {
                    var split = peerMessage.Split(";:;");
                    (message, signature) = (split[0], split[1]);
                    message = message.Replace("::", ":");
                }

                var messages = Messages.GetOrAdd((height, methodCode), new ConcurrentDictionary<string, (string Message, string Signature)>());                
                var state = GetState();

                
                if (message != null && ((methodCode == 0 && state.MethodCode != 0)) ||
                    (height == Globals.LastBlock.Height + 1 
                    && ((methodCode == state.MethodCode && state.Status != ConsensusStatus.Finalized) || 
                        (methodCode == state.MethodCode + 1 && state.Status == ConsensusStatus.Finalized)))
                    && SignatureService.VerifySignature(node.Address, message, signature))
                    messages[node.Address] = (message, signature);
                
                foreach (var address in addresses)
                {
                    if (messages.TryGetValue(address, out var Value))
                    {
                        UpdateConsensusDump(ip, "Message", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerMessage,
                            Prefix + "|" + address + ";:;" + Value.Message.Replace(":", "::") + ";:;" + Value.Signature);
                        return Prefix + "|" + address + ";:;" + Value.Message.Replace(":", "::") + ";:;" + Value.Signature;
                    }
                }               
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ConsensusServer.Message()");
            }

            UpdateConsensusDump(ip, "Message", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerMessage, Prefix);
            return Prefix;
        }

        public string Hash(long height, int methodCode, string[] addresses, string peerHash)
        {
            string Prefix = null;
            string ip = null;
            try
            {
                ip = GetIP(Context);
                LogUtility.LogQueue(ip + " " + height + " " + methodCode + " " + string.IsNullOrWhiteSpace(peerHash) + " " + TimeUtil.GetMillisecondTime(), "Hash", "ConsensusServer.txt", false);                
                if (!Globals.Nodes.TryGetValue(ip, out var node))
                {
                    Context?.Abort();
                    UpdateConsensusDump(ip, "Hash", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerHash, null);
                    return null;
                }

                UpdateNode(node, height - 1, methodCode, true);
                Prefix = (Globals.LastBlock.Height).ToString() + ":" + ConsenusStateSingelton.MethodCode + ":" +
                                    (ConsenusStateSingelton.Status == ConsensusStatus.Finalized ? 1 : 0);

                string hash = null;
                string signature = null;
                if (peerHash != null)
                {
                    var split = peerHash.Split(":");
                    (hash, signature) = (split[0], split[1]);                    
                }

                var hashes = Hashes.GetOrAdd((height, methodCode), new ConcurrentDictionary<string, (string Hash, string Signature)>());
                var state = GetState();
                if (hash != null && height == Globals.LastBlock.Height + 1 && methodCode == state.MethodCode && SignatureService.VerifySignature(node.Address, hash, signature))
                    hashes[node.Address] = (hash, signature);

                if (ConsenusStateSingelton.Status != ConsensusStatus.Finalized && methodCode == state.MethodCode)
                {
                    UpdateConsensusDump(ip, "Hash", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerHash, null);
                    return null;
                }
                
                foreach (var address in addresses)
                {
                    if (hashes.TryGetValue(address, out var Value))
                    {
                        UpdateConsensusDump(ip, "Hash", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerHash,
                            Prefix + "|" + address + ":" + Value.Hash + ":" + Value.Signature);
                        return Prefix + "|" + address + ":" + Value.Hash + ":" + Value.Signature;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ConsensusServer.Hash()");
            }

            UpdateConsensusDump(ip, "Hash", height + " " + methodCode + " (" + string.Join(",", addresses) + ") " + peerHash, Prefix);
            return Prefix;
        }

        public async Task<bool> GetBroadcastedTx(List<TransactionBroadcast> txBroadcastList)
        {
            bool result = false;
            try
            {
                var txHashes = JsonConvert.SerializeObject(txBroadcastList.Select(x => x.Hash).ToArray());
                AdjLogUtility.Log($"UTC Time: {DateTime.Now} TX List Received: {txHashes}", "ConsensusServer.GetBroadcastedTx()");
                foreach (var txBroadcast in txBroadcastList)
                {
                    if (!Globals.ConsensusBroadcastedTrxDict.TryGetValue(txBroadcast.Hash, out _))
                    {
                        var isTxStale = await TransactionData.IsTxTimestampStale(txBroadcast.Transaction);
                        if(!isTxStale)
                        {
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txBroadcast.Transaction);

                            if (!isCraftedIntoBlock)
                            {
                                Globals.ConsensusBroadcastedTrxDict[txBroadcast.Hash] = new TransactionBroadcast
                                {
                                    Hash = txBroadcast.Hash,
                                    IsBroadcastedToAdj = false,
                                    IsBroadcastedToVal = false,
                                    Transaction = txBroadcast.Transaction,
                                    RebroadcastCount = 0
                                };
                            }
                            else
                            {
                                Globals.BroadcastedTrxDict.TryRemove(txBroadcast.Hash, out _);
                                Globals.ConsensusBroadcastedTrxDict.TryRemove(txBroadcast.Hash, out _);
                            }
                        }
                        else
                        {
                            Globals.BroadcastedTrxDict.TryRemove(txBroadcast.Hash, out _);
                            Globals.ConsensusBroadcastedTrxDict.TryRemove(txBroadcast.Hash, out _);
                        }
                    }
                }

                result = true;
            }
            catch(Exception ex)
            {
                result = false;
                AdjLogUtility.Log($"Error receiving broadcasted TX. Error: {ex.ToString()}", "ConsensusServer.GetBroadcastedTx()");
                ErrorLogUtility.LogError($"Error receiving broadcasted TX. Error: {ex.ToString()}", "ConsensusServer.GetBroadcastedTx()");
            }

            return result;
        }

        private static string GetIP(HubCallerContext context)
        {
            try
            {
                var peerIP = "NA";
                var feature = context.Features.Get<IHttpConnectionFeature>();
                if (feature != null)
                {
                    if (feature.RemoteIpAddress != null)
                    {
                        peerIP = feature.RemoteIpAddress.MapToIPv4().ToString();
                    }
                }

                return peerIP;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ConsensusServer.GetIP()");
            }

            return "0.0.0.0";
        }
    }
}
