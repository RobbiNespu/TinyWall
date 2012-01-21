﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO.Pipes;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using PKSoft.WindowsFirewall;

namespace PKSoft
{
    internal class TinyWallService : ServiceBase
    {
        internal readonly static string[] ServiceDependencies = new string[]
        {
            "mpssvc",
            "eventlog",
            "Winmgmt"
        };

        internal const string SERVICE_NAME = "TinyWall";
        internal const string SERVICE_DISPLAY_NAME = "TinyWall Service";

        private RequestQueue Q;

        private Thread FirewallWorkerThread;
        private Timer MinuteTimer;
        private DateTime LastControllerCommandTime = DateTime.Now;
        
        private WindowsFirewall.Policy Firewall;
        private WindowsFirewall.Rules FwRules;
        private List<RuleDef> MasterRules;

        private ProfileType Profile;
        private string ProfileDisplayName;
        private FirewallMode Mode = FirewallMode.Normal;
        private bool UninstallRequested = false;
        private FileLocker FileLocker;

        private void SetModeBlockAll()
        {
            // Disable standard Windows exceptions unconditionally.
            // Since the default packet action is blocking (and because
            // we do not add any exeptions ourself), this will
            // practically disable all traffic.
            FwRules.DisableAllRules();

            // Do we want to let local traffic through?
            if (SettingsManager.CurrentZone.AllowLocalSubnet)
            {
                RuleDef def = new RuleDef("Allow local subnet", PacketAction.Allow, RuleDirection.InOut, Protocol.Any);
                def.RemoteAddresses = "LocalSubnet";
                List<Rule> ruleset = new List<Rule>();
                def.ConstructRule(ruleset, "+"+AppExceptionSettings.GenerateID()); //TODO
                FwRules.EnableRule(ruleset);
            }
        }

        private void SetModeAllowOut()
        {
            if (this.Mode != FirewallMode.Normal)
                SetModeNormal();

            // Add rule to explicitly allow outgoing connections
            RuleDef r = new RuleDef("Allow all outbound", PacketAction.Allow, RuleDirection.Out, Protocol.Any);
            List<Rule> ruleset = new List<Rule>();
            r.ConstructRule(ruleset, "+"+AppExceptionSettings.GenerateID());//TODO
            FwRules.EnableRule(ruleset);
        }

        private void SetModeDisabled()
        {
            // Add rule to explicitly allow everything
            RuleDef r = new RuleDef("Allow everything", PacketAction.Allow, RuleDirection.InOut, Protocol.Any);
            List<Rule> ruleset = new List<Rule>();
            r.ConstructRule(ruleset, "+"+AppExceptionSettings.GenerateID());//TODO
            FwRules.EnableRule(ruleset);

            // Disable blocking rules
            foreach (Rule rule in FwRules)
            {
                if (rule.Action == PacketAction.Block)
                    rule.Enabled = false;
            }
        }

        private void SetModeNormal()
        {
            // We will collect all our rules into this list
            List<Rule> exRules = new List<Rule>(128);

            #region Add machine exceptions
            for (int i = 0; i < SettingsManager.CurrentZone.SpecialExceptions.Length; ++i)
            {
                try
                {   //This try-catch will prevent errors if an exception profile string is invalid
                    ProfileAssoc app = GlobalInstances.ProfileMan.GetApplication(SettingsManager.CurrentZone.SpecialExceptions[i]);
                    AppExceptionSettings ex = app.ToExceptionSetting();
                    ex.AppID = "+" + ex.AppID; //TODO
                    GetRulesForException(ex, exRules);
                }
                catch { }
            }

            #endregion

            #region Add application exceptions
            for (int i = 0; i < SettingsManager.CurrentZone.AppExceptions.Length; ++i)  // for each application
            {
                try
                {   //This try-catch will prevent errors if an exception profile string is invalid

                    AppExceptionSettings ex = SettingsManager.CurrentZone.AppExceptions[i];
                    GetRulesForException(ex, exRules);
                }
                catch { }
            }
            #endregion

            if (SettingsManager.CurrentZone.BlockMalwarePorts)
            {
                string appid = "+" + AppExceptionSettings.GenerateID(); //TODO AppExceptionSettings.GenerateID();
                Profile profileMalwarePortBlock = GlobalInstances.ProfileMan.GetProfile("Malware port block");
                if (profileMalwarePortBlock != null)
                {
                    foreach (RuleDef rule in profileMalwarePortBlock.Rules)
                        rule.ConstructRule(exRules, appid);
                }
            }

            if (SettingsManager.CurrentZone.AllowLocalSubnet)
            {
                RuleDef def = new RuleDef("Allow local subnet", PacketAction.Allow, RuleDirection.InOut, Protocol.Any);
                def.RemoteAddresses = "LocalSubnet";
                def.ConstructRule(exRules, "+"+AppExceptionSettings.GenerateID()); //TODO
            }

            //
            // After all this preparation, let us finally apply the settings to the firewall
            //

            ResetWindowsFirewall();

            // Allow or not standard Windows firewall rules
            if (!SettingsManager.CurrentZone.EnableDefaultWindowsRules)
                FwRules.DisableAllRules();

            // Enable rules
            FwRules.Add(exRules);
        }

        private void GetRulesForException(AppExceptionSettings ex, List<Rule> ruleset)
        {
            if (string.IsNullOrEmpty(ex.AppID))
            {
                ex.RegenerateID();
                ++SettingsManager.Changeset;
            }

            for (int i = 0; i < ex.Profiles.Length; ++i)    // for each profile
            {
                // Get the rules for this profile
                Profile p = GlobalInstances.ProfileMan.GetProfile(ex.Profiles[i]);
                if (p == null)
                    continue;

                for (int j = 0; j < p.Rules.Length; ++j)    // for each rule in profile
                {
                    try
                    {
                        RuleDef def = p.Rules[j];
                        def.Application = ex.ExecutablePath;
                        def.ServiceName = ex.ServiceName;
                        def.ConstructRule(ruleset, ex.AppID);
                    }
                    catch
                    {
                        // Do not let the service crash if a rule cannot be constructed 
#if DEBUG
                        throw;
#endif
                    }
                }
            }

            try
            {
                // Add extra ports
                if (!string.IsNullOrEmpty(ex.OpenPortListenLocalTCP))
                {
                    Rule r = new Rule(ex.AppID + " Extra Tcp Listen Ports", string.Empty, ProfileType.All, RuleDirection.In, PacketAction.Allow, Protocol.TCP);
                    r.LocalPorts = ex.OpenPortListenLocalTCP;
                    r.ApplicationName = ex.ExecutablePath;
                    ruleset.Add(r);
                }
                if (!string.IsNullOrEmpty(ex.OpenPortListenLocalUDP))
                {
                    Rule r = new Rule(ex.AppID + " Extra Udp Listen Ports", string.Empty, ProfileType.All, RuleDirection.In, PacketAction.Allow, Protocol.UDP);
                    r.LocalPorts = ex.OpenPortListenLocalUDP;
                    r.ApplicationName = ex.ExecutablePath;
                    ruleset.Add(r);
                }
                if (!string.IsNullOrEmpty(ex.OpenPortOutboundRemoteTCP))
                {
                    Rule r = new Rule(ex.AppID + " Extra Tcp Outbound Ports", string.Empty, ProfileType.All, RuleDirection.Out, PacketAction.Allow, Protocol.TCP);
                    r.RemotePorts = ex.OpenPortOutboundRemoteTCP;
                    r.ApplicationName = ex.ExecutablePath;
                    ruleset.Add(r);
                }
                if (!string.IsNullOrEmpty(ex.OpenPortOutboundRemoteUDP))
                {
                    Rule r = new Rule(ex.AppID + " Extra Udp Outbound Ports", string.Empty, ProfileType.All, RuleDirection.Out, PacketAction.Allow, Protocol.UDP);
                    r.RemotePorts = ex.OpenPortOutboundRemoteUDP;
                    r.ApplicationName = ex.ExecutablePath;
                    ruleset.Add(r);
                }
            }
            catch (Exception)
            {
                this.EventLog.WriteEntry("Error applying custom port rules for " + ex.ExecutableName + ".", EventLogEntryType.Error);
            }
        }

        private void ResetWindowsFirewall()
        {
            // Set general firewall settings
            Firewall.ResetFirewall();
            Firewall.Enabled = true;
            Firewall.DefaultInboundAction = PacketAction.Block;
            Firewall.DefaultOutboundAction = PacketAction.Block;
            Firewall.BlockAllInboundTraffic = false;
            Firewall.NotificationsDisabled = true;
            FwRules = Firewall.GetRules(false);
            MasterRules = new List<RuleDef>();
        }

        // This method completely reinitializes (reloads) the current firewall settings,
        // reapplying them all.
        private void InitFirewall()
        {
            Firewall = new Policy();
            Profile = Firewall.CurrentProfileTypes;
            if ((int)(Profile & ProfileType.Private) != 0)
                ProfileDisplayName = "Private";
            else if ((int)(Profile & ProfileType.Domain) != 0)
                ProfileDisplayName = "Domain";
            else if ((int)(Profile & ProfileType.Public) != 0)
                ProfileDisplayName = "Public";
            else
                throw new InvalidOperationException("Unexpected network profile value.");

            try
            {
                GlobalInstances.ProfileMan = ProfileManager.Load(ProfileManager.DBPath);
            }
            catch
            {
                GlobalInstances.ProfileMan = new ProfileManager();
            }

            SettingsManager.GlobalConfig = MachineSettings.Load();
            SettingsManager.CurrentZone = ZoneSettings.Load(ProfileDisplayName);

            switch (this.Mode)
            {
                case FirewallMode.Normal:
                    SetModeNormal();
                    break;
                case FirewallMode.AllowOutgoing:
                    SetModeAllowOut();
                    break;
                case FirewallMode.BlockAll:
                    SetModeBlockAll();
                    break;
                case FirewallMode.Disabled:
                    SetModeDisabled();
                    break;
            }

            string HostsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            if (SettingsManager.GlobalConfig.LockHostsFile)
                FileLocker.LockFile(HostsFilePath, FileAccess.Read, FileShare.Read);
            else
                FileLocker.UnlockFile(HostsFilePath);

            if (MinuteTimer != null)
            {
                using (WaitHandle wh = new AutoResetEvent(false))
                {
                    MinuteTimer.Dispose(wh);
                    wh.WaitOne();
                }
                MinuteTimer = null;
            }

            MinuteTimer = new Timer(new TimerCallback(TimerCallback), null, 0, 60000);
        }

        public void TimerCallback(Object state)
        {
            if (!Q.HasRequest(TinyWallCommands.CHECK_SCHEDULED_RULES))
                Q.Enqueue(new ReqResp(new Message(TinyWallCommands.CHECK_SCHEDULED_RULES)));

            if (DateTime.Now - LastControllerCommandTime > TimeSpan.FromMinutes(10))
            {
                Q.Enqueue(new ReqResp(new Message(TinyWallCommands.LOCK)));
            }
        }

        private Message ProcessCmd(Message req)
        {
            switch (req.Command)
            {
                case TinyWallCommands.PING:
                    {
                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.MODE_SWITCH:
                    {
                        this.Mode = (FirewallMode)req.Arguments[0];
                        switch (Mode)
                        {
                            case FirewallMode.AllowOutgoing:
                                SetModeAllowOut();
                                break;
                            case FirewallMode.BlockAll:
                                SetModeBlockAll();
                                break;
                            case FirewallMode.Disabled:
                                SetModeDisabled();
                                break;
                            case FirewallMode.Normal:
                                SetModeNormal();
                                break;
                            default:
                                return new Message(TinyWallCommands.RESPONSE_ERROR);
                        }

                        if (Firewall.LocalPolicyModifyState == LocalPolicyState.GP_OVERRRIDE)
                            return new Message(TinyWallCommands.RESPONSE_WARNING);
                        else
                            return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.PUT_SETTINGS:
                    {
                        SettingsManager.GlobalConfig = (MachineSettings)req.Arguments[0];
                        SettingsManager.GlobalConfig.Save();

                        // This roundabout way is to prevent overwriting the wrong zone if the controller is sending us
                        // data from a zone that is not the current one.
                        ZoneSettings zone = (ZoneSettings)req.Arguments[1];
                        zone.Save();
                        SettingsManager.CurrentZone = zone;

                        InitFirewall();
                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.GET_SETTINGS:
                    {
                        // Get changeset of client
                        int changeset = (int)req.Arguments[0];

                        // If our changeset is different, send new settings to client
                        if (changeset != SettingsManager.Changeset)
                        {
                            return new Message(TinyWallCommands.RESPONSE_OK,
                                SettingsManager.Changeset,
                                SettingsManager.GlobalConfig,
                                SettingsManager.CurrentZone
                                );
                        }
                        else
                        {
                            // Our changeset is the same, so do not send settings again
                            return new Message(TinyWallCommands.RESPONSE_OK, SettingsManager.Changeset);
                        }
                    }
                case TinyWallCommands.RELOAD:
                    {
                        InitFirewall();
                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.GET_PROFILE:
                    {
                        return new Message(TinyWallCommands.RESPONSE_OK, SettingsManager.CurrentZone.ZoneName);
                    }
                case TinyWallCommands.UNLOCK:
                    {
                        if (SettingsManager.ServiceConfig.Unlock((string)req.Arguments[0]))
                            return new Message(TinyWallCommands.RESPONSE_OK);
                        else
                            return new Message(TinyWallCommands.RESPONSE_ERROR);
                    }
                case TinyWallCommands.LOCK:
                    {
                        SettingsManager.ServiceConfig.Locked = true;
                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.GET_LOCK_STATE:
                    {
                        return new Message(TinyWallCommands.RESPONSE_OK, SettingsManager.ServiceConfig.HasPassword ? 1 : 0, SettingsManager.ServiceConfig.Locked ? 1 : 0);
                    }
                case TinyWallCommands.SET_PASSPHRASE:
                    {
                        FileLocker.UnlockFile(ServiceSettings.PasswordFilePath);
                        try
                        {
                            SettingsManager.ServiceConfig.SetPass((string)req.Arguments[0]);
                            return new Message(TinyWallCommands.RESPONSE_OK);
                        }
                        catch
                        {
                            return new Message(TinyWallCommands.RESPONSE_ERROR);
                        }
                        finally
                        {
                            FileLocker.LockFile(ServiceSettings.PasswordFilePath, FileAccess.Read, FileShare.Read);
                        }
                    }
                case TinyWallCommands.GET_MODE:
                    {
                        return new Message(TinyWallCommands.RESPONSE_OK, this.Mode);
                    }
                case TinyWallCommands.STOP_DISABLE:
                    {
                        UninstallRequested = true;

                        // Disable automatic start of service
                        try
                        {
                            using (ScmWrapper.ServiceControlManager scm = new ScmWrapper.ServiceControlManager())
                            {
                                scm.SetStartupMode(TinyWallService.SERVICE_NAME, ServiceStartMode.Automatic);
                                scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, false);
                            }
                        }
                        catch { }

                        // Disable automatic start of controller
                        Utils.RunAtStartup("TinyWall Controller", null);

                        // Reset Windows Firewall to its default state
                        Firewall.ResetFirewall();

                        // Stop service execution
                        Environment.Exit(0);

                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.NEW_EXCEPTION:
                    {
                        // Add new exception
                        {
                            AppExceptionSettings ex = (AppExceptionSettings)req.Arguments[0];
                            SettingsManager.CurrentZone.AppExceptions = Utils.ArrayAddItem(SettingsManager.CurrentZone.AppExceptions, ex);
                            SettingsManager.CurrentZone.Normalize();
                            SettingsManager.CurrentZone.Save();

                            // Apply exception
                            if (this.Mode == FirewallMode.Normal)
                            {
                                List<Rule> ruleset = new List<Rule>();
                                GetRulesForException(ex, ruleset);
                                FwRules.EnableRule(ruleset);
                            }
                        }
                        
                        // Remove dead rules
                        for(int i = FwRules.Count-1; i >= 0; --i)   // for each Windows Firewall rule
                        {
                            Rule rule = FwRules[i];

                            // Skip if this is not a TinyWall rule
                            if (!rule.Name.StartsWith("[TW"))
                                continue;

                            // Extract ID of exception
                            string id = rule.Name;
                            int id_end = id.IndexOf(']');
                            id = id.Substring(0, id_end + 1);

                            // Delete Firewall rule if no corresponding exception is found

                            bool found = false;
                            foreach (AppExceptionSettings ex in SettingsManager.CurrentZone.AppExceptions)
                            {
                                if (ex.AppID == id)
                                {
                                    found = true;
                                    break;
                                }
                            }

                            if (!found)
                                FwRules.RemoveAt(i);
                        }
                        
                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                case TinyWallCommands.CHECK_SCHEDULED_RULES:
                    {
                        bool needsSave = false;

                        // Check all exceptions if any one has expired
                        AppExceptionSettings[] exs = SettingsManager.CurrentZone.AppExceptions;
                        for (int i = 0; i < exs.Length; ++i)
                        {
                            // Timer values above zero are the number of minutes to stay active
                            if ((int)exs[i].Timer <= 0)
                                continue;

                            // Did this one expire?
                            if (exs[i].CreationDate.AddMinutes((double)exs[i].Timer) <= DateTime.Now)
                            {
                                // Remove rule
                                string appid = exs[i].AppID;

                                // Search for the exception identifier in the rule name.
                                // Remove rules with a match.
                                for (int j = FwRules.Count-1; j >= 0; --j)
                                {
                                    if (FwRules[j].Name.Contains(appid))
                                        FwRules.RemoveAt(j);
                                }

                                // Remove exception
                                exs = Utils.ArrayRemoveItem(exs, exs[i]);
                                needsSave = true;
                            }
                        }

                        if (needsSave)
                        {
                            SettingsManager.CurrentZone.AppExceptions = exs;
                            SettingsManager.CurrentZone.Save();
                            ++SettingsManager.Changeset;
                        }

                        return new Message(TinyWallCommands.RESPONSE_OK);
                    }
                default:
                    {
                        return new Message(TinyWallCommands.RESPONSE_ERROR);
                    }
            }
        }

        // Entry point for thread that actually issues commands to Windows Firewall.
        // Only one thread (this one) is allowed to issue them.
        private void FirewallWorkerMethod()
        {
            EventLogWatcher WFEventWatcher = null;
            try
            {
                try
                {
                    WFEventWatcher = new EventLogWatcher("Microsoft-Windows-Windows Firewall With Advanced Security/Firewall");
                    WFEventWatcher.EventRecordWritten += new EventHandler<EventRecordWrittenEventArgs>(WFEventWatcher_EventRecordWritten);
                    WFEventWatcher.Enabled = true;
                }
                catch
                {
                    WFEventWatcher = null;
                    EventLog.WriteEntry("Unable to listen for firewall events. Windows Firewall monitoring will be turned off.");
                }

                while (true)
                {
                    ReqResp req = Q.Dequeue();
                    req.Response = ProcessCmd(req.Request);
                    req.SignalResponse();
                }
            }
            finally
            {
                if (WFEventWatcher != null)
                    WFEventWatcher.Dispose();
            }
        }

        // Entry point for thread that listens to commands from the controller application.
        private Message PipeServerDataReceived(Message req)
        {
            if (((int)req.Command > 2047) && SettingsManager.ServiceConfig.Locked)
            {
                // Notify that we need to be unlocked first
                return new Message(TinyWallCommands.RESPONSE_LOCKED, 1);
            }
            else
            {
                LastControllerCommandTime = DateTime.Now;

                // Process and wait for response
                ReqResp qItem = new ReqResp(req);
                Q.Enqueue(qItem);
                Message resp = qItem.GetResponse();

                // Send response back to pipe
                return resp;
            }
        }

        private void WFEventWatcher_EventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            int propidx = -1;
            switch (e.EventRecord.Id)
            {
                case 2003:     // firewall setting changed
                    {
                        propidx = 7;
                        break;
                    }
                case 2004:     // rule added
                    {
                        propidx = 22;
                        break;
                    }
                case 2005:     // rule changed
                    {
                        propidx = 22;
                        break;
                    }
                case 2006:     // rule deleted
                    {
                        propidx = 3;
                        break;
                    }
                case 2010:     // network interface changed profile
                    {
                        ++SettingsManager.Changeset;
                        if (!Q.HasRequest(TinyWallCommands.RELOAD))
                        {
                            EventLog.WriteEntry("Reloading firewall configuration because a network interface changed profile.");
                            Q.Enqueue(new ReqResp(new Message(TinyWallCommands.RELOAD)));
                        }
                        break;
                    }
                case 2032:     // firewall has been reset
                    {
                        propidx = 1;
                        break;
                    }
                default:
                    break;
            }

            if (propidx != -1)
            {
                string TWpath = Utils.ExecutablePath;
                string EVpath = (string)e.EventRecord.Properties[propidx].Value;
                if (!EVpath.Equals(TWpath, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (!Q.HasRequest(TinyWallCommands.RELOAD))
                    {
                        EventLog.WriteEntry("Reloading firewall configuration because " + EVpath + " has modified it.");
                        Q.Enqueue(new ReqResp(new Message(TinyWallCommands.RELOAD)));
                    }
                }
            }
        }

        internal TinyWallService()
        {
            this.ServiceName = SERVICE_NAME;

            this.CanShutdown = true;
#if DEBUG
            this.CanStop = true;
#else
            this.CanStop = false;
#endif
            if (!EventLog.SourceExists("TinyWallService"))
                EventLog.CreateEventSource("TinyWallService", null);
            this.EventLog.Source = "TinyWallService";
        }


        // Entry point for Windows service.
        protected override void OnStart(string[] args)
        {
            // Register an unhandled exception handler
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            try
            {
                EventLog.WriteEntry("TinyWall service starting up.");

                FileLocker = new PKSoft.FileLocker();
                FileLocker.LockFile(ProfileManager.DBPath, FileAccess.Read, FileShare.Read);
                FileLocker.LockFile(ServiceSettings.PasswordFilePath, FileAccess.Read, FileShare.Read);

                // Lock configuration if we have a password
                SettingsManager.Changeset = 0;
                SettingsManager.ServiceConfig = new ServiceSettings();
                if (SettingsManager.ServiceConfig.HasPassword)
                    SettingsManager.ServiceConfig.Locked = true;

                // Set normal mode on stratup
                this.Mode = FirewallMode.Normal;

                // Issue load command
                Q = new RequestQueue();
                Q.Enqueue(new ReqResp(new Message(TinyWallCommands.RELOAD)));

                // Start thread that is going to control Windows Firewall
                FirewallWorkerThread = new Thread(new ThreadStart(FirewallWorkerMethod));
                FirewallWorkerThread.IsBackground = true;
                FirewallWorkerThread.Start();

                // Fire up pipe
                GlobalInstances.CommunicationMan = new PipeCom("TinyWallController", new PipeDataReceived(PipeServerDataReceived));

#if !DEBUG
                // Messing with the SCM in this method would hang us, so start it parallel
                ThreadPool.QueueUserWorkItem((WaitCallback)delegate(object state)
                {
                    try
                    {
                        TinyWallDoctor.EnsureHealth();
                    }
                    catch { }
                });
#endif
            }
            catch (Exception e)
            {
                CurrentDomain_UnhandledException(null, new UnhandledExceptionEventArgs(e, false));
                throw;
            }
        }

        void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Utils.LogCrash(e.ExceptionObject as Exception);
        }

        // Executed when service is stopped manually.
        protected override void OnStop()
        {
            Shutdown();
        }

        private void Shutdown()
        {
            bool needsSave = false;

            // Check all exceptions if any one has expired
            {
                AppExceptionSettings[] exs = SettingsManager.CurrentZone.AppExceptions;
                for (int i = 0; i < exs.Length; ++i)
                {
                    // "Permanent" exceptions do not expire, skip them
                    if (exs[i].Timer == AppExceptionTimer.Permanent)
                        continue;

                    // Did this one expire?
                    if (exs[i].Timer == AppExceptionTimer.Until_Reboot)
                    {
                        // Remove exception
                        exs = Utils.ArrayRemoveItem(exs, exs[i]);
                        needsSave = true;
                    }
                }

                if (needsSave)
                {
                    SettingsManager.CurrentZone.AppExceptions = exs;
                    SettingsManager.CurrentZone.Save();
                }
            }

            FirewallWorkerThread.Abort();
            SettingsManager.GlobalConfig.Save();
            SettingsManager.CurrentZone.Save();
            FileLocker.UnlockAll();

            if (!UninstallRequested)
            {
                try
                {
                    TinyWallDoctor.EnsureHealth();
                }
                catch { }
            }
        }

        // Executed on computer shutdown.
        protected override void OnShutdown()
        {
            Shutdown();
        }

#if DEBUG
        internal void Start(string[] args)
        {
            this.OnStart(args);
        }
#endif
    }
}
