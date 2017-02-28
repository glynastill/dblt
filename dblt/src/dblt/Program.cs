//Copyright (c) 2010-2011 Glyn Astill <glyn@8kb.co.uk>
//Copyright Notice: GPL

using System;
using System.Collections.Generic;
using System.Xml;
using System.Text;
using System.IO;
using System.Threading;
using System.Data;
using System.Data.SqlClient;
using Npgsql;
using System.Configuration;
using System.Diagnostics;

namespace dblt
{
    class dblt
    {
        public static string sServerDescription = (string)ConfigurationManager.AppSettings["ServerDescription"];
        public static string sMode = (string)ConfigurationManager.AppSettings["Mode"];
        public static int iClients = Convert.ToInt32(ConfigurationManager.AppSettings["Clients"]);
        public static int iClientsScale = Convert.ToInt32(ConfigurationManager.AppSettings["ClientsScale"]);
        public static int iClientsMax = Convert.ToInt32(ConfigurationManager.AppSettings["ClientsMax"]);
        public static int iIterations = Convert.ToInt32(ConfigurationManager.AppSettings["Iterations"]);
        public static string sLogFile = (string)ConfigurationManager.AppSettings["LogFile"];
        public static string sCsvLogFile = (string)ConfigurationManager.AppSettings["CsvLogFile"];
        public static int iLogLevel = Convert.ToInt32(ConfigurationManager.AppSettings["LogLevel"]);
        public static bool bVerboseScreen = Convert.ToBoolean(ConfigurationManager.AppSettings["VerboseScreen"]);
        public static bool bOnFailureRetry = Convert.ToBoolean(ConfigurationManager.AppSettings["ConnectionRetry"]);
        public static bool bConnPerIteration = Convert.ToBoolean(ConfigurationManager.AppSettings["ConnectionPerIteration"]);
        public static string sConn = (string)ConfigurationManager.AppSettings["ConnectionString"];
        public static string sTransactionsFile = (string)ConfigurationManager.AppSettings["TransactionsFile"];
        public static int iSleepTime = Convert.ToInt32(ConfigurationManager.AppSettings["SleepTime"]);
        private static List<string> lsValidModes = new List<string> { "pgsql", "mssql" };

        public static Object oTransCounterLock = new Object();
        public static Object oIterationCounterLock = new Object();
        public static Object oTransDurationLock = new Object();
        public static Object oLogLock = new Object();

        public static XmlDocument xmlTransactions = readTransactionsFile(sTransactionsFile);

        private static PerformanceCounter oCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        private static PerformanceCounter oSysCpuCounter = new PerformanceCounter("Processor", "% Privileged Time", "_Total");
        private static PerformanceCounter oUseCpuCounter = new PerformanceCounter("Processor", "% User Time", "_Total");

        public static string timeStamp = "dd/MM/yyyy HH:mm";
        public static string[] aLogArray = new string[10000000];
        public static double[] aTransactionTimeArray = new double[iClientsMax * iIterations * xmlTransactions.SelectNodes("//transaction").Count];        
        
        public static int iLogIndex, iRunningAtMax, iRunningAtMin, iMaxRunning, iCompletedIterations, iCompletedTransactions;
        public static int iCompletedQueries, iRunningClients, iRunningTransactions, iConnectionTimeouts;
        public static int iConnectionCeilingHit, iConnectionFailure, iConnectionRetries, iFailure, iTransactionTimeIndex;
        public static double iMaxTransDuration, iMinTransDuration;
        public static bool bRunning = false;
        
        static void Main(string[] args)
        {
            if (!lsValidModes.Contains(sMode))
            {
                Console.WriteLine("Invalid operating mode '{0}': Supported modes are 'mssql' or 'pgsql'", sMode);
                Environment.Exit(1);
            }
            if (iClients != 0)
            {
                MainWorker(iClients);
            }
            else if (iClientsScale != 0) 
            {
                if (iClientsMax == 0)
                {
                    iClientsMax = iClientsScale;
                }
                for (int i = iClientsScale; i <= iClientsMax; i= i+iClientsScale)
                {
                    MainWorker(i);
                }
            }
        }

        public static void MainWorker(int iClientsRun)
        {
            iClients = iClientsRun;

            Array.Clear(aLogArray, 0, iLogIndex);
            iLogIndex = 0;
            Array.Clear(aTransactionTimeArray, 0, iTransactionTimeIndex);
            iTransactionTimeIndex = 0;
            iMaxTransDuration = 0;
            iMinTransDuration = 100000000000000000;
            iRunningAtMax = 0;
            iRunningAtMin = 0;
            iMaxRunning = 0;
            iCompletedIterations = 0;
            iCompletedTransactions = 0;
            iCompletedQueries = 0;
            iRunningClients = 0;
            iRunningTransactions = 0;
            iConnectionTimeouts = 0;
            iConnectionCeilingHit = 0;
            iConnectionFailure = 0;
            iConnectionRetries = 0;
            iFailure = 0;
            bRunning = false;

            Console.Clear();

            Info("-------------------- Test Start --------------------", iLogLevel, false);
            Info("Server Description: " + sServerDescription, iLogLevel, false);
            Info("Database load tester.", iLogLevel, true);
            Info("Iterations per client = " + iIterations, iLogLevel, true);
            Info("launching " + iClients + " clients...", iLogLevel, true);

            Thread[] workerThreads = new Thread[iClients];
            
            bRunning = true;
            Thread infoThread = new Thread(new ThreadStart(InfoScreen));
            infoThread.Start();

            for (int i = 0; i < workerThreads.Length; i++)
            {
                workerThreads[i] = new Thread(new ParameterizedThreadStart(ClientWorker));
                workerThreads[i].Start(i);
                iRunningClients++;
            }

            for (int i = 0; i < workerThreads.Length; i++)
            {
                workerThreads[i].Join();
                iRunningClients--;
            }

            bRunning = false;
            infoThread.Join();

            Info("-------------------- Test Complete --------------------", iLogLevel, false);
            LogWriter();
        }

        public static void InfoScreen()
        {
            DateTime startTime = DateTime.Now;
            DateTime currTime = DateTime.Now;
            TimeSpan duration = currTime - startTime;

            double iMeanTransactionDuration = 0;
            double iStandatdDeviation = 0;

            float fCpuAll = 0;
            float fCpuSys = 0;
            float fCpuUse = 0;
            int iSamples = 0;

            if (!bVerboseScreen)
            {
                while (bRunning)
                {
                    fCpuAll = fCpuAll + oCpuCounter.NextValue();
                    fCpuSys = fCpuSys + oSysCpuCounter.NextValue();
                    fCpuUse = fCpuUse + oUseCpuCounter.NextValue();
                    iSamples++;

                    currTime = DateTime.Now;
                    duration = currTime - startTime;

                    Console.SetCursorPosition(0, 4);
                    Console.WriteLine("Running Clients                        {0}                            ", iRunningClients);
                    Console.SetCursorPosition(0, 5);
                    Console.WriteLine("Running Transactions                   {0} : {1} max                  ", iRunningTransactions, iMaxRunning);

                    Console.SetCursorPosition(0, 7);
                    Console.WriteLine("Completed Queries                      {0} : {1} qps                  ", iCompletedQueries, (iCompletedQueries / (Convert.ToInt32(duration.TotalSeconds) + 1)));
                    Console.SetCursorPosition(0, 8);
                    Console.WriteLine("Completed Transactions                 {0} : {1} tps                  ", iCompletedTransactions, (iCompletedTransactions / (Convert.ToInt32(duration.TotalSeconds) + 1)));
                    Console.SetCursorPosition(0, 9);
                    Console.WriteLine("Completed Iterations                   {0} / {1}                      ", iCompletedIterations, (iIterations * iClients));
                    
                    Console.SetCursorPosition(0, 11);
                    Console.WriteLine("Maximum Transaction+Read Time          {0} ms : {1} Running           ", iMaxTransDuration, iRunningAtMax);
                    Console.SetCursorPosition(0, 12);
                    Console.WriteLine("Minimum Transaction+Read Time          {0} ms : {1} Running           ", iMinTransDuration, iRunningAtMin);

                    Console.SetCursorPosition(0, 14);
                    Console.WriteLine("Connection Timeout/Ceiling Hit/Failure {0} / {1} / {2}                ", iConnectionTimeouts, iConnectionCeilingHit, iConnectionFailure);
                    Console.SetCursorPosition(0, 15);
                    Console.WriteLine("Other Failure                          {0}                            ", iFailure);
                    Console.SetCursorPosition(0, 16);
                    Console.WriteLine("Connection Retries                     {0}                            ", iConnectionRetries);
                    Console.SetCursorPosition(0, 17);
                    Console.WriteLine("Average Client CPU All/User/Sys        {0}% / {1}% / {2}%             ", Math.Round((fCpuAll / iSamples), 2), Math.Round((fCpuUse / iSamples), 2), Math.Round((fCpuSys / iSamples), 2));

                    Console.SetCursorPosition(0, 23);
                    Console.WriteLine("{0} s                                                                 ", (Convert.ToInt32(duration.TotalSeconds) + 1));
                    Thread.Sleep(500);
                }
            }

            if (!bRunning)
            {
                Thread.Sleep(500);

                Console.SetCursorPosition(0, 4);
                Console.WriteLine("Running Clients                        {0}                            ", iRunningClients);
                Console.SetCursorPosition(0, 5);
                Console.WriteLine("Running Transactions                   {0} : {1} max                  ", iRunningTransactions, iMaxRunning);

                Console.SetCursorPosition(0, 7);
                Console.WriteLine("Completed Queries                      {0} : {1} qps                  ", iCompletedQueries, (iCompletedQueries / (Convert.ToInt32(duration.TotalSeconds) + 1)));
                Console.SetCursorPosition(0, 8);
                Console.WriteLine("Completed Transactions                 {0} : {1} tps                  ", iCompletedTransactions, (iCompletedTransactions / (Convert.ToInt32(duration.TotalSeconds) + 1)));
                Console.SetCursorPosition(0, 9);
                Console.WriteLine("Completed Iterations                   {0} / {1}                      ", iCompletedIterations, (iIterations * iClients));

                Console.SetCursorPosition(0, 11);
                Console.WriteLine("Maximum Transaction+Read Time          {0} ms : {1} Running           ", iMaxTransDuration, iRunningAtMax);
                Console.SetCursorPosition(0, 12);
                Console.WriteLine("Minimum Transaction+Read Time          {0} ms : {1} Running           ", iMinTransDuration, iRunningAtMin);

                Console.SetCursorPosition(0, 14);
                Console.WriteLine("Connection Timeout/Ceiling Hit/Failure {0} / {1} / {2}                ", iConnectionTimeouts, iConnectionCeilingHit, iConnectionFailure);
                Console.SetCursorPosition(0, 15);
                Console.WriteLine("Other Failure                          {0}                            ", iFailure);
                Console.SetCursorPosition(0, 16);
                Console.WriteLine("Connection Retries                     {0}                            ", iConnectionRetries);
                Console.SetCursorPosition(0, 17);
                Console.WriteLine("Average Client CPU All/User/Sys        {0}% / {1}% / {2}%             ", Math.Round((fCpuAll / iSamples),2), Math.Round((fCpuUse / iSamples),2), Math.Round((fCpuSys / iSamples),2));

                Console.SetCursorPosition(0, 23);
                Console.WriteLine("{0} s                                                                 ", (Convert.ToInt32(duration.TotalSeconds) + 1));

                Info("==========================================================", iLogLevel, bVerboseScreen);
                Info("     Running Clients                        " + iRunningClients, iLogLevel, bVerboseScreen);
                Info("     Running Transactions                   " + iRunningTransactions, iLogLevel, bVerboseScreen);
                Info("     Completed Queries                      " + iCompletedQueries + " : " + (iCompletedQueries / (Convert.ToInt32(duration.TotalSeconds) + 1)) + " qps", iLogLevel, bVerboseScreen);
                Info("     Completed Transactions                 " + iCompletedTransactions + " : " + (iCompletedTransactions / (Convert.ToInt32(duration.TotalSeconds) + 1)) + " tps", iLogLevel, bVerboseScreen);
                Info("     Completed Iterations                   " + iCompletedIterations + " / " + (iIterations * iClients), iLogLevel, bVerboseScreen);
                Info("     Maximum Transaction+Read Time          " + iMaxTransDuration + " ms", iLogLevel, bVerboseScreen);
                Info("     Minimum Transaction+Read Time          " + iMinTransDuration + " ms", iLogLevel, bVerboseScreen);
                Info("     Connection Timeout/Ceiling Hit/Failure " + iConnectionTimeouts + " / " + iConnectionCeilingHit + " / " + iConnectionFailure, iLogLevel, bVerboseScreen);
                Info("     Other Failure                          " + iFailure, iLogLevel, bVerboseScreen);
                Info("     Connection Retries                     " + iConnectionRetries, iLogLevel, bVerboseScreen);
                Info("     Total Time                             " + Convert.ToInt32(duration.TotalSeconds) + 1, iLogLevel, bVerboseScreen);
                Info("     Max Concurrent Transactions            " + iMaxRunning, iLogLevel, bVerboseScreen);
                Info("     Average Client CPU All/User/Sys        " + Math.Round((fCpuAll / iSamples), 2) + "% " + Math.Round((fCpuUse / iSamples), 2) + "% " + Math.Round((fCpuSys / iSamples), 2) + "%", iLogLevel, bVerboseScreen);
                Info("==========================================================", iLogLevel, bVerboseScreen);

                iMeanTransactionDuration = 0;
                iStandatdDeviation = 0;

                for (int i = 0; i < iTransactionTimeIndex; i++)
                {
                    iMeanTransactionDuration = (aTransactionTimeArray[i] + iMeanTransactionDuration);
                }
                iMeanTransactionDuration = (iMeanTransactionDuration / iTransactionTimeIndex);

                for (int i = 0; i < iTransactionTimeIndex; i++)
                {

                    iStandatdDeviation = (Math.Pow((aTransactionTimeArray[i] - iMeanTransactionDuration), 2) + iStandatdDeviation);
                }
                iStandatdDeviation = Math.Sqrt(iStandatdDeviation / iTransactionTimeIndex);

                CsvLogWriter("\"" + sServerDescription + "\"," + iClients + "," + iIterations + "," + iMaxTransDuration + "," + iMinTransDuration + "," + iRunningAtMax + "," + iRunningAtMin + "," + iMeanTransactionDuration + "," + iStandatdDeviation + "," + (iCompletedTransactions / (Convert.ToInt32(duration.TotalSeconds) + 1)) + "," + (iCompletedQueries / (Convert.ToInt32(duration.TotalSeconds) + 1)) + "," + iMaxRunning + "," + (Convert.ToInt32(duration.TotalSeconds) + 1) + "," + (fCpuAll / iSamples));
            }
        }

        public static void ClientWorker(object iThread)
        {
            
                Info("Starting client thread " + iThread.ToString(), iLogLevel, bVerboseScreen);

                Random random = new Random();
                int iTransOn = 0;
                int iSqlOn = 0;
                string sSql = "";
                int iRandomStmt = 0;
                int iRandomTran = 0;
                SqlConnection msconn = null;
                NpgsqlConnection conn = null;
                if (!bConnPerIteration)
                {
                    if (sMode == "pgsql") {
                        conn = new NpgsqlConnection(sConn);
                        conn.Open();
                    }
                    else if (sMode == "mssql")
                    {
                        msconn = new SqlConnection(sConn);
                        msconn.Open();
                    }
                }

                for (int i = 0; i < iIterations; i++)
                {
                    try
                    {
                            DateTime startTime = DateTime.Now;
                            if (bConnPerIteration)
                            {
                                if (sMode == "pgsql")
                                {
                                    conn = new NpgsqlConnection(sConn);
                                    conn.Open();
                                }
                                else if (sMode == "mssql")
                                {
                                    msconn = new SqlConnection(sConn);
                                    msconn.Open();
                                }
                            }

                            iTransOn = 0;
                            foreach (XmlNode node in xmlTransactions.SelectNodes("//transaction"))
                            {
                                iTransOn++;
                                XmlNode randomtransaction = node.Attributes["random"];
                                if (randomtransaction != null && node.Attributes["random"].Value.Trim().ToLower() == "true")
                                {
                                    iRandomTran = random.Next(0, 2);
                                }

                            if (iRandomTran == 0)
                            {
                                    NpgsqlTransaction tran = null;
                                    SqlTransaction mstran = null;

                                    if (sMode == "pgsql")
                                    {
                                        tran = conn.BeginTransaction();
                                    }
                                    else if (sMode == "mssql")
                                    {
                                         mstran = msconn.BeginTransaction();
                                    }

                                    DateTime beginTime = DateTime.Now;

                                    lock (oTransCounterLock)
                                    {
                                        iRunningTransactions++;
                                    }

                                    iSqlOn = 0;
                                    foreach (XmlNode subnode in node.SelectNodes("//sql"))
                                    {
                                        iSqlOn++;
                                        XmlNode randomsql = subnode.Attributes["random"];
                                        if (randomsql != null && subnode.Attributes["random"].Value.Trim().ToLower() == "true")
                                        {
                                            iRandomStmt = random.Next(0, 2);
                                        }

                                        if (iRandomStmt == 0)
                                        {
                                            sSql = subnode.InnerText;

                                            if (sMode == "pgsql")
                                            {
                                                NpgsqlCommand command = new NpgsqlCommand(sSql.Replace("#client_id#", iThread.ToString()), conn, tran);
                                                NpgsqlDataReader dr = command.ExecuteReader();

                                                iCompletedQueries++;
                                                while (dr.Read())
                                                {
                                                    //Just pull the data back into our dataset (but we don't care about it)
                                                }

                                                dr.Close();
                                            }
                                            else if (sMode == "mssql")
                                            {
                                                SqlCommand command = new SqlCommand(sSql.Replace("#client_id#", iThread.ToString()), msconn, mstran);
                                                SqlDataReader dr = command.ExecuteReader();

                                                iCompletedQueries++;
                                                while (dr.Read())
                                                {
                                                    //Just pull the data back into our dataset (but we don't care about it)
                                                }

                                                dr.Close();
                                            }

                                            if (iLogLevel > 1)
                                            {
                                                Info("Client " + iThread.ToString() + " Transaction = " + iTransOn + " Statement = " + iSqlOn + " SQL = \"" + sSql + "\" Running", iLogLevel, bVerboseScreen);
                                            }
                                        }
                                        else
                                        {
                                            if (iLogLevel > 1)
                                            {
                                                Info("Client " + iThread.ToString() + " Transaction = " + iTransOn + " Statement = " + iSqlOn + " Skipping as random statement = true", iLogLevel, bVerboseScreen);
                                            }
                                        }
                                    }


                                    if (sMode == "pgsql")
                                    {
                                        tran.Commit();
                                    }
                                    else if (sMode == "mssql")
                                    {
                                        mstran.Commit();
                                    }

                                    DateTime commitTime = DateTime.Now;
                                    TimeSpan transactionDuration = commitTime - beginTime;

                                    if (iRunningClients == iClients)
                                    {

                                        if (transactionDuration.TotalMilliseconds > iMaxTransDuration)
                                        {
                                            lock (oTransDurationLock)
                                            {
                                                iMaxTransDuration = transactionDuration.TotalMilliseconds;
                                                iRunningAtMax = iRunningTransactions;
                                            }
                                        }
                                        if (transactionDuration.TotalMilliseconds < iMinTransDuration)
                                        {
                                            lock (oTransDurationLock)
                                            {
                                                iMinTransDuration = transactionDuration.TotalMilliseconds;
                                                iRunningAtMin = iRunningTransactions;
                                            }
                                        }
                                    }
                                    lock (oTransCounterLock)
                                    {
                                        if (iRunningTransactions > iMaxRunning)
                                        {
                                            iMaxRunning = iRunningTransactions;
                                        }
                                        iCompletedTransactions++;
                                        aTransactionTimeArray[iTransactionTimeIndex] = transactionDuration.TotalMilliseconds;
                                        iTransactionTimeIndex++;
                                        iRunningTransactions--;
                                    }
                                }
                                else
                                {
                                    if (iLogLevel > 1)
                                    {
                                        Info("Client " + iThread.ToString() + " Transaction = " + iTransOn + " Skipping as random transaction = true", iLogLevel, bVerboseScreen);
                                    }
                                }
                            }

                            lock (oIterationCounterLock)
                            {
                                iCompletedIterations++;
                            }

                            if (bConnPerIteration)
                            {
                                if (sMode == "pgsql")
                                {
                                    conn.Close();
                                }
                                else if (sMode == "mssql")
                                {
                                    msconn.Close();
                                }
                            }
                            
                            DateTime stopTime = DateTime.Now;
                            TimeSpan duration = stopTime - startTime;
                            Info("Client " + iThread.ToString() + " Iteration " + i.ToString() + " Iteration Time " + duration.TotalMilliseconds + " ms (including application read)", iLogLevel, bVerboseScreen);

                    }
                    catch (Exception e)
                    {
                        if (String.Compare(e.Message, 0, "A timeout has occured", 0, 21) == 0)
                        {
                            iConnectionTimeouts++;
                        }
                        else if (String.Compare(e.Message, 0, "Failed to establish a connection", 0, 32) == 0)
                        {
                            iConnectionFailure++;
                        }
                        else if ((e.Message == "ERROR: 08P01: no more connections allowed") || (e.Message == "FATAL: 53300: connection limit exceeded for non-superusers") || (e.Message == "FATAL: 53300: sorry, too many clients already"))
                        {
                            iConnectionCeilingHit++;
                        }
                        else
                        {
                            iFailure++;
                        }
                        Info("Client " + iThread.ToString() + " Iteration " + i.ToString() + " FAILURE " + e.Message + " TARGET " + e.TargetSite + " INFO = " + e.ToString(), iLogLevel, bVerboseScreen);
                        if (bOnFailureRetry)
                        {
                            i--;
                            iConnectionRetries++;
                            continue;
                        }

                    }
                    if (iSleepTime > 0)
                    {
                        Thread.Sleep(iSleepTime);
                    }
                }
                if (!bConnPerIteration)
                {
                    conn.Close();
                }
        }

        public static void Info(string sInfoText, int iInfoLevel, bool bScreen)
        {
            if (iInfoLevel >= 1)
            {
                AddLog(sInfoText);      
            }
            if (bScreen)
            {
                Console.WriteLine(sInfoText);
            }
        }

        public static void AddLog(string sLogText)
        {
            DateTime currTime = DateTime.Now;

            if (sLogText.Length != 0)
            {
                lock (oLogLock)
                {
                    aLogArray[iLogIndex] = currTime.ToString(timeStamp) + " : " + sLogText;
                    iLogIndex++;
                }
            }
        }

        public static void LogWriter()
        {
            if ((sLogFile != "") && (iLogLevel >=1))
            {
                FileStream fs = new FileStream(sLogFile, FileMode.OpenOrCreate, FileAccess.Write);
                StreamWriter m_streamWriter = new StreamWriter(fs);
                m_streamWriter.BaseStream.Seek(0, SeekOrigin.End);

                for (int i = 0; i < iLogIndex; i++)
                {
                    m_streamWriter.WriteLine(aLogArray[i]);
                }

                m_streamWriter.Flush();
                m_streamWriter.Close();
            }
        }

        public static void CsvLogWriter(string sLogText)
        {
            bool bWriteHeader = false;

            if (sCsvLogFile != "")
            {
                if (!File.Exists(sCsvLogFile))
                {
                    bWriteHeader = true;
                }

                FileStream fs = new FileStream(sCsvLogFile, FileMode.OpenOrCreate, FileAccess.Write);
                StreamWriter m_streamWriter = new StreamWriter(fs);
                m_streamWriter.BaseStream.Seek(0, SeekOrigin.End);

                if (bWriteHeader)
                {
                    m_streamWriter.WriteLine("Server name, Clients, Iterations/client, Max transaction duration (ms), Min transaction duration (ms), Concurrent transactions at max duration, Concurrent transactions at min duration, Mean transaction duration (ms), Transaction duration standard deviation (ms), tps, qps, Max concurrent transactions, Total time (s), Avg CPU Time %");
                }
                m_streamWriter.WriteLine(sLogText);

                m_streamWriter.Flush();
                m_streamWriter.Close();
            }
        }

        public static XmlDocument readTransactionsFile(string sTransactionsFile)
        {
            DataSet ds = new DataSet();
            XmlDocument transfile = new XmlDocument();
            try
            {
                transfile.Load(sTransactionsFile);
            }
            catch (Exception e)
            {
                Info("Error reading transaction file " + e.ToString(), iLogLevel, bVerboseScreen);
            }
            return transfile;
        }
    }
       
}
