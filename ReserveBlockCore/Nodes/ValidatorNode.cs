﻿using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks.Dataflow;


namespace ReserveBlockCore.Nodes
{
    public class ValidatorNode : IHostedService, IDisposable
    {
        public static IHubContext<P2PValidatorServer> HubContext;
        private readonly IHubContext<P2PValidatorServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        private static SemaphoreSlim ConsensusLock = new SemaphoreSlim(1, 1);
        private static bool ActiveValidatorRequestDone = false;
        private static bool AlertValidatorsOfStatusDone = false;
        static SemaphoreSlim NotifyExplorerLock = new SemaphoreSlim(1, 1);
        private static ConcurrentBag<(string, long)> ValidatorApprovalBag = new ConcurrentBag<(string, long)>();

        public ValidatorNode(IHubContext<P2PValidatorServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            //Request latest val list - RequestValidatorList()
            _ = ActiveValidatorRequest();

            //Alert vals you are online - OnlineMethod()
            _ = AlertValidatorsOfStatus();

            //Checks for active vals every 15 mins
            _ = ValidatorHeartbeat();

            //Notify Explorer for visibility.
            _ = NotifyExplorer();

            //Start consensus
            _ = StartConsensus();

            //return Task.CompletedTask;
        }
        public static async Task ActiveValidatorRequest()
        {
            bool waitForStartup = true;
            while (waitForStartup)
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers || !Globals.IsChainSynced))
                {
                    await delay;
                    continue;
                }
                waitForStartup = false;
            }

            await P2PValidatorClient.RequestActiveValidators();

            ActiveValidatorRequestDone = true;
        }

        public static async Task AlertValidatorsOfStatus(bool comingOnline = true)
        {
            if (comingOnline)
            {
                while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    var delay = Task.Delay(new TimeSpan(0, 0, 30));
                    if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                    {
                        await delay;
                        continue;
                    }

                    var valList = Globals.NetworkValidators.Values.ToList();

                    if(!valList.Any())
                    {
                        await delay;
                        continue;
                    }

                    var account = AccountData.GetLocalValidator();
                    var validators = Validators.Validator.GetAll();
                    var validator = validators.FindOne(x => x.Address == account.Address);
                    if (validator == null)
                        return;

                    var time = TimeUtil.GetTime().ToString();
                    var signature = SignatureService.ValidatorSignature(validator.Address + ":" + time + ":" + account.PublicKey);

                    var networkVal = new NetworkValidator { 
                        Address = validator.Address,
                        IPAddress = "0.0.0.0",
                        CheckFailCount = 0,
                        PublicKey = account.PublicKey,
                        Signature = signature,
                        UniqueName = validator.UniqueName,
                        SignatureMessage = validator.Address + ":" + time + ":" + account.PublicKey
                    };

                    var postData = JsonConvert.SerializeObject(networkVal);
                    var httpContent = new StringContent(postData, Encoding.UTF8, "application/json");

                    valList.ParallelLoop(async peer =>
                    {
                        using (var client = Globals.HttpClientFactory.CreateClient())
                        {
                            try
                            {
                                var uri = $"http://{peer.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/status";
                                await client.PostAsync(uri, httpContent).WaitAsync(new TimeSpan(0, 0, 1));
                                await Task.Delay(75);
                            }
                            catch (Exception ex) { }

                        }
                    });

                    comingOnline = false;
                    AlertValidatorsOfStatusDone = true;
                    await Task.Delay(new TimeSpan(0, 30, 0));
                }
            }
            else
            {

            }
        }

        public static async Task ValidatorHeartbeat()
        {
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                if (!AlertValidatorsOfStatusDone || !ActiveValidatorRequestDone)
                {
                    await delay;
                    continue;
                }

                var peerList = Globals.NetworkValidators.Values.ToList();

                if (!peerList.Any()) 
                {
                    await delay;
                    continue;
                }

                ConcurrentBag<string> BadValidatorList = new ConcurrentBag<string>();

                var peerDB = Peers.GetAll();

                foreach (var val in peerList) {
                    var valBalance = AccountStateTrei.GetAccountBalance(val.Address);

                    if(valBalance < Globals.ValidatorRequiredRBX)
                        BadValidatorList.Add(val.IPAddress);
                }

                peerList.ParallelLoop(async peer =>
                {
                    try
                    {
                        if (!BadValidatorList.Contains(peer.IPAddress))
                        {
                            using (var client = Globals.HttpClientFactory.CreateClient())
                            {
                                var uri = $"http://{peer.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/heartbeat";
                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 2));

                                if (response != null)
                                {
                                    if (!response.IsSuccessStatusCode)
                                        BadValidatorList.Add(peer.IPAddress);

                                    if(response.IsSuccessStatusCode)
                                    {
                                        Globals.NetworkValidators.TryGetValue(peer.Address, out var networkValidator);
                                        if (networkValidator != null)
                                        {
                                            networkValidator.CheckFailCount = 0;
                                            Globals.NetworkValidators[networkValidator.Address] = networkValidator;
                                        }
                                    }
                                }
                            }
                        }
                        
                    }
                    catch (Exception ex)
                    {
                        BadValidatorList.Add(peer.IPAddress);
                    }
                });

                foreach (var val in BadValidatorList)
                {
                    var networkVal = Globals.NetworkValidators.Values.Where(x => x.IPAddress == val).FirstOrDefault();
                    if(networkVal != null)
                    {
                        networkVal.CheckFailCount++;
                        Globals.NetworkValidators[networkVal.Address] = networkVal;

                        if (networkVal.CheckFailCount > 3)
                        {
                            var validator = peerDB.FindOne(x => x.PeerIP == val);
                            if (validator != null)
                            {
                                validator.IsValidator = false;
                                peerDB.UpdateSafe(validator);
                            }

                            Globals.NetworkValidators.TryRemove(networkVal.Address, out var _);
                        }
                    }
                }

                await Task.Delay(new TimeSpan(0, 0, 30));
            }
        }

        public static async Task StartConsensus()
        {
            var EpochTime = Globals.IsTestNet ? 1731454926600L : 1674172800000L;
            var BeginBlock = Globals.IsTestNet ? Globals.V4Height : Globals.V3Height;
            var PreviousHeight = -1L;
            var BlockDelay = Task.CompletedTask;

            ConsoleWriterService.Output("Booting up consensus loop");
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 5));
                if ((Globals.StopAllTimers && !Globals.IsChainSynced) || Globals.Nodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                if (!AlertValidatorsOfStatusDone || !ActiveValidatorRequestDone)
                {
                    await delay;
                    continue;
                }

                try
                {
                    var Height = Globals.LastBlock.Height + 1;

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    if (PreviousHeight != Height)
                    {
                        PreviousHeight = Height;
                        await Task.WhenAll(BlockDelay, Task.Delay(1500));
                        var CurrentTime = TimeUtil.GetMillisecondTime();
                        var DelayTimeCorrection = Globals.BlockTime * (Height - BeginBlock) - (CurrentTime - EpochTime);
                        var DelayTime = Math.Min(Math.Max(Globals.BlockTime + DelayTimeCorrection, Globals.BlockTimeMin), Globals.BlockTimeMax);
                        BlockDelay = Task.Delay((int)DelayTime);
                        ConsoleWriterService.Output("\r\nNext Consensus Delay: " + DelayTime + " (" + DelayTimeCorrection + ")");
                    }

                    if (Height != Globals.LastBlock.Height + 1)
                        continue;

                    //Generate Proofs for ALL vals
                    ConsoleWriterService.Output("\r\nGenerating Proofs");
                    var proofs = await ProofUtility.GenerateProofs();
                    ConsoleWriterService.Output($"\r\n{proofs.Count()} Proofs Generated");
                    var winningProof = await ProofUtility.SortProofs(proofs);
                    ConsoleWriterService.Output($"\r\nSorting Proofs");

                    //cast vote to master and subs
                    if (winningProof != null)
                    {
                        ConsoleWriterService.Output($"\r\nPotential Winner Found! Address: {winningProof.Address}");
                        Globals.Proofs.Add(winningProof);
                        await Broadcast("2", JsonConvert.SerializeObject(winningProof), "SendWinningProofVote");
                    }

                    //await 
                    await Task.Delay(2500); //Give 3 seconds for other proofs. Might be able to reduce this.

                    var finalizedWinnerGroup = Globals.Proofs
                        .GroupBy(x => x.Address)  // Group by address
                        .OrderByDescending(x => x.Count())  // Primary sort by count
                        .ThenBy(x => x.Min(y => Math.Abs(y.VRFNumber)))  // Secondary sort by the VRFNumber closest to zero
                        .FirstOrDefault();  // Get the first group (the "winner")

                    if (finalizedWinnerGroup != null)
                    {
                        var finalizedWinner = finalizedWinnerGroup.FirstOrDefault();
                        if (finalizedWinner != null)
                        {
                            if (finalizedWinner.Address == Globals.ValidatorAddress)
                            {
                                //Craft Block
                                ConsoleWriterService.Output($"\r\nYou Won! Awaiting Approval To Craft Block");
                                ValidatorApprovalBag = new ConcurrentBag<(string, long)>();

                                bool approved = false;

                                ValidatorApprovalBag.Add(("local", finalizedWinner.BlockHeight));

                                while (!approved)
                                {
                                    var valCount = Globals.NetworkValidators.Count();
                                    var approvalCount = ValidatorApprovalBag.Where(x => x.Item2 == finalizedWinner.BlockHeight).Count();

                                    decimal approvalRate = (decimal)approvalCount / valCount;

                                    if(approvalRate >= 0.51M)
                                        approved = true;

                                    await Task.Delay(200);
                                }

                                var nextblock = Globals.LastBlock.Height + 1;
                                var block = await BlockchainData.CraftBlock_V5(
                                                Globals.ValidatorAddress,
                                                Globals.NetworkValidators.Count(),
                                                finalizedWinner.ProofHash, nextblock);

                                if (block != null)
                                {
                                    ConsoleWriterService.Output($"\r\nBlock crafted. Sending block.");
                                    //Send block to others
                                    _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                    _ = P2PValidatorClient.BroadcastBlock(block);
                                }
                            }
                            else
                            {
                                //Give winner time to craft. Might need to increase.
                                var approvalSent = false;
                                var count = 0;

                                while (!approvalSent) 
                                {
                                    if(count > 3)
                                        approvalSent=true;

                                    using (var client = Globals.HttpClientFactory.CreateClient())
                                    {
                                        var uri = $"http://{finalizedWinner.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/sendapproval/{finalizedWinner.BlockHeight}";
                                        var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 2));
                                        if (response.IsSuccessStatusCode)
                                        {
                                            approvalSent = true;
                                        }
                                        else
                                        {
                                            await Task.Delay(200);
                                            count++;
                                        }
                                    }
                                }
                                await Task.Delay(2000);
                                ConsoleWriterService.Output($"\r\nYou did not win. Looking for block.");
                                //Request block if it is not here
                                if (Globals.LastBlock.Height < finalizedWinner.BlockHeight)
                                {
                                    for (var i = 0; i < 3; i++)
                                    {
                                        try
                                        {
                                            using (var client = Globals.HttpClientFactory.CreateClient())
                                            {
                                                var uri = $"http://{finalizedWinner.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/getblock/{finalizedWinner.BlockHeight}";
                                                var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 2));

                                                if (response != null)
                                                {
                                                    if (response.IsSuccessStatusCode)
                                                    {
                                                        var responseBody = await response.Content.ReadAsStringAsync();
                                                        if (responseBody != null)
                                                        {
                                                            if (responseBody == "0")
                                                            {
                                                                await Task.Delay(1000);
                                                                continue;
                                                            }

                                                            var block = JsonConvert.DeserializeObject<Block>(responseBody);
                                                            if (block != null)
                                                            {
                                                                var IP = finalizedWinner.IPAddress;
                                                                var nextHeight = Globals.LastBlock.Height + 1;
                                                                var currentHeight = block.Height;

                                                                if (currentHeight >= nextHeight && BlockDownloadService.BlockDict.TryAdd(currentHeight, (block, IP)))
                                                                {
                                                                    await Task.Delay(2000);

                                                                    if (Globals.LastBlock.Height < block.Height)
                                                                        await BlockValidatorService.ValidateBlocks();

                                                                    if (nextHeight == currentHeight)
                                                                    {
                                                                        _ = Broadcast("7", JsonConvert.SerializeObject(block), "");
                                                                        _ = P2PValidatorClient.BroadcastBlock(block);
                                                                    }

                                                                    if (nextHeight < currentHeight)
                                                                        await BlockDownloadService.GetAllBlocks();
                                                                }
                                                            }
                                                        }
                                                    }

                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {

                                        }
                                    }
                                }
                            }
                        }
                    }

                    //start over.
                    ConsoleWriterService.Output($"\r\nStarting over.");
                    Globals.Proofs.Clear();
                    Globals.Proofs = new ConcurrentBag<Proof>();

                }
                catch (Exception ex)
                {

                }
            }

        }

        #region Process Data
        public static async Task ProcessData(string message, string data, string ipAddress)
        {
            if (string.IsNullOrEmpty(message))
                return;

            switch (message)
            {
                case "1":
                    _ = IpMessage(data);
                    break;
                case "2":
                    _ = ReceiveVote(data);
                    break;
                case "3":
                    _ = ReceiveNetworkValidator(data);
                    break;
                case "7":
                    _ = ReceiveConfirmedBlock(data);
                    break;
                case "7777":
                    _ = TxMessage(data);
                    break;
                case "9999":
                    _ = FailedToConnect(data);
                    break;
            }
        }

        #endregion

        #region Messages
        //1
        private static async Task IpMessage(string data)
        {
            var IP = data.ToString();
            if (Globals.ReportedIPs.TryGetValue(IP, out int Occurrences))
                Globals.ReportedIPs[IP]++;
            else
                Globals.ReportedIPs[IP] = 1;
        }

        //2
        private static async Task ReceiveVote(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            try
            {
                var proof = JsonConvert.DeserializeObject<Proof>(data);
                if (proof != null)
                {
                    if (proof.VerifyProof())
                        Globals.Proofs.Add(proof);
                }
            }
            catch (Exception ex)
            {
            }
        }

        //3
        private static async Task ReceiveNetworkValidator(string data)
        {
            try
            {
                var netVal = JsonConvert.DeserializeObject<NetworkValidator>(data);
                if (netVal == null)
                    return;

                await NetworkValidator.AddValidatorToPool(netVal);
            }
            catch (Exception ex)
            {

            }
        }

        //7
        public static async Task ReceiveConfirmedBlock(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            var nextBlock = JsonConvert.DeserializeObject<Block>(data);

            if (nextBlock == null) return;

            var lastBlockHeight = Globals.LastBlock.Height;
            if (lastBlockHeight < nextBlock.Height)
            {
                var result = await BlockValidatorService.ValidateBlock(nextBlock, true, false, false, true);
                if (result)
                {
                    if (nextBlock.Height > lastBlockHeight)
                    {
                        _ = P2PValidatorClient.BroadcastBlock(nextBlock, false);
                    }

                }
            }
        }

        //7777
        private static async Task TxMessage(string data)
        {
            var transaction = JsonConvert.DeserializeObject<Transaction>(data);
            if (transaction != null)
            {
                var ablList = Globals.ABL.ToList();
                if (ablList.Exists(x => x == transaction.FromAddress))
                {
                    return;
                }

                var isTxStale = await TransactionData.IsTxTimestampStale(transaction);
                if (!isTxStale)
                {
                    var mempool = TransactionData.GetPool();

                    if (mempool.Count() != 0)
                    {
                        var txFound = mempool.FindOne(x => x.Hash == transaction.Hash);
                        if (txFound == null)
                        {
                            var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                            var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(transaction) : await TransactionValidatorService.VerifyTX(transaction, false, false, true);
                            if (txResult.Item1 == true)
                            {
                                var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                var rating = await TransactionRatingService.GetTransactionRating(transaction);
                                transaction.TransactionRating = rating;

                                if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                {
                                    mempool.InsertSafe(transaction);
                                    _ = Broadcast("7777", data, "SendTxToMempoolVals");

                                }
                            }

                        }
                        else
                        {
                            //TODO Add this to also check in-mem blocks
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                            if (isCraftedIntoBlock)
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == transaction.Hash);// tx has been crafted into block. Remove.
                                }
                                catch (Exception ex)
                                {
                                    //delete failed
                                }
                            }
                        }
                    }
                    else
                    {
                        var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                        var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(transaction) : await TransactionValidatorService.VerifyTX(transaction, false, false, true);
                        if (txResult.Item1 == true)
                        {
                            var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                            var rating = await TransactionRatingService.GetTransactionRating(transaction);
                            transaction.TransactionRating = rating;

                            if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                            {
                                mempool.InsertSafe(transaction);
                            }
                        }
                    }
                }

            }
        }

        //9999
        public static async Task FailedToConnect(string data)
        {

        }
        #endregion

        #region Notify Explorer Status
        public static async Task NotifyExplorer()
        {
            while (true && !string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                var delay = Task.Delay(new TimeSpan(0, 1, 0));
                try
                {
                    if (Globals.StopAllTimers && !Globals.IsChainSynced)
                    {
                        await delay;
                        continue;
                    }
                    await NotifyExplorerLock.WaitAsync();


                    var account = AccountData.GetLocalValidator();
                    if (account == null)
                        return;

                    var validator = Validators.Validator.GetAll().FindOne(x => x.Address == account.Address);
                    if (validator == null)
                        return;

                    var fortis = new FortisPool
                    {
                        Address = Globals.ValidatorAddress,
                        ConnectDate = Globals.ValidatorStartDate,
                        IpAddress = P2PClient.MostLikelyIP(),
                        LastAnswerSendDate = DateTime.UtcNow,
                        UniqueName = validator.UniqueName,
                        WalletVersion = validator.WalletVersion
                    };

                    List<FortisPool> fortisPool = new List<FortisPool> { fortis };

                    var listFortisPool = fortisPool.Select(x => new
                    {
                        ConnectionId = "NA",
                        x.ConnectDate,
                        x.LastAnswerSendDate,
                        x.IpAddress,
                        x.Address,
                        x.UniqueName,
                        x.WalletVersion
                    }).ToList();

                    var fortisPoolStr = JsonConvert.SerializeObject(listFortisPool);

                    using (var client = Globals.HttpClientFactory.CreateClient())
                    {
                        string endpoint = Globals.IsTestNet ? "https://testnet-data.rbx.network/api/masternodes/send/" : "https://data.rbx.network/api/masternodes/send/";
                        var httpContent = new StringContent(fortisPoolStr, Encoding.UTF8, "application/json");
                        using (var Response = await client.PostAsync(endpoint, httpContent))
                        {
                            if (Response.StatusCode == System.Net.HttpStatusCode.OK)
                            {
                                //success
                                Globals.ExplorerValDataLastSend = DateTime.Now;
                                Globals.ExplorerValDataLastSendSuccess = true;
                            }
                            else
                            {
                                //ErrorLogUtility.LogError($"Error sending payload to explorer. Response Code: {Response.StatusCode}. Reason: {Response.ReasonPhrase}", "ClientCallService.DoFortisPoolWork()");
                                Globals.ExplorerValDataLastSendSuccess = false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Failed to send validator list to explorer API. Error: {ex.ToString()}", "ValidatorService.NotifyExplorer()");
                    Globals.ExplorerValDataLastSendSuccess = false;
                }
                finally
                {
                    NotifyExplorerLock.Release();
                }
                await delay;
            }

        }

        #endregion

        #region Broadcast
        public static async Task Broadcast(string messageType, string data, string method = "")
        {
            await HubContext.Clients.All.SendAsync("GetValMessage", messageType, data);

            if (method == "") return;

            if (!Globals.ValidatorNodes.Any()) return;

            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            if (valNodeList == null || valNodeList.Count() == 0) return;

            foreach (var val in valNodeList)
            {
                var source = new CancellationTokenSource(2000);
                await val.Connection.InvokeCoreAsync(method, args: new object?[] { data }, source.Token);
            }
        }

        #endregion

        #region Get Val List
        public static async Task<Peers[]?> GetValList(bool skipConnectedNodes = false)
        {
            var peerDB = Peers.GetAll();

            var SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
            .Union(Globals.BannedIPs.Keys)
            .Union(Globals.SkipValPeers.Keys)
            .Union(Globals.ReportedIPs.Keys));

            if (!skipConnectedNodes)
            {
                var connectedNodes = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToArray();

                foreach (var validator in connectedNodes)
                {
                    SkipIPs.Add(validator.NodeIP);
                }
            }

            if (Globals.ValidatorAddress == "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC")
            {
                SkipIPs.Add("66.94.124.2");
            }

            var peerList = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray();

            if (!peerList.Any())
            {
                //clear out skipped peers to try again
                Globals.SkipValPeers.Clear();

                SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipValPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

                peerList = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray();
            }

            return peerList;
        }
        #endregion

        #region Get Approval

        public static async Task GetApproval(string? ip, long blockHeight)
        {
            if (ip == null)
                return;

            var alreadyApproved = ValidatorApprovalBag.Where(x => x.Item1 == ip).ToList();
            if (alreadyApproved.Any())
                return;

            ValidatorApprovalBag.Add((ip,blockHeight));
        }

        #endregion

        #region Stop/Dispose
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {

        }

        #endregion
    }
}
