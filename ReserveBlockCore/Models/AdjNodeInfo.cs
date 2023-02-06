﻿using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReserveBlockCore.Models
{
    public class AdjNodeInfo
    {
        public HubConnection Connection;
        public string Address { get; set; }
        public string IpAddress { get; set; }
        public bool LastWinningTaskError { get; set; }

        public long LastWinningTaskRequestTime { get; set; }
        public DateTime LastWinningTaskSentTime { get; set; }
        public long LastWinningTaskBlockHeight { get; set; }
        public long LastSentBlockHeight { get; set; }        
        public DateTime? AdjudicatorConnectDate { get; set; }
        public DateTime? LastTaskSentTime { get; set; }        
        public DateTime? LastTaskResultTime { get; set; }
        public long LastTaskBlockHeight { get; set; }
        public bool LastTaskError { get; set; }
        public int LastTaskErrorCount { get; set; }                       
        public bool IsConnected { get { return Connection?.State == HubConnectionState.Connected; } }
        
        private Task InvokeDelay = Task.CompletedTask;

        private int ProcessQueueLock = 0;

        private ConcurrentQueue<(Func<CancellationToken, Task<object>> invokeFunc, Func<CancellationToken> ctFunc, Action<object> setResult)> invokeQueue =
            new ConcurrentQueue<(Func<CancellationToken, Task<object>> invokeFunc, Func<CancellationToken> ctFunc, Action<object> setResult)>();

        private async Task ProcessQueue()
        {
            if (Interlocked.Exchange(ref ProcessQueueLock, 1) == 1)
                return;

            while (invokeQueue.Count != 0)
            {
                try
                {
                    if (invokeQueue.TryDequeue(out var RequestInfo))
                    {
                        var Fail = true;
                        try
                        {
                            var token = RequestInfo.ctFunc();
                            if (token.IsCancellationRequested)
                            {
                                RequestInfo.setResult(default);
                                continue;
                            }

                            if (Globals.AdjudicateAccount == null)
                            {
                                await InvokeDelay;                                                               
                            }                                

                            var Result = await RequestInfo.invokeFunc(token);
                            InvokeDelay = Task.Delay(1000);
                            RequestInfo.setResult(Result);
                            Fail = false;
                        }
                        catch { }
                        if (Fail)
                            RequestInfo.setResult(default);
                    }
                }
                catch { }
            }

            Interlocked.Exchange(ref ProcessQueueLock, 0);
            if (invokeQueue.Count != 0)
                await ProcessQueue();
        }

        public async Task<T> InvokeAsync<T>(string method, object[] args, Func<CancellationToken> ctFunc)
        {
            try
            {  
                var Source = new TaskCompletionSource<T>();
                var InvokeFunc = async (CancellationToken ct) => {
                    try { return Connection != null ? (object)(await Connection.InvokeCoreAsync<T>(method, args, ct)) : (object)default(T); }
                    catch { }
                    return (object)default(T);
                };
                invokeQueue.Enqueue((InvokeFunc, ctFunc, (object x) => Source.SetResult((T)x)));
                _ = ProcessQueue();

                var Result = await Source.Task;
                return Result;
            }
            catch { }

            return default;
        }
    }
}
