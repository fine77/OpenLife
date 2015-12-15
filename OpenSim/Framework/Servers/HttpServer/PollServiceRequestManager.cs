﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Threading;
using System.Reflection;
using log4net;
using HttpServer;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using Amib.Threading;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace OpenSim.Framework.Servers.HttpServer
{
    public class PollServiceRequestManager
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly BaseHttpServer m_server;

        private Dictionary<PollServiceHttpRequest, Queue<PollServiceHttpRequest>> m_bycontext;
        private BlockingQueue<PollServiceHttpRequest> m_requests = new BlockingQueue<PollServiceHttpRequest>();
        private static Queue<PollServiceHttpRequest> m_slowRequests = new Queue<PollServiceHttpRequest>();
        private static Queue<PollServiceHttpRequest> m_retryRequests = new Queue<PollServiceHttpRequest>();

        private uint m_WorkerThreadCount = 0;
        private Thread[] m_workerThreads;
        private Thread m_retrysThread;

        private bool m_running = false;
        private int slowCount = 0;

        private SmartThreadPool m_threadPool;


        public PollServiceRequestManager(
            BaseHttpServer pSrv, bool performResponsesAsync, uint pWorkerThreadCount, int pTimeout)
        {
            m_server = pSrv;
            m_WorkerThreadCount = pWorkerThreadCount;
            m_workerThreads = new Thread[m_WorkerThreadCount];

            PollServiceHttpRequestComparer preqCp = new PollServiceHttpRequestComparer();
            m_bycontext = new Dictionary<PollServiceHttpRequest, Queue<PollServiceHttpRequest>>(preqCp);

            STPStartInfo startInfo = new STPStartInfo();
            startInfo.IdleTimeout = 30000;
            startInfo.MaxWorkerThreads = 15;
            startInfo.MinWorkerThreads = 1;
            startInfo.ThreadPriority = ThreadPriority.Normal;
            startInfo.StartSuspended = true;
            startInfo.ThreadPoolName = "PoolService";

            m_threadPool = new SmartThreadPool(startInfo);
		
        }

        public void Start()
        {
            m_running = true;
            m_threadPool.Start();
            //startup worker threads
            for (uint i = 0; i < m_WorkerThreadCount; i++)
            {
                m_workerThreads[i]
                    = WorkManager.StartThread(
                        PoolWorkerJob,
                        string.Format("PollServiceWorkerThread {0}:{1}", i, m_server.Port),
                        ThreadPriority.Normal,
                        false,
                        false,
                        null,
                        int.MaxValue);
            }

            m_retrysThread = WorkManager.StartThread(
                this.CheckRetries,
                string.Format("PollServiceWatcherThread:{0}", m_server.Port),
                ThreadPriority.Normal,
                false,
                true,
                null,
                1000 * 60 * 10);
				

        }

        private void ReQueueEvent(PollServiceHttpRequest req)
        {
            if (m_running)
            {
                lock (m_retryRequests)
                    m_retryRequests.Enqueue(req);
            }
        }

        public void Enqueue(PollServiceHttpRequest req)
        {
            lock (m_bycontext)
            {
                Queue<PollServiceHttpRequest> ctxQeueue;
                if (m_bycontext.TryGetValue(req, out ctxQeueue))
                {
                    ctxQeueue.Enqueue(req);
                }
                else
                {
                    ctxQeueue = new Queue<PollServiceHttpRequest>();
                    m_bycontext[req] = ctxQeueue;
                    EnqueueInt(req);
                }
            }
        }

        public void byContextDequeue(PollServiceHttpRequest req)
        {
            Queue<PollServiceHttpRequest> ctxQeueue;
            lock (m_bycontext)
            {
                if (m_bycontext.TryGetValue(req, out ctxQeueue))
                {
                    if (ctxQeueue.Count > 0)
                    {
                        PollServiceHttpRequest newreq = ctxQeueue.Dequeue();
                        EnqueueInt(newreq);
                    }
                    else
                    {
                        m_bycontext.Remove(req);
                    }
                }
            }
        }


        public void EnqueueInt(PollServiceHttpRequest req)
        {
            if (m_running)
            {
                if (req.PollServiceArgs.Type != PollServiceEventArgs.EventType.LongPoll)
                {
                    m_requests.Enqueue(req);
                }
                else
                {
                    lock (m_slowRequests)
                        m_slowRequests.Enqueue(req);
                }
            }
        }

        private void CheckRetries()
        {
            while (m_running)

            {
                Thread.Sleep(100); // let the world move  .. back to faster rate
                Watchdog.UpdateThread();
                lock (m_retryRequests)
                {
                    while (m_retryRequests.Count > 0 && m_running)
                        m_requests.Enqueue(m_retryRequests.Dequeue());
                }
                slowCount++;
                if (slowCount >= 10)
                {
                    slowCount = 0;

                    lock (m_slowRequests)
                    {
                        while (m_slowRequests.Count > 0 && m_running)
                            m_requests.Enqueue(m_slowRequests.Dequeue());
                    }
                }
            }
        }

        public void Stop()
        {
            m_running = false;

            Thread.Sleep(1000); // let the world move

            foreach (Thread t in m_workerThreads)
                Watchdog.AbortThread(t.ManagedThreadId);

            // any entry in m_bycontext should have a active request on the other queues
            // so just delete contents to easy GC
            foreach (Queue<PollServiceHttpRequest> qu in m_bycontext.Values)
                qu.Clear();
            m_bycontext.Clear();

            try
            {
                foreach (PollServiceHttpRequest req in m_retryRequests)
                {
                    req.DoHTTPstop(m_server);
                }
            }
            catch
            {
            }

            PollServiceHttpRequest wreq;
            m_retryRequests.Clear();

            lock (m_slowRequests)
            {
                while (m_slowRequests.Count > 0)
                    m_requests.Enqueue(m_slowRequests.Dequeue());

            }

            while (m_requests.Count() > 0)
            {
                try
                {
                    wreq = m_requests.Dequeue(0);
                    wreq.DoHTTPstop(m_server);

                }
                catch
                {
                }
            }

            m_requests.Clear();
        }

        // work threads

        private void PoolWorkerJob()
        {
            while (m_running)
            {
                PollServiceHttpRequest req = m_requests.Dequeue(5000);

                Watchdog.UpdateThread();
                if (req != null)
                {
                    try
                    {
                        if (req.PollServiceArgs.HasEvents(req.RequestID, req.PollServiceArgs.Id))
                        {
                            Hashtable responsedata = req.PollServiceArgs.GetEvents(req.RequestID, req.PollServiceArgs.Id);

                            if (req.PollServiceArgs.Type == PollServiceEventArgs.EventType.LongPoll) // This is the event queue
                            {
                                try
                                {
                                    req.DoHTTPGruntWork(m_server, responsedata);
                                    byContextDequeue(req);
                                }
                                catch (ObjectDisposedException) // Browser aborted before we could read body, server closed the stream
                                {
                                    // Ignore it, no need to reply
                                }
                            }
                            else
                            {
                                m_threadPool.QueueWorkItem(x =>
                                {
                                    try
                                    {
                                        req.DoHTTPGruntWork(m_server, responsedata);
                                        byContextDequeue(req);
                                    }
                                    catch (ObjectDisposedException) // Browser aborted before we could read body, server closed the stream
                                    {
                                        // Ignore it, no need to reply
                                    }

                                    return null;
                                }, null);
                            }
                        }
                        else
                        {
                            if ((Environment.TickCount - req.RequestTime) > req.PollServiceArgs.TimeOutms)
                            {
                                req.DoHTTPGruntWork(m_server,
                                    req.PollServiceArgs.NoEvents(req.RequestID, req.PollServiceArgs.Id));
                                byContextDequeue(req);
                            }
                            else
                            {
                                ReQueueEvent(req);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("Exception in poll service thread: " + e.ToString());
                    }
                }
            }
        }

    }
}
