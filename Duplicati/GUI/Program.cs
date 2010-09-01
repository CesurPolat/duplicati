#region Disclaimer / License
// Copyright (C) 2010, Kenneth Skovhede
// http://www.hexad.dk, opensource@hexad.dk
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
// 
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
// 
#endregion
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Data.LightDatamodel;
using System.Drawing;
using Duplicati.Datamodel;

namespace Duplicati.GUI
{
    static class Program
    {
        /// <summary>
        /// The name of the environment variable that holds the path to the data folder used by Duplicati
        /// </summary>
        public const string DATAFOLDER_ENV_NAME = "DUPLICATI_HOME";

        /// <summary>
        /// Gets the folder where Duplicati data is stored
        /// </summary>
        public static string DATAFOLDER { get { return Library.Core.Utility.AppendDirSeperator(Environment.ExpandEnvironmentVariables("%" + "DUPLICATI_HOME" + "%").TrimStart('"').TrimEnd('"')); } } 

        /// <summary>
        /// This is the only access to the database
        /// </summary>
        public static IDataFetcherWithRelations DataConnection;

        /// <summary>
        /// This is the lock to be used before manipulating the shared resources
        /// </summary>
        public static object MainLock = new object();

        /// <summary>
        /// This is the scheduling thread
        /// </summary>
        public static Scheduler Scheduler;
        
        /// <summary>
        /// This is the working thread
        /// </summary>
        public static WorkerThread<IDuplicityTask> WorkThread;

        /// <summary>
        /// The path to the file that contains the current database
        /// </summary>
        public static string DatabasePath;

        /// <summary>
        /// The actual runner, do not call directly. Only used for events.
        /// </summary>
        public static DuplicatiRunner Runner;

        /// <summary>
        /// The controller interface for pause/resume and throttle options
        /// </summary>
        public static LiveControls LiveControl;

        /// <summary>
        /// The main form that contains the tray icon
        /// </summary>
        public static MainForm DisplayHelper;

        /// <summary>
        /// The single instance keeper
        /// </summary>
        public static SingleInstance SingleInstance;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            //Set the %DUPLICATI_HOME% env variable, if it is not already set
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DUPLICATI_HOME")))
            {
#if DEBUG
                //debug mode uses a lock file located in the app folder
                Environment.SetEnvironmentVariable("DUPLICATI_HOME", System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
#else
                bool portableMode = false;
                if (args != null)
                    foreach (string s in args)
                        if (s.Equals("--portable-mode", StringComparison.InvariantCultureIgnoreCase))
                            portableMode = true;

                if (portableMode)
                {
                    //Portable mode uses a data folder in the application home dir
                    Environment.SetEnvironmentVariable("DUPLICATI_HOME", System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "data"));
                }
                else
                {
                    //Normal release mode uses the systems "Application Data" folder
                    Environment.SetEnvironmentVariable("DUPLICATI_HOME", System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Application.ProductName));
                }
#endif
            }

            try
            {
                try
                {
                    //This will also create Program.DATAFOLDER if it does not exist
                    SingleInstance = new SingleInstance(Application.ProductName, Program.DATAFOLDER);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(Strings.Program.StartupFailure, ex.ToString()), Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!SingleInstance.IsFirstInstance)
                {
                    //Linux shows this output
                    Console.WriteLine(Strings.Program.AnotherInstanceDetected);
                    return;
                }

                DatabasePath = System.IO.Path.Combine(Program.DATAFOLDER, "Duplicati.sqlite");
                Version sqliteVersion = new Version((string)SQLiteLoader.SQLiteConnectionType.GetProperty("SQLiteVersion").GetValue(null, null));
                if (sqliteVersion < new Version(3, 6, 3))
                {
                    //The official Mono SQLite provider is also broken with less than 3.6.3
                    MessageBox.Show(string.Format(Strings.Program.WrongSQLiteVersion, sqliteVersion, "3.6.3"), Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                System.Data.IDbConnection con = (System.Data.IDbConnection)Activator.CreateInstance(SQLiteLoader.SQLiteConnectionType);

                try
                {
                    if (!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(DatabasePath)))
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath));

                    //This also opens the db for us :)
                    DatabaseUpgrader.UpgradeDatebase(con, DatabasePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(Strings.Program.DatabaseOpenError, ex.Message), Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                DataConnection = new DataFetcherWithRelations(new SQLiteDataProvider(con));

                if (!string.IsNullOrEmpty(new Datamodel.ApplicationSettings(DataConnection).DisplayLanguage))
                    try
                    {
                        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(new Datamodel.ApplicationSettings(DataConnection).DisplayLanguage);
                        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(new Datamodel.ApplicationSettings(DataConnection).DisplayLanguage);
                    }
                    catch(Exception ex)
                    {
                        MessageBox.Show(string.Format(Strings.Program.LanguageSelectionError, ex.Message), Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        //This is non-fatal, just keep running with system default language
                    }

#if DEBUG
                //Log various information in the logfile
                string logfile = System.IO.Path.Combine(Application.StartupPath, "Duplicati.debug.log");
                if (System.IO.File.Exists(logfile))
                    System.IO.File.Delete(logfile);
                
                Duplicati.Library.Logging.Log.LogLevel = Duplicati.Library.Logging.LogMessageType.Profiling;
                Duplicati.Library.Logging.Log.CurrentLog = new Duplicati.Library.Logging.StreamLog(logfile);
#endif

                LiveControl = new LiveControls(new ApplicationSettings(DataConnection));
                LiveControl.StateChanged += new EventHandler(LiveControl_StateChanged);
                LiveControl.ThreadPriorityChanged += new EventHandler(LiveControl_ThreadPriorityChanged);
                LiveControl.ThrottleSpeedChanged += new EventHandler(LiveControl_ThrottleSpeedChanged);

                Runner = new DuplicatiRunner();
                WorkThread = new WorkerThread<IDuplicityTask>(new WorkerThread<IDuplicityTask>.ProcessItemDelegate(Runner.ExecuteTask), LiveControl.State == LiveControls.LiveControlState.Paused);
                Scheduler = new Scheduler(DataConnection, WorkThread, MainLock);

                DataConnection.AfterDataConnection += new DataConnectionEventHandler(DataConnection_AfterDataConnection);

                DisplayHelper = new MainForm();
                DisplayHelper.InitialArguments = args;

                Application.Run(DisplayHelper);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Strings.Program.SeriousError, ex.ToString()), Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }


            if (Scheduler != null)
                Scheduler.Terminate(true);
            if (WorkThread != null)
                WorkThread.Terminate(true);
            if (SingleInstance != null)
                SingleInstance.Dispose();

#if DEBUG
            using(Duplicati.Library.Logging.Log.CurrentLog as Duplicati.Library.Logging.StreamLog)
                Duplicati.Library.Logging.Log.CurrentLog = null;
#endif
        }

        /// <summary>
        /// Handles a change in the LiveControl and updates the Runner
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void LiveControl_ThreadPriorityChanged(object sender, EventArgs e)
        {
            if (LiveControl.ThreadPriority == null)
                Runner.UnsetThreadPriority();
            else
                Runner.SetThreadPriority(LiveControl.ThreadPriority.Value);
        }

        /// <summary>
        /// Handles a change in the LiveControl and updates the Runner
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void LiveControl_ThrottleSpeedChanged(object sender, EventArgs e)
        {
            if (LiveControl.DownloadLimit == null)
                Runner.SetDownloadLimit(null);
            else
                Runner.SetDownloadLimit(LiveControl.DownloadLimit.Value.ToString() + "b");

            if (LiveControl.UploadLimit == null)
                Runner.SetUploadLimit(null);
            else
                Runner.SetUploadLimit(LiveControl.UploadLimit.Value.ToString() + "b");
        }

        /// <summary>
        /// This event handler updates the trayicon menu with the current state of the runner.
        /// </summary>
        static void LiveControl_StateChanged(object sender, EventArgs e)
        {
            switch (LiveControl.State)
            {
                case LiveControls.LiveControlState.Paused:
                    WorkThread.Pause();
                    Runner.Pause();
                    break;
                case LiveControls.LiveControlState.Running:
                    WorkThread.Resume();
                    Runner.Resume();
                    break;
            }
        }


        private static void DataConnection_AfterDataConnection(object sender, DataActions action)
        {
            if (action == DataActions.Insert || action == DataActions.Update)
                Scheduler.Reschedule();
        }

       
        /// <summary>
        /// Returns a localized name for a task type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static string LocalizeTaskType(DuplicityTaskType type)
        {
            switch (type)
            {
                case DuplicityTaskType.FullBackup:
                    return Strings.TaskType.FullBackup;
                case DuplicityTaskType.IncrementalBackup:
                    return Strings.TaskType.IncrementalBackup;
                case DuplicityTaskType.ListActualFiles:
                    return Strings.TaskType.ListActualFiles;
                case DuplicityTaskType.ListBackupEntries:
                    return Strings.TaskType.ListBackupEntries;
                case DuplicityTaskType.ListBackups:
                    return Strings.TaskType.ListBackups;
                case DuplicityTaskType.ListFiles:
                    return Strings.TaskType.ListFiles;
                case DuplicityTaskType.RemoveAllButNFull:
                    return Strings.TaskType.RemoveAllButNFull;
                case DuplicityTaskType.RemoveOlderThan:
                    return Strings.TaskType.RemoveOlderThan;
                case DuplicityTaskType.Restore:
                    return Strings.TaskType.Restore;
                case DuplicityTaskType.RestoreSetup:
                    return Strings.TaskType.RestoreSetup;
                default:
                    return type.ToString();
            }
        }
    }
}